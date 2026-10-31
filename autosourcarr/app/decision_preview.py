"""Run the actual matcher against a draft policy without changing live evidence."""
import hashlib
import json
from . import matching_policies
from .db import now_iso
from .config import settings

ENGINE='reimagined-0.3.47-policy-preview-v1'


def preview(database,service,draft,pairs):
    if not isinstance(draft,dict) or set(draft)!={'scope','subject','direction','rule'}:raise ValueError('Select one policy and its draft rule')
    scope,subject,direction=draft['scope'],str(draft['subject']),draft['direction']
    if scope not in {'source','group'} or direction not in {'internal','external'}:raise ValueError('Unknown policy scope')
    rule=matching_policies.validate(draft['rule'])
    if not isinstance(pairs,list) or not 1<=len(pairs)<=32:raise ValueError('Choose 1 to 32 representative pairs')
    with database.catalog() as c:
        table,column=('source_config','source_id') if scope=='source' else ('source_groups','id')
        if not c.execute(f'SELECT 1 FROM {table} WHERE {column}=?',(subject,)).fetchone():raise ValueError('Policy subject is unavailable')
        # Use fresh source/group membership, never a stale runtime cache.
        sources={str(r['source_id']):dict(r) for r in c.execute('SELECT * FROM source_config')}
        before=dict(matching_policies.rules(database));after=dict(before)
        if rule is None:after.pop((scope,subject,direction),None)
        else:after[(scope,subject,direction)]=rule
    results=[];direct_pairs=0
    for pair in pairs:
        if not isinstance(pair,list) or len(pair)!=2 or any(type(i) is not int or i<=0 for i in pair):raise ValueError('Invalid review pair')
        left,right=pair
        if left==right or service.canonical_entry_id(left)!=left or service.canonical_entry_id(right)!=right:raise ValueError('Select two current canonical works')
        member_counts=[]
        for eid in pair:
            with database.catalog() as c:
                if not c.execute('SELECT 1 FROM entries WHERE id=?',(eid,)).fetchone():raise KeyError(eid)
            count=len(service._automatic_merge_member_snapshots(eid));member_counts.append(count)
            if count>32:raise ValueError('Large lineage requires a narrower review')
        direct_pairs+=member_counts[0]*member_counts[1]
        if direct_pairs>256:raise ValueError('Large merged groups require fewer preview pairs')
        # Both runs use the same base identity/edition/rejection/non-transitive
        # gates as automation. Only the policy map is substituted, never settings.
        old=service._auto_merge_group_evidence(left,right,policy_values=before,policy_sources=sources)
        new=service._auto_merge_group_evidence(left,right,policy_values=after,policy_sources=sources)
        snapshots=[{'current':service._entry_duplicate_evidence_snapshot(eid),
                    'original_members':service._automatic_merge_member_snapshots(eid)} for eid in pair]
        results.append({'pair':pair,'before':old,'after':new,'changed':old.get('eligible')!=new.get('eligible'),
                        'evidence_revision':hashlib.sha256(json.dumps(snapshots,sort_keys=True,default=str).encode()).hexdigest()})
    output={'engine':ENGINE,'draft':{**draft,'subject':subject,'rule':rule},'pairs':results,
            'base_policy':{name:getattr(settings,name) for name in ('auto_match_threshold','duplicate_auto_merge_min_exact_titles',
                'duplicate_auto_merge_summary_threshold','duplicate_auto_merge_min_summary_chars','exact_thumbnail_matching')},
            'before_policy':matching_policies.revision(before),'after_policy':matching_policies.revision(after),
            'source_revision':hashlib.sha256(json.dumps(sources,sort_keys=True,default=str).encode()).hexdigest(),
            'scope_note':'Evaluates these pairs only; applying saves the policy for future work and never merges these pairs.'}
    output['revision']=hashlib.sha256(json.dumps(output,sort_keys=True,default=str).encode()).hexdigest()
    return output


def apply(database,service,draft,pairs,revision):
    with service._catalog_maintenance_lock,database.catalog() as c:
        c.execute('BEGIN IMMEDIATE')
        result=preview(database,service,draft,pairs)
        if result['revision']!=revision:raise ValueError('Policy or evidence changed; preview again')
        d=result['draft'];key=(d['scope'],d['subject'],d['direction'])
        c.execute('INSERT INTO policy_preview_history(preview_json,created_at) VALUES(?,?)',(json.dumps(result,sort_keys=True),now_iso()))
        if d['rule'] is None:c.execute('DELETE FROM matching_policies WHERE scope=? AND subject=? AND direction=?',key)
        else:c.execute('''INSERT INTO matching_policies(scope,subject,direction,rule_json,updated_at) VALUES(?,?,?,?,?)
               ON CONFLICT(scope,subject,direction) DO UPDATE SET rule_json=excluded.rule_json,updated_at=excluded.updated_at''',(*key,json.dumps(d['rule']),now_iso()))
    matching_policies.invalidate(database)
    return {'applied':True,'policy_revision':result['after_policy'],'evaluated_pairs':len(result['pairs'])}
