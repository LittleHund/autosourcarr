"""Per-work preferences rank only candidates admitted by existing hard gates."""
import json
from .db import now_iso


DEFAULT={'languages':[],'sources':[],'quality':'default'}


def validate(profile):
    if not isinstance(profile,dict) or set(profile)-set(DEFAULT): raise ValueError('Unknown profile field')
    result={**DEFAULT,**profile}
    for field in ('languages','sources'):
        values=result[field]
        if not isinstance(values,list) or len(values)>32 or any(not isinstance(v,str) or not v or len(v)>100 for v in values):
            raise ValueError('Use at most 32 language or source identifiers')
        result[field]=list(dict.fromkeys(values))
    if result['quality'] not in {'default','quality','complete'}: raise ValueError('Unknown quality preference')
    return result


def read(database,entry_id,*,include_retained=True):
    with database.catalog() as c:
        if not c.execute('SELECT 1 FROM entries WHERE id=?',(entry_id,)).fetchone(): raise KeyError(entry_id)
        row=c.execute('SELECT * FROM serving_profiles WHERE entry_id=?',(entry_id,)).fetchone()
        retained=[{'id':r['id'],'title':r['title'],'profile':json.loads(r['profile_json']) if r['profile_json']!='{}' else None,
                   'monitored':json.loads(r['tracking_json']).get('monitored') if r['tracking_json']!='{}' else None} for r in c.execute(
            "SELECT * FROM entry_preference_history WHERE canonical_entry_id=? ORDER BY id DESC",(entry_id,))] if include_retained else []
    return {'entry_id':entry_id,'profile':json.loads(row['profile_json']) if row else dict(DEFAULT),'revision':row['revision'] if row else 0,'retained':retained}


def save(database,service,entry_id,profile,revision):
    value=validate(profile)
    with service._catalog_maintenance_lock:
        with database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            current=read(database,entry_id)
            if current['revision']!=revision: raise ValueError('Profile changed; reload before saving')
            c.execute('INSERT INTO serving_profiles VALUES(?,?,?,?) ON CONFLICT(entry_id) DO UPDATE SET profile_json=excluded.profile_json,revision=excluded.revision,updated_at=excluded.updated_at',
                      (entry_id,json.dumps(value),revision+1,now_iso()))
    return read(database,entry_id)


def context(database,entry_id):
    try:profile=read(database,entry_id,include_retained=False)['profile']
    except KeyError:return dict(DEFAULT),{}
    if profile==DEFAULT:return profile,{}
    with database.catalog() as c:
        langs={str(r['source_id']):str(r['lang']) for r in c.execute('SELECT source_id,lang FROM source_config')}
    return profile,langs


def rank(database,entry_id,candidates,*,prepared=None):
    if not candidates:return
    profile,langs=prepared if prepared is not None else context(database,entry_id)
    if profile==DEFAULT:return
    max_pages=max((max(0,int(r.get('page_count') or 0)) for r in candidates),default=0)
    # Preferences never create membership or revive rejected candidates. Existing
    # identity/edition/manual source locks have already selected this candidate set.
    for row in candidates:
        score=float(row.get('resolver_score') or 0)
        for field,value,weight in [('languages',langs.get(str(row['source_id']),''),1000),('sources',str(row['source_id']),100)]:
            order=profile[field]
            if value in order: score+=weight*(len(order)-order.index(value))/len(order)
        if profile['quality']=='quality': score+=max(0,min(100,float(row.get('quality_score') or 0)))/100*20
        if profile['quality']=='complete' and max_pages: score+=20*max(0,int(row.get('page_count') or 0))/max_pages
        row['resolver_score']=score
