"""Local, advisory review of records that may belong to different works.

This never changes matching gates or source bindings. Missing metadata is not a
conflict, and a review applies only to the exact source evidence inspected.
"""
import hashlib
import json
import re
from collections import OrderedDict, defaultdict
from threading import Lock
from itertools import combinations

from .matcher import clean_summary, description_similarity, extract_alt_titles, name_similarity, title_color_variant, title_similarity


def fingerprint(records):
    fields = ('source_id', 'source_manga_id', 'title', 'description', 'author', 'media_mode', 'color_mode', 'lang')
    evidence = [tuple(record.get(field, '') for field in fields) for record in records]
    return hashlib.sha256(json.dumps(['mixed-v1', sorted(evidence)], ensure_ascii=False).encode()).hexdigest()


_assessments = OrderedDict()
_assessment_lock = Lock()


def pair_reasons(title_a, summary_a, author_a, media_a, color_a, lang_a,
                 title_b, summary_b, author_b, media_b, color_b, lang_b):
    reasons = []
    if media_a != 'unknown' and media_b != 'unknown' and media_a != media_b:
        reasons.append('Different publication formats: manga / webtoon')
    if color_a != 'unknown' and color_b != 'unknown' and color_a != color_b:
        reasons.append('Different color editions')
    # Native alternate titles may explain different translations of the same work.
    titles_a = [title_a, *extract_alt_titles(summary_a)]
    titles_b = [title_b, *extract_alt_titles(summary_b)]
    title_score = max((title_similarity(a, b) for a in titles_a[:20] for b in titles_b[:20] if a and b), default=1)
    left, right = clean_summary(summary_a), clean_summary(summary_b)
    # Do not treat short, absent, or cross-language summaries as contrary evidence.
    if lang_a == lang_b == 'en' and len(left) >= 100 and len(right) >= 100:
        stop = set('this that with from while their there they them then when where what which into about after before have has been will would could should she her his him and the for are was were not but who how new one all can only some through every more than each life world story'.split())
        words_a = set(re.findall(r'[a-z]{3,}', left.casefold())) - stop
        words_b = set(re.findall(r'[a-z]{3,}', right.casefold())) - stop
        overlap = len(words_a & words_b) / max(1, min(len(words_a), len(words_b)))
        if min(len(words_a), len(words_b)) < 10 or overlap >= .10:
            return tuple(reasons)
        summary_score = description_similarity(left, right)
        if title_score < .55 and summary_score < .50:
            reasons.append('Source titles and detailed summaries describe different works')
        elif title_score < .80 and summary_score < .50 and author_a and author_b and name_similarity(author_a, author_b) < .40:
            reasons.append('Different authors, titles and detailed summaries')
    return tuple(reasons)


def _assess(records):
    evidence = []
    examined = 0
    for left, right in combinations(records, 2):
        if examined >= 512:
            return evidence, True
        examined += 1
        fields = ('title', 'description', 'author', 'media_mode', 'color_mode', 'lang')
        reasons = pair_reasons(*(left[field] for field in fields), *(right[field] for field in fields))
        if reasons:
            evidence.append({'left': left['key'], 'right': right['key'], 'reasons': list(reasons)})
        if len(evidence) >= 12:
            return evidence, True
    return evidence, False


def assess(records, signature):
    # Cache only the small assessment, keyed by exact record evidence. Do not
    # keep full descriptions in a pair cache which thrashes on large catalogs.
    with _assessment_lock:
        cached = _assessments.get(signature)
        if cached is not None:
            _assessments.move_to_end(signature)
    if cached is None:
        evidence, partial = _assess(records)
        cached = (tuple((item['left'], item['right'], tuple(item['reasons'])) for item in evidence), partial)
        with _assessment_lock:
            _assessments[signature] = cached
            _assessments.move_to_end(signature)
            while len(_assessments) > 20000:
                _assessments.popitem(last=False)
    return ([{'left': left, 'right': right, 'reasons': list(reasons)} for left, right, reasons in cached[0]], cached[1])


def snapshot(database, service, entry_id=None):
    columns = 'entry_id,source_id,source_manga_id,source_title,description,author,artist,genre,status,thumbnail_url,source_manga_url,real_url,metadata_json,initialized,locked,match_type'
    condition = 'AND r.entry_id=?' if entry_id is not None else ''
    with database.catalog() as connection:
        rows = database.rows(connection.execute(f'''
            SELECT r.*, e.preferred_title, COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name,
                   COALESCE(sc.lang,'') source_lang, p.color_mode sampled_color, p.confidence color_confidence
            FROM (SELECT {columns} FROM source_matches WHERE active=1 UNION ALL
                  SELECT {columns} FROM source_variants WHERE active=1) r
            JOIN entries e ON e.id=r.entry_id
            LEFT JOIN source_config sc ON sc.source_id=r.source_id
            LEFT JOIN source_color_profiles p ON p.source_id=r.source_id AND p.source_manga_id=r.source_manga_id
            WHERE 1=1 {condition} ORDER BY r.entry_id,r.source_id,r.source_manga_id
        ''', (int(entry_id),) if entry_id is not None else ()))
        reviewed = {row['fingerprint']: row['reviewed_at'] for row in database.rows(connection.execute(
            'SELECT fingerprint,reviewed_at FROM mixed_entry_reviews'))}
    groups = defaultdict(dict)
    titles = {}
    for row in rows:
        eid = int(row['entry_id'])
        titles[eid] = row['preferred_title']
        payload = service._record_payload(row)
        color = title_color_variant(payload['title'])
        if color == 'unknown' and float(row.get('color_confidence') or 0) >= service.mixed_color_confidence():
            color = row.get('sampled_color') or 'unknown'
        key = f"{row['source_id']}:{int(row['source_manga_id'])}"
        groups[eid][key] = {
            'key': key, 'source_id': str(row['source_id']), 'source_manga_id': int(row['source_manga_id']),
            'source_name': row['source_name'] or row['source_id'], 'lang': row['source_lang'],
            'title': str(payload['title']), 'description': str(payload['description']), 'author': str(payload['author']),
            'media_mode': service._candidate_media_mode(payload), 'color_mode': color,
            'locked': bool(row['locked']), 'match_type': row['match_type'] or '',
            'cover_url': f"/api/v1/source-covers/{int(row['source_manga_id'])}",
        }
    result = []
    for eid, values in groups.items():
        records = list(values.values())
        if len(records) < 2:
            continue
        signature = fingerprint(records)
        evidence, partial = assess(records, signature)
        result.append({'entry_id': eid, 'preferred_title': titles[eid], 'cover_url': f'/api/v1/covers/{eid}',
                       'fingerprint': signature, 'records': records, 'record_count': len(records),
                       'evidence': evidence, 'partial': partial, 'reviewed_at': reviewed.get(signature),
                       'state': 'reviewed' if signature in reviewed else 'needs_review' if evidence else 'no_conflict'})
    return result
