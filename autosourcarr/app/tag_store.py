"""Source-owned tags, conservative aliases and reversible human decisions.

No extra service or database: local catalogue reads never contact source websites.
Tag equivalence supports matching evidence; it never merges entries on its own.
"""
from __future__ import annotations

import asyncio
import heapq
import json
import re
import threading
import unicodedata
from collections import defaultdict
from difflib import SequenceMatcher
from typing import Any

from .db import db, now_iso
from .config import settings

# Deliberately small, unambiguous vocabulary. Fuzzy matches are suggestions only.
_ALIASES = {
    'sci fi': 'science fiction', 'sci-fi': 'science fiction', 'scifi': 'science fiction',
    'full color': 'full colour', 'full-color': 'full colour', 'full-colour': 'full colour',
    'colour': 'color', 'greyscale': 'grayscale',
    'one-shot': 'one shot', 'oneshot': 'one shot',
}
_NAMESPACES = {'artists': 'artist', 'authors': 'author', 'groups': 'circle', 'group': 'circle',
               'languages': 'language', 'characters': 'character', 'parodies': 'parody',
               'genres': 'tag', 'genre': 'tag', 'tags': 'tag', 'misc': 'tag', 'general': 'tag'}
_cache_lock = threading.RLock()
_cache: dict[tuple, dict[str, str]] = {}


def normalize(value: Any) -> str:
    return re.sub(r'\s+', ' ', unicodedata.normalize('NFKC', str(value or '')).casefold().replace('_', ' ')).strip()


def _tag(value: Any) -> dict | None:
    if isinstance(value, dict):
        name = str(value.get('name') or value.get('tag') or '').strip()[:200]
        namespace = normalize(value.get('namespace') or 'tag')[:50]
    else:
        name = str(value or '').strip()[:200]
        namespace = 'tag'
        if ':' in name:
            prefix, suffix = name.split(':', 1)
            if re.fullmatch(r'[a-zA-Z _-]{1,30}', prefix) and suffix.strip():
                namespace, name = normalize(prefix), suffix.strip()
    namespace = _NAMESPACES.get(namespace, namespace)
    if not name or any(ord(c) < 32 for c in name):
        return None
    return {'namespace': namespace, 'name': name, 'normalized': normalize(name)}


def _key(tag: dict) -> str:
    return tag['namespace'] + ':' + normalize(tag['name'])


def _automatic(key: str) -> str:
    namespace, name = key.split(':', 1)
    # Creator names and gender namespaces are never treated as genre synonyms.
    if namespace in {'tag', 'category', 'type'}:
        name = _ALIASES.get(name, name)
    return namespace + ':' + name


def invalidate() -> None:
    with _cache_lock:
        _cache.clear()


def _mapping(rows: list[dict], reviews: list[dict], strict=False) -> dict[str, str]:
    keys = {r['id']: _key(r) for r in rows}
    all_keys = set(keys.values()) | {_automatic(k) for k in keys.values()}
    parent = {k: k for k in all_keys}
    members = {k: {k} for k in all_keys}
    blocked = {frozenset((keys[r['left_id']], keys[r['right_id']])) for r in reviews if r['decision'] == 'different'}

    def root(k):
        while parent[k] != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k

    def union(a, b, manual=False):
        a, b = root(a), root(b)
        if a == b:
            return
        conflict = bool(blocked) and any(frozenset((x, y)) in blocked for x in members[a] for y in members[b])
        if conflict:
            if strict and manual:
                raise ValueError('This would contradict a Separate decision. Clear the conflicting review first.')
            return
        low, high = sorted((a, b))
        parent[high] = low
        members[low] |= members.pop(high)

    for r in reviews:
        if r['decision'] == 'same':
            union(keys[r['left_id']], keys[r['right_id']], True)
    for k in sorted(all_keys):
        union(k, _automatic(k))
    return {k: root(k) for k in all_keys}


def _read_mapping(conn):
    # One statement is one SQLite snapshot even during concurrent reviews.
    joined = conn.execute('''SELECT r.*,l.namespace lns,l.name lname,
        t.namespace rns,t.name rname FROM tag_reviews r
        JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id
        ORDER BY r.left_id,r.right_id''').fetchall()
    rows, reviews = {}, []
    for r in joined:
        rows[r['left_id']] = {'id':r['left_id'],'namespace':r['lns'],'name':r['lname']}
        rows[r['right_id']] = {'id':r['right_id'],'namespace':r['rns'],'name':r['rname']}
        reviews.append(dict(r))
    return _mapping(list(rows.values()), reviews)


def _current_mapping(c=None) -> dict[str, str]:
    cache_key = (id(db), str(settings.catalog_path))
    # A caller-owned transaction may still see the state before a review committed.
    # Its snapshot must never repopulate the process cache after invalidation.
    if c is not None and c.in_transaction:
        return _read_mapping(c)
    with _cache_lock:
        if cache_key in _cache:
            return _cache[cache_key]
        if c is None:
            with db.catalog() as conn:
                if conn.in_transaction:
                    return _read_mapping(conn)
                mapping = _read_mapping(conn)
        else:
            mapping = _read_mapping(c)
        _cache[cache_key] = mapping
        return mapping


def canonical_keys(tags: list[dict]) -> set[str]:
    mapping = _current_mapping()
    result = set()
    for value in tags or []:
        tag = _tag(value)
        if tag:
            key = _key(tag)
            result.add(mapping.get(key, mapping.get(_automatic(key), _automatic(key))))
    return result


def _payload_tags(manga: dict) -> list[dict]:
    from .gallery_metadata import source_tags
    result = []
    gallery = manga.get('_gallery_metadata') or {}
    # Old source rows may store genre as a JSON string; decode before parsing.
    native = dict(manga)
    if isinstance(native.get('genre'), str) and native['genre'].lstrip().startswith('['):
        try: native['genre'] = json.loads(native['genre'])
        except ValueError: pass
    values = list(gallery.get('tags') or []) + source_tags(native)
    seen = set()
    for value in values[:500]:
        tag = _tag(value)
        if tag and _key(tag) not in seen:
            seen.add(_key(tag))
            result.append(tag)
    return result


def sync_source_record(c, source_id, source_manga_id, manga, source_info=None) -> None:
    """Called inside the source-binding transaction; no separate entry copy can drift."""
    from .gallery_metadata import enrich_gallery_payload
    enriched = enrich_gallery_payload(manga, source_info or {})
    tags = _payload_tags(enriched)
    sid, mid = str(source_id), int(source_manga_id)
    # Feed cards omit details. Never erase hydrated tags on a later thin card.
    authoritative = manga.get('initialized') and any(k in manga for k in ('genre', 'tags', 'genres'))
    existing = {(r['namespace'],r['normalized']) for r in c.execute('''SELECT t.namespace,t.normalized
        FROM source_record_tags st JOIN tags t ON t.id=st.tag_id
        WHERE st.source_id=? AND st.source_manga_id=?''',(sid,mid))}
    incoming = {(t['namespace'],t['normalized']) for t in tags}
    changed = incoming != existing if authoritative else not incoming.issubset(existing)
    if changed and authoritative:
        c.execute('DELETE FROM source_record_tags WHERE source_id=? AND source_manga_id=?', (sid, mid))
    if changed and tags:
        for tag in tags:
            c.execute('INSERT OR IGNORE INTO tags(namespace,name,normalized) VALUES(?,?,?)',
                      (tag['namespace'], tag['name'], tag['normalized']))
            tid = c.execute('SELECT id FROM tags WHERE namespace=? AND normalized=?', (tag['namespace'], tag['normalized'])).fetchone()[0]
            c.execute('INSERT OR IGNORE INTO source_record_tags VALUES(?,?,?)', (sid, mid, tid))
    gallery = enriched.get('_gallery_metadata')
    if gallery:
        if not manga.get('initialized'):
            previous = c.execute('SELECT metadata_json FROM gallery_records WHERE source_id=? AND source_manga_id=?', (sid, mid)).fetchone()
            if previous:
                try:
                    old = json.loads(previous[0])
                    gallery = {**old, **{k:v for k,v in gallery.items() if v not in ('',None,[],{})}}
                    combined = {(_tag(t)['namespace'], _tag(t)['normalized']): t
                                for t in (old.get('tags') or []) + (gallery.get('tags') or []) if _tag(t)}
                    gallery['tags'] = list(combined.values())
                except (ValueError,TypeError): pass
        c.execute('INSERT INTO gallery_records VALUES(?,?,?,?) ON CONFLICT(source_id,source_manga_id) DO UPDATE SET metadata_json=excluded.metadata_json,updated_at=excluded.updated_at WHERE gallery_records.metadata_json<>excluded.metadata_json',
                  (sid, mid, json.dumps(gallery, ensure_ascii=False), now_iso()))


_ACTIVE = '''SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1
             UNION SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1'''


def _catalog(c, *, include_aliases=True):
    rows = [dict(r) for r in c.execute('SELECT * FROM tags ORDER BY namespace,normalized,id')]
    mapping = _current_mapping(c)
    groups = defaultdict(list)
    for row in rows:
        key = _key(row)
        groups[mapping.get(key, mapping.get(_automatic(key), _automatic(key)))].append(row)
    by_id = {}
    for group in groups.values():
        canonical = min(group, key=lambda r: r['id'])
        for row in group:
            row['canonical_id'] = canonical['id']
            row['canonical_name'] = canonical['name']
            if include_aliases:
                row['aliases'] = [{k: other[k] for k in ('id','name','namespace')} for other in group if other['id'] != row['id']]
            by_id[row['id']] = row
    return rows, by_id


def _hydrate_aliases(rows, selected):
    # Materialize response aliases only for displayed rows. Building them for
    # every member of a large reviewed group made a page allocate N*(N-1) dicts.
    groups = defaultdict(list)
    for row in rows:
        groups[row['canonical_id']].append(row)
    for row in selected:
        row['aliases'] = [{k: other[k] for k in ('id','name','namespace')}
                          for other in groups[row['canonical_id']] if other['id'] != row['id']]


def list_tags(search='', limit=100, offset=0):
    query = normalize(search)
    limit, offset = max(1,min(int(limit),200)), max(0,int(offset))
    with db.catalog() as c:
        rows, _ = _catalog(c, include_aliases=False)
        filtered = [r for r in rows if not query or query in normalize(r['namespace'] + ' ' + r['name'])]
        total = len(filtered)
        page = filtered[offset:offset+limit]
        _hydrate_aliases(rows, page)
        ids = [r['id'] for r in page]
        placeholders = ','.join('?' for _ in ids) or 'NULL'
        counts = {r['tag_id']: dict(r) for r in c.execute(f'''WITH active AS ({_ACTIVE})
            SELECT t.tag_id,count(DISTINCT t.source_id) source_count,count(DISTINCT a.entry_id) entry_count
            FROM source_record_tags t JOIN active a USING(source_id,source_manga_id)
            WHERE t.tag_id IN ({placeholders}) GROUP BY t.tag_id''', ids)}
    return {'items': [{**r,'source_count': counts.get(r['id'],{}).get('source_count',0),
                       'entry_count': counts.get(r['id'],{}).get('entry_count',0)} for r in page],
            'total': total, 'limit': limit, 'offset': offset}


def suggestions(limit=100):
    with db.catalog() as c:
        rows, _ = _catalog(c, include_aliases=False)
        reviewed = {(r['left_id'],r['right_id']) for r in c.execute('SELECT * FROM tag_reviews')}
    # Sorted neighbourhood instead of quadratic all-pairs scoring.
    def candidates():
        for i, left in enumerate(rows):
            for right in rows[i+1:i+13]:
                if left['namespace'] != right['namespace']:
                    break
                pair = tuple(sorted((left['id'],right['id'])))
                if pair in reviewed or left['canonical_id'] == right['canonical_id']:
                    continue
                score = SequenceMatcher(None,left['normalized'],right['normalized']).ratio()
                if score >= .84:
                    yield {'left': left,'right': right,'score':round(score,3),
                           'reason':'Similar spelling; review before treating these tags as equivalent'}
    # Preserve the complete neighbourhood and stable rounded-score ordering,
    # but retain only the requested best candidates instead of every match.
    selected = heapq.nsmallest(max(1,min(limit,200)), candidates(), key=lambda x: -x['score'])
    displayed = {row['id']: row for item in selected for row in (item['left'], item['right'])}
    _hydrate_aliases(rows, displayed.values())
    return {'items': selected}


def review(left_id: int, right_id: int, decision: str):
    if decision not in {'same','different','clear'} or left_id == right_id:
        raise ValueError('Choose two different tags and Same, Separate, or Clear.')
    left_id,right_id=sorted((int(left_id),int(right_id)))
    with db.catalog() as c:
        c.execute('BEGIN IMMEDIATE')
        found = [dict(r) for r in c.execute('SELECT * FROM tags WHERE id IN (?,?)',(left_id,right_id))]
        if len(found)!=2:
            raise ValueError('One of these tags no longer exists. Refresh the list.')
        if decision == 'same' and found[0]['namespace'] != found[1]['namespace']:
            raise ValueError('Different namespaces describe different metadata roles and cannot be combined.')
        c.execute('DELETE FROM tag_reviews WHERE left_id=? AND right_id=?',(left_id,right_id))
        if decision!='clear':
            c.execute('INSERT INTO tag_reviews VALUES(?,?,?,?)',(left_id,right_id,decision,now_iso()))
        rows=[dict(r) for r in c.execute('SELECT * FROM tags WHERE id IN (SELECT left_id FROM tag_reviews UNION SELECT right_id FROM tag_reviews)')]
        reviews=[dict(r) for r in c.execute('SELECT * FROM tag_reviews ORDER BY left_id,right_id')]
        _mapping(rows,reviews,strict=True)
    invalidate()
    return {'left_id':left_id,'right_id':right_id,'decision':decision}


def reviews():
    with db.catalog() as c:
        result = [{'left':{'id':r['left_id'],'namespace':r['lns'],'name':r['lname']},
                   'right':{'id':r['right_id'],'namespace':r['rns'],'name':r['rname']},
                   'decision':r['decision'],'updated_at':r['updated_at']} for r in c.execute('''
                   SELECT r.*,l.namespace lns,l.name lname,t.namespace rns,t.name rname
                   FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id
                   ORDER BY r.updated_at DESC''')]
    return {'items':result}


def entry_metadata(entry_id):
    with db.catalog() as c:
        if not c.execute('SELECT 1 FROM entries WHERE id=?',(entry_id,)).fetchone():
            raise KeyError('Entry not found')
        tags=[dict(r) for r in c.execute(f'''WITH active AS ({_ACTIVE})
            SELECT DISTINCT t.id,t.name,t.namespace FROM tags t JOIN source_record_tags st ON st.tag_id=t.id
            JOIN active a USING(source_id,source_manga_id) WHERE a.entry_id=? ORDER BY t.namespace,t.name''',(entry_id,))]
        gallery=[]
        for row in c.execute(f'''WITH active AS ({_ACTIVE}) SELECT DISTINCT g.* FROM gallery_records g
            JOIN active a USING(source_id,source_manga_id) WHERE a.entry_id=?''',(entry_id,)):
            try: value=json.loads(row['metadata_json'])
            except (ValueError,TypeError): continue
            gallery.append({**value,'source_id':row['source_id'],'source_manga_id':row['source_manga_id']})
    return {'tags':tags,'gallery':gallery,'metadata_basis':'source' if gallery else 'catalogue'}


def reindex_batch(table: str, after: int, size=100):
    if table not in {'source_matches','source_variants'}:
        raise ValueError('Invalid source record table')
    try:
        with db.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            rows=c.execute(f'''SELECT r.*, s.name source_name,s.base_url,s.lang FROM {table} r
                LEFT JOIN source_config s USING(source_id) WHERE r.id>? AND r.active=1 ORDER BY r.id LIMIT ?''',(after,size)).fetchall()
            for r in rows:
                try: manga=json.loads(r['metadata_json'] or '{}')
                except (ValueError,TypeError): manga={}
                if not isinstance(manga,dict): manga={}
                for key in ('artist','author','genre','description'):
                    if not manga.get(key): manga[key]=r[key]
                manga.setdefault('title',r['source_title'])
                manga.setdefault('initialized',bool(r['initialized']))
                sync_source_record(c,r['source_id'],r['source_manga_id'],manga,
                                   {'name':r['source_name'],'base_url':r['base_url'],'lang':r['lang']})
            return (int(rows[-1]['id']) if rows else after,len(rows))
    finally:
        db.close_current_thread()


def queue_reindex(service, *, startup=False):
    from .async_utils import run_blocking
    from .task_registry import task_registry
    name='__lifespan-tag-reindex__'
    existing=service._refresh_tasks.get(name)
    if existing is not None and not existing.done():
        return {'state':'queued'}
    async def run():
        tid=None
        try:
            if startup:
                await asyncio.sleep(15)
                if await run_blocking(db.setting,'tag_index_version','') == '0.3.9': return
                await service._wait_for_background_idle(quiet_seconds=3)
            tid=task_registry.create('Index source tags',message='Reading saved source metadata; no website requests',
                                     origin='automatic' if startup else 'manual')
            task_registry.bind_runtime(tid, asyncio.current_task())
            task_registry.mark_active(tid)
            count=0
            for table in ('source_matches','source_variants'):
                after=0
                while True:
                    after,n=await run_blocking(reindex_batch,table,after)
                    count+=n
                    if not n: break
                    task_registry.update(tid,current=count,message=f'Indexed {count} saved source records')
                    await asyncio.sleep(.01)
            await run_blocking(db.set_setting,'tag_index_version','0.3.9')
            task_registry.complete(tid,message=f'Indexed {count} saved source records')
        except asyncio.CancelledError:
            if tid: task_registry.cancel(tid,message='Tag indexing cancelled')
            raise
        except Exception as exc:
            if tid: task_registry.fail(tid,exc,message='Source tag indexing failed')
    service.track_startup_task('tag-reindex',run())
    return {'state':'queued'}
