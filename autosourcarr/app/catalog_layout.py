"""Gallery layout for shared-only metadata; this never creates native bindings."""
import json
from . import catalog_identity as identity,catalog_exchange_format as wire,gallery_policy
from .gallery_metadata import enrich_gallery_payload
from .matcher import normalize_title


def snapshot(exchange,c,eid):
    row=dict(c.execute('SELECT * FROM entries WHERE id=?',(eid,)).fetchone())
    primary=identity.ensure(c,eid);aliases=identity.ids(c,eid)
    return {'uid':primary,'aliases':aliases,'lineage':identity.lineage(c,aliases),'title':row['preferred_title'],
            'titles':[r[0] for r in c.execute('SELECT title FROM titles WHERE entry_id=? ORDER BY id',(eid,))],
            'metadata':exchange._metadata(row),'references':[r[0] for r in c.execute('SELECT reference FROM catalogue_public_refs WHERE entry_id=? ORDER BY reference',(eid,))],
            'sources':[json.loads(r[0]) for r in c.execute('SELECT fact_json FROM catalogue_source_facts WHERE entry_id=? ORDER BY source_id,reference',(eid,))]}


def separated(c,service,left,right,left_record,right_record):
    if service._duplicate_review(left,right)=='not_duplicate':return True
    a={(r['source_id'],r['reference']) for r in left_record['sources']};b={(r['source_id'],r['reference']) for r in right_record['sources']}
    for row in c.execute("SELECT decision_json FROM catalogue_decisions WHERE state<>'ignored'"):
        choice=json.loads(row[0]);kind=choice['kind']
        if kind=='entry-separate' and len(choice['entries'])==2:
            x,y=(set(identity.targets(c,[uid])) for uid in choice['entries'])
            if left in x and right in y or left in y and right in x:return True
        if kind=='separate' and choice['entries']:
            target=set(identity.targets(c,[choice['entries'][0]]));records={(r['source_id'],r['reference']) for r in choice['records']}
            if left in target and records&b or right in target and records&a:return True
    return False


def apply(database,service,sid,revision,*,task_id=None):
    from .catalog_exchange import CatalogueExchange
    from .task_registry import task_registry
    exchange=CatalogueExchange(database,service);result={'splits':0,'merged':0,'checked':0}
    with database.catalog() as c:
        ids=[int(r[0]) for r in c.execute('SELECT DISTINCT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id WHERE f.source_id=?',(str(sid),))]
    for eid in ids:
        if task_id and task_registry.cancel_requested(task_id):raise RuntimeError('Cancelled by user')
        with service._catalog_maintenance_lock,database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            if gallery_policy.read(database,sid)['revision']!=revision:raise ValueError('Gallery policy changed during shared layout; apply the newest rules')
            if not identity.unverified(database,eid):continue
            record=snapshot(exchange,c,eid);units=exchange._units(record);result['checked']+=1
            if len(units)<2:continue
            # Keep the original local row for the first gallery. Additional rows
            # get source-scoped identities, while the old group ID stays traceable.
            c.execute('DELETE FROM catalogue_source_facts WHERE entry_id=?',(eid,))
            c.execute("DELETE FROM catalogue_public_refs WHERE entry_id=? AND (reference LIKE 'asc1:record:%' OR reference LIKE 'asc1:gallery:%')",(eid,))
            c.execute('UPDATE catalogue_entry_ids SET primary_id=0 WHERE entry_id=?',(eid,))
            occupied=identity.targets(c,[units[0]['uid']])
            identity.ensure(c,eid,None if occupied and occupied!=[eid] else units[0]['uid'])
            if not c.execute('SELECT manual_preferred_title FROM entries WHERE id=?',(eid,)).fetchone()[0]:
                c.execute('UPDATE entries SET preferred_title=? WHERE id=?',(units[0]['title'],eid))
            exchange._associate(c,eid,units[0])
            for unit in units[1:]:
                owners=identity.targets(c,[unit['uid']])
                chosen=owners[0] if len(owners)==1 and owners[0]!=eid else exchange._add(c,unit)
                exchange._associate(c,chosen,unit);result['splits']+=1
            identity.refresh_groups(c,[record['uid']])
    if not gallery_policy.effective(database,sid)['enabled']:return result
    # Exact native work-heading buckets bound candidate discovery for unverified
    # metadata. All source facts in a proposed group still pass the same gallery
    # proof and selected criteria; active native data is handled by the service.
    buckets={}
    with database.catalog() as c:
        ids=[int(r[0]) for r in c.execute('SELECT DISTINCT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id WHERE f.source_id=?',(str(sid),))]
    for eid in ids:
        if task_id and task_registry.cancel_requested(task_id):raise RuntimeError('Cancelled by user')
        with service._catalog_maintenance_lock:
            if not identity.unverified(database,eid):continue
            with database.catalog() as c:
                if gallery_policy.read(database,sid)['revision']!=revision:raise ValueError('Gallery policy changed during shared layout')
                record=snapshot(exchange,c,eid)
            fact=next((r for r in record['sources'] if r['source_id']==str(sid)),None)
            if not fact or not fact['gallery']:continue
            payload=enrich_gallery_payload({'sourceId':sid,'title':fact['raw_title'] or fact['title'],**fact['metadata']},{'metadata_profile':'gallery'})
            key=normalize_title((payload.get('_gallery_metadata') or {}).get('work_title') or '')
            if not key:continue
            candidates=buckets.setdefault(key,[]);combined=False
            for other in list(candidates):
                other=service.canonical_entry_id(other)
                if other==eid or not identity.unverified(database,other):continue
                with database.catalog() as c:
                    previous=snapshot(exchange,c,other)
                    if separated(c,service,other,eid,previous,record):continue
                facts=list({(r['source_id'],r['reference']):r for r in [*previous['sources'],*record['sources']]}.values())
                if len(facts)>wire.MAX_MEMBERS:continue
                if len(exchange._units({**previous,'sources':facts}))!=1:continue
                target=service.merge_entries(other,eid,'reviewed-shared-gallery-layout')
                with database.catalog() as c:c.execute('UPDATE entries SET preferred_title=? WHERE id=? AND manual_preferred_title=0',((payload.get('_gallery_metadata') or {}).get('work_title') or record['title'],target))
                result['merged']+=1;combined=True;break
            if not combined:candidates.append(eid)
    return result
