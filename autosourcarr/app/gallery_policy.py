"""Per-source gallery family controls; native identity proof remains authoritative."""
import copy
import json
import math
import threading
from weakref import WeakKeyDictionary
from .gallery_metadata import is_gallery_source,_GENERIC
from .matcher import best_automatic_title_score

SCHEMA='''
CREATE TABLE IF NOT EXISTS gallery_policies (
 source_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, policy_json TEXT NOT NULL,
 updated_at TEXT NOT NULL, layout_revision INTEGER NOT NULL DEFAULT 0,
 layout_result_json TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE IF NOT EXISTS gallery_layout_candidates (
 split_id INTEGER PRIMARY KEY, candidate_ids_json TEXT NOT NULL
);
'''
DEFAULT={'profile':'auto','enabled':True,'criteria':[],'title_minimum':.94,'tag_similarity':.5,'tag_overlap':2}
CRITERIA={'artist','circle','parody','language','category','tags'}
_lock=threading.RLock();_cache=WeakKeyDictionary();_generations=WeakKeyDictionary()


def invalidate(database):
    with _lock:
        _cache.pop(database,None);_generations[database]=_generations.get(database,0)+1


def rules(database):
    while True:
        with _lock:
            if database in _cache:return _cache[database]
            generation=_generations.get(database,0)
        with database.catalog() as c:rows=c.execute('SELECT source_id,policy_json FROM gallery_policies').fetchall()
        values={r['source_id']:json.loads(r['policy_json']) for r in rows}
        with _lock:
            if generation==_generations.get(database,0):_cache[database]=values;return values


def effective(database,sid):return rules(database).get(str(sid),DEFAULT)


def source_profile(database,sid,source):
    result=dict(source or {})
    if effective(database,sid)['profile']=='gallery':result['metadata_profile']='gallery'
    return result


def validate(value):
    if not isinstance(value,dict) or set(value)!=set(DEFAULT):raise ValueError('Use the complete gallery policy')
    if not isinstance(value['profile'],str) or value['profile'] not in {'auto','gallery'} or type(value['enabled']) is not bool:raise ValueError('Choose the gallery profile and combining setting')
    fields=value['criteria']
    if not isinstance(fields,list) or len(fields)>len(CRITERIA) or any(not isinstance(f,str) or f not in CRITERIA for f in fields) or len(set(fields))!=len(fields):raise ValueError('Choose supported gallery matching criteria')
    for key,minimum in [('title_minimum',.94),('tag_similarity',.01)]:
        if type(value[key]) not in (int,float) or not math.isfinite(value[key]) or not minimum<=value[key]<=1:raise ValueError(f'{key} must be between {minimum:.0%} and 100%')
    if type(value['tag_overlap']) is not int or not 1<=value['tag_overlap']<=50:raise ValueError('Choose 1 to 50 shared informative tags')
    return {**value,'criteria':sorted(fields)}


def read(database,sid):
    with database.catalog() as c:
        source=c.execute('SELECT source_id,name,lang,base_url FROM source_config WHERE source_id=?',(str(sid),)).fetchone()
        row=c.execute('SELECT * FROM gallery_policies WHERE source_id=?',(str(sid),)).fetchone()
        if not source:
            if not row and not c.execute('SELECT 1 FROM catalogue_source_facts WHERE source_id=? LIMIT 1',(str(sid),)).fetchone():raise KeyError(sid)
            source={'source_id':str(sid),'name':'Shared source '+str(sid),'lang':'not installed','base_url':''}
        combined=c.execute('''SELECT COUNT(*) FROM (SELECT entry_id FROM (
          SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1
          UNION SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1)
          GROUP BY entry_id HAVING COUNT(*)>1 AND SUM(source_id=?)>0)''',(str(sid),)).fetchone()[0]
        combined+=c.execute('''SELECT COUNT(*) FROM (SELECT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id
          WHERE NOT EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=f.entry_id AND m.active=1)
            AND NOT EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=f.entry_id AND v.active=1)
          GROUP BY f.entry_id HAVING COUNT(*)>1 AND SUM(f.source_id=?)>0)''',(str(sid),)).fetchone()[0]
    return {'source':dict(source),'revision':row['revision'] if row else 0,
            'policy':json.loads(row['policy_json']) if row else copy.deepcopy(DEFAULT),
            'detected':is_gallery_source(dict(source)),'combined_entries':combined,
            'layout_pending':bool(row and row['layout_revision']!=row['revision']),
            'layout_result':json.loads(row['layout_result_json']) if row else {},
            'note':'Selected criteria add requirements to native title, creator, edition and animation safeguards. All selected checks must pass. Turning combining off separates gallery records; chapter pages and served IDs are retained.'}


def save(database,service,sid,value,revision):
    from .db import now_iso
    value=validate(value)
    with service._catalog_maintenance_lock,database.catalog() as c:
        c.execute('BEGIN IMMEDIATE');current=read(database,sid)
        if current['revision']!=revision:raise ValueError('Gallery policy changed; reload before saving')
        c.execute('''INSERT INTO gallery_policies(source_id,revision,policy_json,updated_at)
          VALUES(?,?,?,?) ON CONFLICT(source_id) DO UPDATE SET revision=excluded.revision,
          policy_json=excluded.policy_json,updated_at=excluded.updated_at''',(str(sid),revision+1,json.dumps(value),now_iso()))
    invalidate(database);service._invalidate_duplicate_snapshot()
    return read(database,sid)


def pending(database):
    with database.catalog() as c:return [r[0] for r in c.execute('SELECT source_id FROM gallery_policies WHERE layout_revision<>revision')]


def record_layout(database,sid,revision,result):
    with database.catalog() as c:
        c.execute('UPDATE gallery_policies SET layout_revision=?,layout_result_json=? WHERE source_id=? AND revision=?',
          (revision,json.dumps(result),str(sid),revision))


def _keys(data,criterion):
    from . import tag_store
    tags=list(data.get('tags') or [])
    if criterion=='tags':
        return tag_store.canonical_keys([t for t in tags if t.get('namespace') not in {'language','category','type','uploader'} and str(t.get('name') or '').casefold() not in _GENERIC])
    names={'artist','author'} if criterion=='artist' else {criterion}
    selected=[{**t,'namespace':'artist' if criterion=='artist' else criterion} for t in tags if t.get('namespace') in names]
    if not selected and data.get(criterion):selected=[{'namespace':criterion,'name':data[criterion]}]
    return tag_store.canonical_keys(selected)


def pair_gate(database,left,right,threshold=.94,*,manual=False):
    """Policies never turn metadata similarity into a native binding proof."""
    failure=lambda reason:{'applies':True,'eligible':False,'score':0.0,'title_score':0.0,'reason':reason}
    values=[effective(database,sid) for sid in {str(d.get('source_id') or '') for d in (left,right)} if sid]
    for value in values:
        if not value['enabled']:return failure('Gallery combining is disabled for this source')
        if manual:continue  # Explicit manual choice still requires combining enabled.
        score,_,_=best_automatic_title_score([str(left.get('work_title') or left.get('title') or '')],[str(right.get('work_title') or right.get('title') or '')])
        if score<max(.94,threshold,value['title_minimum']):return failure('Gallery native work title is below the selected minimum')
        for criterion in value['criteria']:
            a,b=_keys(left,criterion),_keys(right,criterion)
            if not a or not b:return failure('Gallery '+criterion+' evidence is missing')
            overlap=a&b
            if criterion=='tags':
                if len(overlap)<value['tag_overlap'] or len(overlap)/len(a|b)<value['tag_similarity']:return failure('Gallery informative tags do not meet the selected overlap')
            elif not overlap:return failure('Gallery '+criterion+' does not match')
    return None


def grouping_gate(database,payloads):
    """An explicit off choice also blocks a manual/restore path around the setting."""
    seen={};galleries=[]
    for payload in payloads:
        key=(str(payload.get('sourceId') or ''),int(payload.get('id') or 0))
        if key in seen:continue
        seen[key]=True
        metadata=payload.get('_gallery_metadata')
        if metadata:galleries.append(metadata)
    if len(seen)>1:
        for data in galleries:
            if not effective(database,data.get('source_id') or '')['enabled']:raise ValueError('Gallery combining is disabled for this source; enable it before combining records')
