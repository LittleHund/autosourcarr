"""Per-instance, local-only requirements layered over automatic matching safeguards."""
import hashlib
import json
import math
import threading
from weakref import WeakKeyDictionary

from .matcher import best_automatic_title_score, best_description_score, name_similarity, extract_alt_titles
from . import tag_store

_lock = threading.RLock()
_cache = WeakKeyDictionary()
_generations = WeakKeyDictionary()
CHECKS = {'title': .94, 'summary': .01, 'author': .01, 'tags': .01}


def presets():
    # Return fresh drafts. Presets use the same validated rule contract as edits;
    # they never replace the base identity/edition safeguards or write policies.
    return [
        {'id': 'title', 'name': 'High title similarity',
         'description': 'Require native titles to be at least 97% similar.',
         'rule': {'enabled': True, 'checks': {'title': .97}}},
        {'id': 'creators', 'name': 'Titles + authors / artists',
         'description': 'Require 97% title and 90% author / artist similarity. Missing creators block automatic matching.',
         'rule': {'enabled': True, 'checks': {'title': .97, 'author': .90}}},
        {'id': 'summary', 'name': 'Titles + summaries',
         'description': 'Require 97% title and 90% summary similarity. Missing summaries block automatic matching.',
         'rule': {'enabled': True, 'checks': {'title': .97, 'summary': .90}}},
        {'id': 'gallery', 'name': 'Titles + informative tags',
         'description': 'Require 97% title similarity and 70% informative tag overlap. Missing informative tags block automatic matching.',
         'rule': {'enabled': True, 'checks': {'title': .97, 'tags': .70}}},
        {'id': 'strict', 'name': 'Strict metadata',
         'description': 'Require 99% title, 95% summary and 95% author / artist similarity. All three must be available.',
         'rule': {'enabled': True, 'checks': {'title': .99, 'summary': .95, 'author': .95}}},
    ]


class PolicyBlocked(ValueError):
    pass


def invalidate(database):
    with _lock:
        _cache.pop(database, None)
        _generations[database] = _generations.get(database, 0) + 1


def rules(database):
    # Never hold the cache mutex while acquiring SQLite's lock: matching can be
    # called from an existing catalogue transaction. A generation prevents a
    # concurrent edit from repopulating this cache with pre-edit values.
    while True:
        with _lock:
            if database in _cache:
                return _cache[database]
            generation = _generations.get(database, 0)
        with database.catalog() as c:
            rows = c.execute('SELECT scope,subject,direction,rule_json FROM matching_policies').fetchall()
        values = {(r['scope'], r['subject'], r['direction']): json.loads(r['rule_json']) for r in rows}
        with _lock:
            if generation == _generations.get(database, 0):
                _cache[database] = values
                return values


def revision(values):
    return hashlib.sha256(json.dumps(sorted((list(k), v) for k, v in values.items()), sort_keys=True).encode()).hexdigest()


def validate(rule):
    if rule is None:
        return None
    if not isinstance(rule, dict) or set(rule) != {'enabled', 'checks'} or type(rule['enabled']) is not bool:
        raise ValueError('Use enabled and a set of required checks.')
    checks = rule['checks']
    if not isinstance(checks, dict) or set(checks) - CHECKS.keys():
        raise ValueError('Unknown matching check.')
    if rule['enabled'] and not checks:
        raise ValueError('Choose at least one required check, or restore inherited rules.')
    for key, value in checks.items():
        if type(value) not in (int, float) or not math.isfinite(value) or not CHECKS[key] <= value <= 1:
            raise ValueError(f'{key.title()} threshold must be between {CHECKS[key]:.0%} and 100%.')
    return {'enabled': rule['enabled'], 'checks': dict(checks)}


def effective(values, sources, sid, direction):
    own = values.get(('source', str(sid), direction))
    if own is not None:
        return own, f'source {sid}'
    gid = (sources.get(str(sid)) or {}).get('group_id')
    inherited = values.get(('group', str(gid), direction)) if gid is not None else None
    return inherited, f'group {gid}'


def source_ids(payloads):
    return {str(p.get('sourceId') or p.get('source_id')) for p in payloads if p.get('sourceId') or p.get('source_id')}


def titles(payloads):
    values = []
    for p in payloads:
        gallery = p.get('_gallery_metadata') or {}
        values.append(str(gallery.get('work_title') or gallery.get('title') or p.get('title') or ''))
        values.extend(extract_alt_titles(str(p.get('description') or '')))
    return [v for v in values if v]


def metrics(left, right, fallback, *, required_checks=None):
    # A score from another source cannot fill a missing pair-specific check.
    required = set(CHECKS if required_checks is None else required_checks)
    result = {key: None for key in CHECKS}
    if left and right:
        if 'title' in required:
            lt, rt = titles(left), titles(right)
            result['title'] = best_automatic_title_score(lt, rt)[0] if lt and rt else None
        if 'summary' in required:
            ld = [str(p.get('description') or '') for p in left if p.get('description')]
            rd = [str(p.get('description') or '') for p in right if p.get('description')]
            result['summary'] = max((best_description_score(d, rd) for d in ld), default=None) if rd else None
        if 'author' in required:
            la = [str(p.get('author') or p.get('artist') or '') for p in left if p.get('author') or p.get('artist')]
            ra = [str(p.get('author') or p.get('artist') or '') for p in right if p.get('author') or p.get('artist')]
            result['author'] = max((name_similarity(a, b) for a in la for b in ra), default=None)
    if 'tags' in required:
        def tags(p):
            # Keep the same canonical/manual tag equivalences as gallery matching.
            values = tag_store._payload_tags(p)
            return tag_store.canonical_keys([t for t in values if t.get('namespace') not in {'language','category','type','uploader'}])
        lt, rt = [tags(p) for p in left], [tags(p) for p in right]
        result['tags'] = max((len(a & b) / len(a | b) for a in lt for b in rt if a and b), default=None)
    return result


def apply(database, sources, evidence, left_payloads, right_payloads, *, left_ids=None, right_ids=None, values=None):
    values = rules(database) if values is None else values
    if not values or not evidence.get('eligible'):
        return evidence
    lids = set(left_ids or source_ids(left_payloads)) or {''}
    rids = set(right_ids or source_ids(right_payloads)) or {''}
    required = []
    for l in sorted(lids):
        for r in sorted(rids):
            direction = 'internal' if l and l == r else 'external'
            pair = []
            for sid in dict.fromkeys((l, r)):
                rule, origin = effective(values, sources, sid, direction)
                if rule is not None:
                    pair.append((rule, origin))
            if pair:
                required.append((l, r, direction, pair))
    if not required:
        return evidence
    fallback = {'title': evidence.get('title_score', evidence.get('best_title_similarity')),
                'summary': evidence.get('summary_similarity'), 'author': evidence.get('author_similarity')}
    checks = []
    for l, r, direction, pair in required:
        lp = [p for p in left_payloads if str(p.get('sourceId') or p.get('source_id') or '') == l]
        rp = [p for p in right_payloads if str(p.get('sourceId') or p.get('source_id') or '') == r]
        scores = None
        for rule, origin in pair:
            label = f'{direction} matching · {origin}'
            if not rule['enabled']:
                return {**evidence, 'eligible': False, 'policy_blocked': True, 'reason': label+' is disabled', 'policy_checks': checks}
            if scores is None:
                needed = {key for candidate, _ in pair if candidate['enabled'] for key in candidate['checks']}
                scores = metrics(lp, rp, fallback, required_checks=needed)
            for key, threshold in rule['checks'].items():
                score = scores.get(key)
                passed = score is not None and score + 1e-12 >= threshold
                checks.append({'scope': origin, 'direction': direction, 'check': key, 'threshold': threshold, 'score': score, 'passed': passed})
                if not passed:
                    reason = f'{label}: {key} '+('evidence missing' if score is None else f'{score:.0%} is below {threshold:.0%}')
                    return {**evidence, 'eligible': False, 'policy_blocked': True, 'reason': reason, 'policy_checks': checks}
    return {**evidence, 'policy_checks': checks, 'policy_revision': revision(values)}
