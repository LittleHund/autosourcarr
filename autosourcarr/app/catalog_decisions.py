"""Portable manual choices. Foreign numbers never become local source bindings."""
import json
import uuid
from . import catalog_identity as identity,catalog_exchange_format as wire


def collect(c,service,uid_for,records,make_fact,clean,selected,*,first_page=True):
    result=[]
    def add(kind,key,entries=(),locators=(),value=None):
        entries=list(dict.fromkeys(int(e) for e in entries if e))
        if entries and min(entries) not in selected:return
        if not entries and not first_page:return
        row={'uid':identity.decision_uid(c,kind,key),'kind':kind,'entries':[uid_for(e) for e in entries],
             'records':list(locators),'value':value or {}}
        wire.decision(row)
        if _effect(c,row) not in applied_effects:result.append(row)
    applied_titles=set();applied_effects=set()
    retained=[]
    for row in c.execute('SELECT decision_json,state FROM catalogue_decisions ORDER BY uid,digest'):
        decision=json.loads(row['decision_json']);retained.append(decision)
        if row['state']=='applied':
            effect=_effect(c,decision,imported=True)
            if effect is not None:applied_effects.add(effect)
        if row['state']=='applied' and decision['kind']=='title':
            for eid in identity.targets(c,decision['entries']):applied_titles.add((eid,decision['value'].get('title')))
    for row in c.execute('''SELECT e.id,COALESCE(o.title,e.preferred_title) title FROM entries e
                           LEFT JOIN preferred_title_overrides o ON o.entry_id=e.id WHERE o.entry_id IS NOT NULL OR e.manual_preferred_title=1'''):
        if (row['id'],clean(row['title'])) not in applied_titles:add('title',uid_for(row['id']),[row['id']],value={'title':clean(row['title'])})
    for row in c.execute('SELECT * FROM duplicate_reviews WHERE decision=?',('not_duplicate',)):
        add('entry-separate',str(row['left_entry_id'])+':'+str(row['right_entry_id']),[row['left_entry_id'],row['right_entry_id']])
    group_rows={r['id']:dict(r) for r in c.execute('SELECT * FROM manual_match_groups')}
    bindings=[dict(r) for r in c.execute('SELECT * FROM manual_match_bindings ORDER BY source_id,source_manga_id')]
    def fact_for(row):
        record=records.get((str(row['source_id']),int(row['source_manga_id'])))
        if record:return record['fact'],record['entry_id']
        try:
            payload=json.loads(row.get('record_json') or '{}')
            native={'source_id':row['source_id'],'source_manga_id':row['source_manga_id'],'source_manga_url':payload.get('url',''),
                    'source_title':row.get('source_title') or payload.get('title',''),'metadata_json':json.dumps(payload)}
            return make_fact(native),row.get('entry_id_hint')
        except (ValueError,TypeError):return None,row.get('entry_id_hint')
    for group_id,group in group_rows.items():
        members=[r for r in bindings if r['group_id']==group_id];facts=[];entries=[];missing=False
        for row in members:
            fact,eid=fact_for(row)
            if fact:facts.append(fact)
            else:missing=True
            if eid:entries.append(eid)
        value={'group_title':clean(group['title'])}
        if missing:value['note']='Some original members have no portable source URL. Review this group against locally installed sources.'
        add('group',group_id,entries,[] if missing else facts,value)
    for row in bindings:
        if row['group_id']:continue
        fact,eid=fact_for(row);value={'grouping_suppressed':str(int(bool(row['grouping_suppressed'])))}
        if not fact:value['note']='The original record has no portable public source identity; retained for review.'
        add('source-match',str(row['source_id'])+':'+str(row['source_manga_id']),[eid] if eid else [],[fact] if fact else [],value)
    for row in c.execute('SELECT * FROM manual_separate_records'):
        row=dict(row);fact,_=fact_for(row)
        entries=[row['target_entry_id']]
        if row.get('separate_entry_id_hint'):entries.append(row['separate_entry_id_hint'])
        add('separate',str(row['target_entry_id'])+':'+str(row['source_id'])+':'+str(row['source_manga_id']),entries,[fact] if fact else [],
            {'title':clean(row['source_title']),'note':'' if fact else 'Original source identity is unavailable; local record selection is required.'})
    for row in c.execute('SELECT * FROM import_manual_autoresolve'):
        row=dict(row);record=records.get((str(row['target_source_id']),int(row['target_source_manga_id'])))
        fact=record['fact'] if record else None;eid=record['entry_id'] if record else row.get('target_entry_id_hint')
        value={k:clean(row.get(k) or '') for k in ('backup_title','backup_source')};value['backup_key']=str(row['backup_key'])
        value['backup_source_id']=str(row.get('backup_source_id') or '')
        try:value['backup_source_url']=service._backup_url_key(row.get('backup_source_url'))
        except (ValueError,TypeError):value['backup_source_url']=''
        if value['backup_source_url']:
            try:wire.portable_identity.source_path(value['backup_source_url'])
            except ValueError:value['backup_source_url']='';value['note']='Original backup URL is private or unavailable; retained for review.'
        if not fact:value['note']='The target needs a locally resolved source record.'
        if not all(wire.public_identity_text(value.get(k,'')) for k in ('backup_key','backup_source_url')):
            value['backup_key']='';value['backup_source_url']='';value['note']='The original import identity contains a private access parameter; retain this choice for local review.'
        add('import-choice',row['backup_key'],[eid] if eid else [],[fact] if fact else [],value)
    for row in c.execute('SELECT * FROM import_match_rejections'):
        row=dict(row);value={k:clean(row.get(k) or '') for k in ('backup_title','backup_source','rejected_title')};value['backup_key']=str(row['backup_key'])
        signature=str(row['signature'])
        if signature.startswith(('source-url:','source-title:','entry-title:')):value['signature']=signature
        else:value['note']='Legacy source-number rejection retained by title; resolve its public source identity before applying.'
        if not all(wire.public_identity_text(value.get(k,'')) for k in ('backup_key','signature')):
            value['backup_key']='';value.pop('signature',None);value['note']='The original rejection identity contains a private access parameter; retain its titles for local review.'
        add('import-rejection',row['backup_key']+':'+signature,value=value)
    for row in c.execute('SELECT * FROM portable_identity_reviews'):
        row=dict(row)
        if row['reference'].startswith('asc1:entry:'):continue
        add('identity-review',str(row['entry_id'])+':'+row['reference'],[row['entry_id']],value={'reference':row['reference'],'decision':row['decision'],'note':clean(row['note'])})
    # Forward original decision IDs/versions; don't mint a new origin each hop.
    for row in retained:
        owners=identity.targets(c,row['entries'])
        if owners and min(owners) not in selected:continue
        if not owners and not first_page:continue
        result.append(row)
    return list({(row['uid'],wire.digest(row)):row for row in result}.values())


def _effect(c,choice,*,imported=False):
    targets=[identity.targets(c,[uid]) for uid in choice['entries']]
    if any(len(values)!=1 for values in targets):return None
    value=choice['value'];kind=choice['kind'];entries=[row[0] for row in targets]
    records=sorted((r['source_id'],r['reference']) for r in choice['records'])
    if kind=='title':effect=[entries,value.get('title')]
    elif kind=='group':effect=records
    elif kind=='source-match':effect=[entries,records,value.get('grouping_suppressed','0')]
    elif kind=='entry-separate':effect=sorted(entries)
    elif kind=='separate':effect=[entries[:1],records]
    elif kind in {'import-choice','import-rejection'}:effect=[value.get('backup_key'),records,value.get('signature')]
    else:effect=[entries,value.get('reference'),'needs-review' if imported and value.get('decision')=='confirmed' else value.get('decision')]
    return wire.canonical([kind,effect])


def inspect(c,service,decision,resolve_records):
    targets=[identity.targets(c,[value]) for value in decision['entries']]
    unique=[values[0] for values in targets if len(values)==1]
    rows=[resolve_records(record) for record in decision['records']]
    records=[value[0] for value in rows if len(value)==1]
    kind=decision['kind'];value=decision['value']
    result={'state':'pending','message':'Needs a local entry or source identity before this choice can be applied.'}
    if any(len(values)!=1 for values in targets):return result
    if kind=='title' and len(unique)==1:
        existing=c.execute('SELECT title FROM preferred_title_overrides WHERE entry_id=?',(unique[0],)).fetchone()
        entry=c.execute('SELECT preferred_title,manual_preferred_title FROM entries WHERE id=?',(unique[0],)).fetchone()
        current=existing['title'] if existing else entry['preferred_title'] if entry and entry['manual_preferred_title'] else None
        if current and current!=value['title']:return {'state':'conflict','message':'A different local manual title is retained.'}
        return {'state':'ready','message':'Apply the saved title to this mapped entry.'}
    if kind=='entry-separate' and len(unique)==2:
        if unique[0]==unique[1]:return {'state':'conflict','message':'Both foreign identities currently resolve to one local entry; review its separation first.'}
        return {'state':'ready','message':'Keep these two mapped entries separate.'}
    if kind=='import-rejection':
        return {'state':'ready','message':'Retain the portable rejected-import signature.'} if value.get('signature') and value.get('backup_key') else result
    if kind=='identity-review' and len(unique)==1:
        existing=c.execute('SELECT decision FROM portable_identity_reviews WHERE entry_id=? AND reference=?',(unique[0],value['reference'])).fetchone()
        if existing:return {'state':'conflict','message':'An existing local identity review remains authoritative.'}
        return {'state':'ready','message':'Retain the exclusion or require a fresh local confirmation.'}
    if len(records)!=len(decision['records']) or not records:return result
    if kind=='import-choice':
        if not value.get('backup_key'):return result
        existing=c.execute('SELECT target_source_id,target_source_manga_id FROM import_manual_autoresolve WHERE backup_key=?',(value.get('backup_key',''),)).fetchone()
        if existing and (existing['target_source_id'],existing['target_source_manga_id'])!=(records[0]['source_id'],records[0]['source_manga_id']):return {'state':'conflict','message':'A different local import mapping is retained.'}
        return {'state':'ready','message':'Use the resolved local record for this saved import choice.'}
    if kind in {'group','source-match'}:
        if kind=='group' and len(records)>1:
            for record in records:
                policy=c.execute('SELECT policy_json FROM gallery_policies WHERE source_id=?',(record['source_id'],)).fetchone()
                if record['fact']['gallery'] and policy and not json.loads(policy[0])['enabled']:
                    return {'state':'pending','message':'Gallery combining is disabled for a member source; its imported group is retained separately.'}
        owners={int(r['entry_id']) for r in records}
        if unique and owners!={unique[0]} or len(owners)!=1:return {'state':'pending','message':'This choice requires moving or combining local records; inspect and apply it explicitly.'}
        for row in records:
            existing=c.execute('SELECT group_id,grouping_suppressed,entry_id_hint FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?',(row['source_id'],row['source_manga_id'])).fetchone()
            if existing:return {'state':'conflict','message':'A local manual binding or group remains authoritative.'}
        return {'state':'ready','message':'Persist the choice against the already mapped local record numbers.'}
    if kind=='separate' and unique:
        if int(records[0]['entry_id'])==unique[0]:return {'state':'pending','message':'This source is still attached to the target entry; an explicit local split is required.'}
        return {'state':'ready','message':'Keep this resolved source record separate from the target.'}
    return result


def apply_ready(c,database,service,decision,resolve_records):
    from .db import now_iso
    status=inspect(c,service,decision,resolve_records)
    if status['state']!='ready':return status
    values=decision['value'];kind=decision['kind'];entries=[identity.targets(c,[value])[0] for value in decision['entries']]
    records=[resolve_records(value)[0] for value in decision['records']]
    stamp=now_iso()
    if kind=='title':service.set_preferred_title(entries[0],values['title'])
    elif kind=='entry-separate':service.review_duplicate(entries[0],entries[1],'not_duplicate')
    elif kind=='import-rejection':
        c.execute('INSERT OR IGNORE INTO import_match_rejections VALUES(?,?,?,?,?,?)',(values['backup_key'],values['signature'],values.get('backup_title',''),values.get('backup_source',''),values.get('rejected_title',''),stamp))
    elif kind=='identity-review':
        from . import identity_review
        c.execute('INSERT OR IGNORE INTO catalogue_public_refs VALUES(?,?)',(entries[0],values['reference']))
        state=identity_review.read(database,service,entries[0]);verb='excluded' if values['decision']=='excluded' else 'needs-review'
        identity_review.save(database,service,entries[0],values['reference'],verb,state['revision'],values.get('note') or 'Imported review; confirm against local evidence')
    elif kind in {'group','source-match'}:
        group_id=uuid.uuid5(uuid.UUID(identity.origin(c).rsplit(':',1)[-1]),decision['uid']).hex if kind=='group' else None
        if group_id:c.execute('INSERT OR IGNORE INTO manual_match_groups VALUES(?,?,?,?)',(group_id,values.get('group_title',''),stamp,stamp))
        for row in records:
            payload=service._record_payload(row)
            c.execute('''INSERT OR IGNORE INTO manual_match_bindings(source_id,source_manga_id,group_id,grouping_suppressed,entry_id_hint,source_name,source_title,record_json,created_at,updated_at)
                         VALUES(?,?,?,?,?,?,?,?,?,?)''',(row['source_id'],row['source_manga_id'],group_id,int(values.get('grouping_suppressed','0')),row['entry_id'],row.get('source_name',''),row['source_title'],json.dumps(payload),stamp,stamp))
            for table in ('source_matches','source_variants'):c.execute(f'UPDATE {table} SET locked=1 WHERE entry_id=? AND source_id=? AND source_manga_id=? AND active=1',(row['entry_id'],row['source_id'],row['source_manga_id']))
    elif kind=='separate':
        row=records[0];service._remember_manual_separate_record(entries[0],row['source_id'],row['source_manga_id'],separate_entry_id=row['entry_id'],source_title=row['source_title'])
        service.review_duplicate(entries[0],row['entry_id'],'not_duplicate')
    elif kind=='import-choice':
        row=records[0]
        c.execute('''INSERT OR IGNORE INTO import_manual_autoresolve(backup_key,backup_title,backup_source,backup_source_id,backup_source_url,target_source_id,target_source_manga_id,target_source_name,target_title,target_entry_id_hint,target_record_json,created_at,updated_at)
                     VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)''',(values['backup_key'],values.get('backup_title',''),values.get('backup_source',''),values.get('backup_source_id',''),values.get('backup_source_url',''),row['source_id'],row['source_manga_id'],row.get('source_name',''),row['source_title'],row['entry_id'],json.dumps(service._record_payload(row)),stamp,stamp))
    return {'state':'applied','message':'Applied through mapped local identities; original numeric IDs were retained.'}
