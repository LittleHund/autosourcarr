"""Opt-in, reviewed choices for future serving. Downloads/progress stay owned by main."""
import hashlib
import json
import math
from datetime import datetime,timezone
from .db import now_iso

DEFAULT={'enabled':False,'cutoff':90,'minimum_improvement':10,'cooldown_hours':24,'sample_max_age_hours':168}


def age(value):
    try:return max(0,(datetime.now(timezone.utc)-datetime.fromisoformat(value.replace('Z','+00:00'))).total_seconds())
    except (ValueError,TypeError):return float('inf')


def read(database,eid):
    with database.catalog() as c:
        if not c.execute('SELECT 1 FROM entries WHERE id=?',(eid,)).fetchone():raise KeyError(eid)
        row=c.execute('SELECT * FROM serving_upgrade_policies WHERE entry_id=?',(eid,)).fetchone()
        retained=[{**dict(r),'policy':json.loads(r['policy_json'])} for r in c.execute('SELECT * FROM serving_policy_history WHERE entry_id=? ORDER BY id DESC LIMIT 50',(eid,))]
    return {'entry_id':eid,'revision':row['revision'] if row else 0,'policy':json.loads(row['policy_json']) if row else dict(DEFAULT),'retained':retained}


def save(database,service,eid,policy,revision):
    if not isinstance(policy,dict) or set(policy)!=set(DEFAULT) or type(policy['enabled']) is not bool:raise ValueError('Invalid upgrade policy')
    for name,lo,hi in [('cutoff',1,100),('minimum_improvement',1,100),('cooldown_hours',1,8760),('sample_max_age_hours',1,8760)]:
        value=policy[name]
        if type(value) not in (int,float) or not math.isfinite(value) or not lo<=value<=hi:raise ValueError('Invalid upgrade threshold')
    with service._catalog_maintenance_lock,database.catalog() as c:
        c.execute('BEGIN IMMEDIATE')
        current=read(database,eid)
        if current['revision']!=revision:raise ValueError('Upgrade policy changed; reload')
        c.execute('INSERT INTO serving_policy_history(entry_id,old_entry_id,policy_json,reason,created_at) VALUES(?,?,?,?,?)',
                  (eid,eid,json.dumps(current['policy']),'before policy edit',now_iso()))
        c.execute('INSERT OR REPLACE INTO serving_upgrade_policies VALUES(?,?,?,?)',(eid,revision+1,json.dumps(policy),now_iso()))
    return read(database,eid)


def prefer(database,entry_id,chapter_id,candidates):
    """Called only after existing membership, rejection and edition gates."""
    with database.catalog() as c:
        policy=c.execute('SELECT policy_json FROM serving_upgrade_policies WHERE entry_id=?',(entry_id,)).fetchone()
        choice=c.execute('SELECT candidate_id FROM serving_choices WHERE virtual_chapter_id=? AND entry_id=?',(chapter_id,entry_id)).fetchone()
        manual=any(c.execute(f'SELECT 1 FROM {table} WHERE entry_id=? AND active=1 AND locked=1 LIMIT 1',(entry_id,)).fetchone() for table in ('source_matches','source_variants'))
    if not manual and policy and json.loads(policy[0]).get('enabled') and choice:
        candidates.sort(key=lambda row:0 if row['id']==choice[0] else 1)


def preview(database,service,eid,chapter_id,candidate_id):
    policy=read(database,eid);rules=policy['policy'];detail=service.chapter_details(chapter_id)
    if detail['chapter']['entry_id']!=eid:raise ValueError('Chapter is not owned by this work')
    candidates={r['id']:r for r in detail['candidates']}
    if candidate_id not in candidates:raise ValueError('Candidate is no longer admitted by the resolver')
    with database.catalog() as c:
        choice=c.execute('SELECT * FROM serving_choices WHERE virtual_chapter_id=?',(chapter_id,)).fetchone()
        manual=any(c.execute(f'SELECT 1 FROM {table} WHERE entry_id=? AND active=1 AND locked=1 LIMIT 1',(eid,)).fetchone() for table in ('source_matches','source_variants'))
        attempt=c.execute('SELECT * FROM quality_attempts WHERE candidate_id=?',(candidate_id,)).fetchone()
    current_id=choice['candidate_id'] if choice else (detail.get('served') or {}).get('candidate_id') or detail['would_serve_candidate_id']
    current=candidates.get(current_id);target=candidates[candidate_id];blockers=[]
    if not rules['enabled']:blockers.append('Reviewed upgrades are disabled for this work')
    if manual:blockers.append('A manually locked source record controls this work')
    if current is None:blockers.append('Previous serving candidate is no longer an admitted baseline')
    if current_id==candidate_id:blockers.append('Candidate is already selected')
    with database.origins() as o:
        sample=o.execute('SELECT * FROM quality_samples WHERE candidate_id=?',(candidate_id,)).fetchone()
        baseline=o.execute('SELECT * FROM quality_samples WHERE candidate_id=?',(current_id,)).fetchone()
    if not sample or age(sample['analyzed_at'])>rules['sample_max_age_hours']*3600 or attempt and attempt['outcome']!='complete':blockers.append('Target needs a recent complete quality sample')
    if not baseline or age(baseline['analyzed_at'])>rules['sample_max_age_hours']*3600:blockers.append('Current candidate needs a recent complete quality sample')
    old_score=float((current or {}).get('quality_score') or 0);new_score=float(target.get('quality_score') or 0)
    if old_score>=rules['cutoff']:blockers.append('Current candidate already meets the cutoff')
    if new_score<rules['cutoff']:blockers.append('Target is below the cutoff')
    if new_score-old_score<rules['minimum_improvement']:blockers.append('Improvement is below the configured minimum')
    if choice and age(choice['changed_at'])<rules['cooldown_hours']*3600:blockers.append('Serving choice cooldown is active')
    output={'entry_id':eid,'chapter_id':chapter_id,'candidate_id':candidate_id,'current_candidate_id':current_id,
            'quality_before':old_score,'quality_after':new_score,'blockers':blockers,'eligible':not blockers,
            'policy':policy,'choice':dict(choice) if choice else None,
            'candidate_revision':hashlib.sha256(json.dumps(detail,sort_keys=True,default=str).encode()).hexdigest(),
            'sample_revision':hashlib.sha256(json.dumps([dict(sample) if sample else None,dict(baseline) if baseline else None,dict(attempt) if attempt else None],sort_keys=True).encode()).hexdigest(),
            'effect':'Select this candidate for future serving; retain downloaded files, reading progress and origin history.'}
    output['revision']=hashlib.sha256(json.dumps(output,sort_keys=True).encode()).hexdigest()
    return output


def apply(database,service,eid,chapter_id,candidate_id,revision):
    with service._catalog_maintenance_lock,database.catalog() as c:
        c.execute('BEGIN IMMEDIATE');result=preview(database,service,eid,chapter_id,candidate_id)
        if result['revision']!=revision:raise ValueError('Candidate, sample or policy changed; preview again')
        if not result['eligible']:raise ValueError('; '.join(result['blockers']))
        old=result['choice'];ts=now_iso()
        c.execute('INSERT OR REPLACE INTO serving_choices VALUES(?,?,?,?,?)',(chapter_id,eid,candidate_id,(old['revision'] if old else 0)+1,ts))
        c.execute('INSERT INTO serving_upgrade_history(entry_id,virtual_chapter_id,old_candidate_id,candidate_id,changed_at) VALUES(?,?,?,?,?)',
                  (eid,chapter_id,result['current_candidate_id'],candidate_id,ts))
    return {'applied':True,'effect':result['effect']}
