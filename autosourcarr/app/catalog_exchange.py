"""Additive catalogue exchange. Files carry identities and facts, never local SQL IDs."""
import json
import re
import time
import uuid
from collections import defaultdict
from . import catalog_exchange_format as wire,catalog_identity as identity,catalog_decisions as choices
from . import portable_identity,gallery_policy
from .db import now_iso
from .gallery_metadata import enrich_gallery_payload,is_gallery_source


class _NativeRecords(dict):
    def __init__(self):super().__init__();self.by_public=defaultdict(list);self.by_entry=defaultdict(list)


class CatalogueExchange:
    def __init__(self,database,service):self.db=database;self.service=service

    @staticmethod
    def clean(value):
        from .support_logs import support_logs
        # Titles are intentionally shared; scrub only their free-text contents.
        value=support_logs.privacy.text(str(value or ''))
        return re.sub(r'\[(?:host|email|ip)-[0-9a-f]{10}\]','[redacted]',value)

    def _metadata(self,row):
        return {**{key:self.clean(row.get(key) or '') for key in ('description','author','artist','genre')},'status':int(row.get('status') or 0)}

    def _fact(self,row):
        sid=str(row['source_id']);url=row.get('source_manga_url') or ''
        reference=portable_identity.gallery_reference(row) or portable_identity.record_id(sid,url)
        payload={}
        try:payload=json.loads(row.get('metadata_json') or '{}')
        except (ValueError,TypeError):pass
        if not isinstance(payload,dict):payload={}
        native=self.service._clean_source_manga_payload(sid,{**payload,'id':row.get('source_manga_id',0),'title':row.get('source_title') or payload.get('title','')})
        fact={'reference':reference,'source_id':sid,'title':self.clean(row.get('source_title') or native.get('title') or ''),
              'raw_title':self.clean((native.get('_gallery_metadata') or {}).get('raw_title') or native.get('_autosourcarr_raw_title') or row.get('source_title') or ''),
              'gallery':bool(native.get('_gallery_metadata')),'metadata':self._metadata({**payload,**row})}
        wire.locator(fact);return fact

    def _native(self,c,*,full_range=None,write_index=False):
        records=_NativeRecords();by_ref=defaultdict(set)
        for table in ('source_matches','source_variants'):
            columns='r.*' if full_range else 'r.entry_id,r.source_id,r.source_manga_id,r.source_manga_url,r.real_url,r.source_title'
            condition=' AND r.entry_id>=? AND r.entry_id<=?' if full_range else ''
            rows=c.execute(f'''SELECT {columns},sc.name source_name,sc.base_url FROM {table} r
                               LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.active=1{condition}''',full_range or ()).fetchall()
            for item in rows:
                row={**dict(item),'_storage':table}
                try:
                    if full_range:fact=self._fact(row)
                    else:
                        sid=str(row['source_id']);reference=portable_identity.gallery_reference(row) or portable_identity.record_id(sid,row['source_manga_url'])
                        fact={'reference':reference,'source_id':sid,'title':self.clean(row['source_title']),'raw_title':self.clean(row['source_title']),
                              'gallery':is_gallery_source(gallery_policy.source_profile(self.db,sid,self.service._source_cache.get(sid) or {})),
                              'metadata':{'description':'','author':'','artist':'','genre':'','status':0}}
                        wire.locator(fact)
                except (ValueError,TypeError):continue
                row['fact']=fact;records[(str(row['source_id']),int(row['source_manga_id']))]=row
                if write_index:identity.record_binding(c,int(row['entry_id']),row['source_id'],row['source_manga_id'],fact['reference'])
                records.by_public[(fact['source_id'],fact['reference'])].append(row)
                records.by_entry[int(row['entry_id'])].append(row)
                by_ref[(fact['source_id'],fact['reference'])].add(int(row['entry_id']))
        return records,by_ref

    @staticmethod
    def _resolve(records,locator):
        rows=records.by_public.get((locator['source_id'],locator['reference']),[])
        return list({(r['source_id'],r['source_manga_id'],r['entry_id']):r for r in rows}.values())

    @staticmethod
    def _full(c,row):
        full=c.execute(f'SELECT * FROM {row["_storage"]} WHERE entry_id=? AND source_id=? AND source_manga_id=? AND active=1',
                       (row['entry_id'],row['source_id'],row['source_manga_id'])).fetchone()
        return {**row,**dict(full)} if full else row

    def export(self,*,after=0,limit=5000,include_decisions=True):
        if type(after) is not int or after<0 or type(limit) is not int or not 1<=limit<=5000:raise ValueError('Choose an export size from 1 to 5000 entries')
        with self.service._catalog_maintenance_lock,self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            origin=identity.origin(c)
            all_ids=[int(r[0]) for r in c.execute('SELECT id FROM entries ORDER BY id')]
            uid_map={int(r['entry_id']):r['uid'] for r in c.execute('SELECT entry_id,uid FROM catalogue_entry_ids WHERE primary_id=1')}
            for eid in all_ids:
                if eid not in uid_map:uid_map[eid]=identity.ensure(c,eid)
            def uid_for(eid):
                eid=int(eid)
                if eid not in uid_map:uid_map[eid]=identity.ensure(c,eid)
                return uid_map[eid]
            selected=[eid for eid in all_ids if eid>after][:limit]
            native,_=self._native(c,write_index=True);entries=[]
            selected_native,_=self._native(c,full_range=(selected[0],selected[-1])) if selected else (_NativeRecords(),{})
            for eid in selected:
                row=dict(c.execute('SELECT * FROM entries WHERE id=?',(eid,)).fetchone())
                item=self.service.get_entry(eid);described=portable_identity.describe(item)
                refs={r['id'] for r in described['references']}
                refs.update(r[0] for r in c.execute('SELECT reference FROM catalogue_public_refs WHERE entry_id=?',(eid,)))
                facts={(r['fact']['reference'],r['fact']['source_id']):r['fact'] for r in selected_native.by_entry.get(eid,[])}
                for fact in c.execute('SELECT fact_json FROM catalogue_source_facts WHERE entry_id=?',(eid,)):
                    value=json.loads(fact[0]);facts.setdefault((value['reference'],value['source_id']),value)
                refs.update(value['reference'] for value in facts.values())
                primary=uid_for(eid)
                for fact in facts.values():c.execute('INSERT OR IGNORE INTO catalogue_uid_records VALUES(?,?,?)',(primary,fact['source_id'],fact['reference']))
                aliases=identity.ids(c,eid)
                entries.append({'uid':primary,'aliases':aliases,'lineage':identity.lineage(c,aliases),'title':self.clean(row['preferred_title']),
                                'titles':list(dict.fromkeys(self.clean(r[0]) for r in c.execute('SELECT title FROM titles WHERE entry_id=? ORDER BY id',(eid,)))),
                                'metadata':self._metadata(row),'references':sorted(refs),'sources':list(facts.values())})
            decisions=choices.collect(c,self.service,uid_for,native,self._fact,self.clean,set(selected),first_page=after==0) if include_decisions else []
            # Forward retained versions with their original identities, including
            # conflicting metadata. The receiving instance still keeps local choices.
            seen={(item['uid'],wire.digest(item)) for item in entries}
            for row in c.execute("SELECT uid,digest,record_json FROM catalogue_import_records WHERE state<>'ignored' ORDER BY uid,digest"):
                item=json.loads(row['record_json']);item=self._resolved_record(item,self._resolution(c,item))
                owners=identity.targets(c,[item['uid']])
                if owners and not set(owners)&set(selected) or not owners and after!=0:continue
                key=(item['uid'],wire.digest(item))
                if key not in seen:seen.add(key);entries.append(item)
            bundle=wire.validate({'format':wire.FORMAT,'version':1,'origin':origin,'entries':entries,'decisions':decisions})
            if len(wire.canonical(bundle).encode('utf-8'))>wire.MAX_BYTES:raise ValueError('Export exceeds 64 MiB; choose a smaller entry batch')
            next_cursor=selected[-1] if selected and any(eid>selected[-1] for eid in all_ids) else None
            return {'catalogue':bundle,'next_cursor':next_cursor,'total':len(all_ids),'entries':len(entries),'decisions':len(decisions)}

    def _units(self,entry):
        """A foreign group cannot override this instance's gallery settings."""
        if len(entry['sources'])<2:
            if entry['sources'] and entry['sources'][0]['gallery'] and not gallery_policy.effective(self.db,entry['sources'][0]['source_id'])['enabled']:
                fact=entry['sources'][0];return [{**entry,'title':fact['raw_title'] or fact['title'] or entry['title']}]
            return [entry]
        groups=[]
        for fact in entry['sources']:
            source=gallery_policy.source_profile(self.db,fact['source_id'],self.service._source_cache.get(fact['source_id']) or {})
            if fact['gallery']:source={**source,'metadata_profile':'gallery'}
            payload=enrich_gallery_payload({'sourceId':fact['source_id'],'title':fact['raw_title'] or fact['title'],**fact['metadata']},source)
            chosen=None
            for group in groups:
                evidence=self.service._gallery_evidence(payload,[p for _,p in group])
                if evidence is None or evidence.get('eligible'):chosen=group;break
            if chosen is None:chosen=[];groups.append(chosen)
            chosen.append((fact,payload))
        if len(groups)==1:return [entry]
        namespace=uuid.UUID(entry['uid'].rsplit(':',1)[-1]);result=[]
        for group in groups:
            facts=[f for f,_ in group];refs={f['reference'] for f in facts}
            key='asc1:entry:autosourcarr:'+str(uuid.uuid5(namespace,wire.canonical(sorted((f['source_id'],f['reference']) for f in facts))))
            references=[ref for ref in entry['references'] if ref.split(':')[1] in {'work','edition'} or ref in refs]
            title=facts[0]['raw_title'] or facts[0]['title'] if len(facts)==1 else entry['title']
            result.append({**entry,'uid':key,'aliases':sorted(set([entry['uid'],*entry['aliases']])),
                           'lineage':[*entry['lineage'],{'uid':key,'records':[{'source_id':f['source_id'],'reference':f['reference']} for f in facts]}],
                           'title':title or entry['title'],'sources':facts,'references':sorted(set(references)|refs),
                           'metadata':facts[0]['metadata'] if len(facts)==1 else entry['metadata']})
        return result

    @staticmethod
    def _source_keys(record):return sorted([r['source_id'],r['reference']] for r in record['sources'])

    def _resolution(self,c,record):
        row=c.execute('SELECT resolution_json FROM catalogue_record_resolutions WHERE uid=? AND digest=?',(record['uid'],wire.digest(record))).fetchone()
        if row:return json.loads(row[0])
        for row in c.execute('SELECT resolution_json FROM catalogue_record_resolutions WHERE uid=? ORDER BY rowid DESC LIMIT 64',(record['uid'],)):
            value=json.loads(row[0])
            if value.get('remap') and value.get('source_keys')==self._source_keys(record):return value
        return None

    @staticmethod
    def _resolved_record(record,resolution):return {**record,**resolution['remap']} if resolution and resolution.get('remap') else record

    def _plan(self,c,bundle):
        native,by_ref=self._native(c)
        for row in c.execute('SELECT r.reference,r.source_id,r.entry_id FROM catalogue_source_facts r JOIN entries e ON e.id=r.entry_id'):
            by_ref[(row['source_id'],row['reference'])].add(int(row['entry_id']))
        items=[];seen_principals={};virtual_sources=defaultdict(set);virtual_uids=set()
        for original in bundle['entries']:
            original=self._resolved_record(original,self._resolution(c,original))
            original_owners=identity.targets(c,[original['uid']]);original_refs={(r['source_id'],r['reference']) for r in original['sources']}
            known_refs=set()
            for eid in original_owners:
                known_refs.update((r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_source_facts WHERE entry_id=?',(eid,)))
                known_refs.update((r['source_id'],r['fact']['reference']) for r in native.by_entry.get(eid,[]))
            parent_conflict=bool(known_refs and original_refs and not known_refs&original_refs)
            for incoming in self._units(original):
                resolution=self._resolution(c,incoming);unit=self._resolved_record(incoming,resolution)
                wire.entry(unit)
                exact=identity.targets(c,[unit['uid']]);refs={(fact['source_id'],fact['reference']) for fact in unit['sources']}
                source_ids=set().union(*(by_ref.get(ref,set()) for ref in refs)) if refs else set()
                virtual=set().union(*(virtual_sources.get(ref,set()) for ref in refs)) if refs else set()
                partitioned=incoming['uid']!=original['uid']
                target_ids=exact or sorted(source_ids) or ([] if partitioned else identity.targets(c,unit['aliases']))
                if len(target_ids)>1 and source_ids:target_ids=sorted(set(target_ids)&source_ids)
                action='new' if not target_ids else 'existing' if len(target_ids)==1 else 'review'
                message='New catalogue entry; source references remain unverified.' if action=='new' else 'Keep the mapped local entry and add references.'
                if action=='review':message='References identify several local entries; retained for review without combining them.'
                if len(target_ids)==1 and refs:
                    old_refs={(r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_source_facts WHERE entry_id=?',(target_ids[0],))}
                    old_refs.update((row['source_id'],row['fact']['reference']) for row in native.by_entry.get(target_ids[0],[]))
                    if old_refs and not old_refs&refs:action='review';message='A shared ID was reused with different source identities; keep local data and review this incoming version.'
                if parent_conflict and not (resolution and resolution.get('remap')):action='review';message='The parent shared ID belongs to different source records; retain this version for review.'
                previous=seen_principals.get(unit['uid'])
                if previous and previous['digest']!=wire.digest(unit) and previous['sources']!=refs:
                    action='review';message='This file reuses one shared ID for differing source records; the additional version is retained for review.'
                seen_principals[unit['uid']]={'digest':wire.digest(unit),'sources':refs}
                batch_uid=None
                if action=='new' and unit['uid'] in virtual_uids:
                    action='batch';batch_uid=unit['uid'];message='Retain another version of the same incoming entry without replacing its fields.'
                if action=='new' and virtual:
                    if len(virtual)==1:action='batch';batch_uid=next(iter(virtual));message='Use the exact source record already being added from this file.'
                    else:action='review';message='This incoming group spans several separate records in the same file; retained for review.'
                if action=='new':
                    for ref in refs:virtual_sources[ref].add(unit['uid'])
                if resolution and resolution.get('ignored'):action='ignored';message='This incoming version was explicitly ignored on this instance.'
                if action in {'new','batch'}:virtual_uids.add(unit['uid'])
                proof=[]
                for eid in target_ids:
                    row=c.execute('SELECT id,preferred_title,manual_preferred_title FROM entries WHERE id=?',(eid,)).fetchone()
                    override=c.execute('SELECT title,updated_at FROM preferred_title_overrides WHERE entry_id=?',(eid,)).fetchone()
                    proof.append([dict(row) if row else None,dict(override) if override else None])
                items.append({'uid':unit['uid'],'group_root':original['uid'] if partitioned and not parent_conflict else None,'claimed_uid':incoming['uid'],'title':unit['title'],'action':action,'batch_uid':batch_uid,'target_ids':target_ids,
                              'message':message,'record':unit,'incoming':incoming,'proof':proof})
        decisions=[]
        resolver=lambda record:self._resolve(native,record)
        for choice in bundle['decisions']:
            old=c.execute('SELECT state,message FROM catalogue_decisions WHERE uid=? AND digest=?',(choice['uid'],wire.digest(choice))).fetchone()
            status=dict(old) if old and old['state'] in {'applied','ignored'} else choices.inspect(c,self.service,choice,resolver)
            decisions.append({'uid':choice['uid'],'digest':wire.digest(choice),'kind':choice['kind'],**status})
        policies=[tuple(r) for r in c.execute('SELECT source_id,revision FROM gallery_policies ORDER BY source_id')]
        projection=[{**{k:v for k,v in item.items() if k not in {'record','incoming'}},'record_digest':wire.digest(item['record'])} for item in items]
        revision=wire.digest([wire.digest(bundle),projection,decisions,policies])
        return {'revision':revision,'items':items,'decisions':decisions,'added':sum(r['action']=='new' for r in items),
                'reused':sum(r['action'] in {'existing','batch'} for r in items),'retained':sum(r['action']=='review' for r in items)}

    def _already_added(self,c,bundle,plan):
        receipt=c.execute('SELECT result_json FROM catalogue_exchange_receipts WHERE digest=?',(wire.digest(bundle),)).fetchone()
        if not receipt:return None
        for item in plan['items']:
            if not c.execute('SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=?',(item['uid'],)).fetchone():
                incoming=item['incoming'];known=c.execute('SELECT state FROM catalogue_import_records WHERE uid=? AND digest=?',(incoming['uid'],wire.digest(incoming))).fetchone()
                if not known or known['state'] not in {'review','ignored'}:return None
        previous=json.loads(receipt[0])
        return {**previous,'added':0,'decisions_applied':0,'already_applied':True,
                'entry_ids':sorted({eid for item in plan['items'] for eid in identity.targets(c,[item['uid']])})}

    @staticmethod
    def _public_plan(plan,offset=0,limit=100):
        return {**plan,'items':[{k:v for k,v in row.items() if k not in {'record','incoming','proof'}} for row in plan['items'][offset:offset+limit]],
                'decisions':plan['decisions'][:100],'item_total':len(plan['items']),'decision_total':len(plan['decisions']),'offset':offset,'limit':limit}

    def preview(self,raw):
        bundle=wire.decode(raw)
        with self.service._catalog_maintenance_lock,self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE');plan=self._plan(c,bundle);stage_id=uuid.uuid4().hex
            c.execute('DELETE FROM catalogue_exchange_stages WHERE created_at<?',(time.time()-86400,))
            rows=c.execute('SELECT id FROM catalogue_exchange_stages ORDER BY created_at DESC').fetchall()
            for row in rows[3:]:c.execute('DELETE FROM catalogue_exchange_stages WHERE id=?',(row[0],))
            c.execute('INSERT INTO catalogue_exchange_stages(id,payload_json,revision,created_at) VALUES(?,?,?,?)',(stage_id,wire.canonical(bundle),plan['revision'],time.time()))
        with self.db.catalog() as c:already=self._already_added(c,bundle,plan)
        return {'id':stage_id,**self._public_plan(plan),'already_applied':bool(already)}

    def _stage(self,c,stage_id):
        row=c.execute('SELECT * FROM catalogue_exchange_stages WHERE id=?',(stage_id,)).fetchone()
        if not row or row['created_at']<time.time()-86400:raise ValueError('Catalogue preview expired; select the file again')
        return row,json.loads(row['payload_json'])

    def stage(self,stage_id,*,offset=0,limit=100):
        with self.db.catalog() as c:
            row,bundle=self._stage(c,stage_id)
            if row['result_json']:return {'id':stage_id,'state':'applied','result':json.loads(row['result_json'])}
            plan=self._plan(c,bundle)
            return {'id':stage_id,'state':'preview' if row['revision']==plan['revision'] else 'stale',**self._public_plan(plan,offset,limit)}

    def _add(self,c,record):
        data=record['metadata'];stamp=now_iso()
        cur=c.execute('''INSERT INTO entries(preferred_title,description,author,artist,genre,status,created_at,updated_at)
                         VALUES(?,?,?,?,?,?,?,?)''',(record['title'],data['description'],data['author'],data['artist'],data['genre'],data['status'],stamp,stamp))
        eid=int(cur.lastrowid);identity.ensure(c,eid,record['uid']);c.execute('INSERT INTO catalogue_shared_entries VALUES(?)',(eid,));return eid

    def _associate(self,c,eid,record):
        from .matcher import normalize_title
        identity.ensure(c,eid);identity.link(c,eid,[record['uid'],*record['aliases']])
        for item in record['lineage']:
            for source in item['records']:c.execute('INSERT OR IGNORE INTO catalogue_uid_records VALUES(?,?,?)',(item['uid'],source['source_id'],source['reference']))
        primary=identity.ensure(c,eid)
        c.execute('INSERT OR IGNORE INTO catalogue_uid_records SELECT ?,source_id,reference FROM catalogue_record_bindings WHERE entry_id=?',(primary,eid))
        for ref in set(record['references'])|{r['reference'] for r in record['sources']}:c.execute('INSERT OR IGNORE INTO catalogue_public_refs VALUES(?,?)',(eid,ref))
        for fact in record['sources']:c.execute('INSERT OR IGNORE INTO catalogue_source_facts VALUES(?,?,?,?)',(eid,fact['reference'],fact['source_id'],wire.canonical(fact)))
        for title in dict.fromkeys([record['title'],*record['titles']]):
            if title.strip():c.execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,title_type) VALUES(?,?,?,'cached')",(eid,title,normalize_title(title)))

    def apply(self,stage_id,revision,*,include_decisions=True):
        with self.service._catalog_maintenance_lock,self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE');stage,bundle=self._stage(c,stage_id);plan=self._plan(c,bundle)
            if stage['result_json'] and stage['revision']==revision:
                return {**json.loads(stage['result_json']),'added':0,'already_applied':True}
            if stage['revision']!=revision or plan['revision']!=revision:raise ValueError('Catalogue or local choices changed; refresh the preview before applying')
            already=self._already_added(c,bundle,plan)
            if already:
                c.execute('UPDATE catalogue_exchange_stages SET result_json=? WHERE id=?',(wire.canonical(already),stage_id))
                return already
            result={'added':0,'reused':0,'retained':0,'decisions_applied':0,'decisions_pending':0,'entry_ids':[]}
            for item in plan['items']:
                record=item['record'];incoming=item['incoming'];key=(incoming['uid'],wire.digest(incoming));state='review'
                if item['action']=='ignored':state='ignored'
                elif item['action']=='review':result['retained']+=1
                else:
                    eid=self._add(c,record) if item['action']=='new' else identity.targets(c,[item['batch_uid']])[0] if item['action']=='batch' else item['target_ids'][0]
                    result['added' if item['action']=='new' else 'reused']+=1;result['entry_ids'].append(eid)
                    self._associate(c,eid,record);state='imported'
                c.execute('''INSERT INTO catalogue_import_records VALUES(?,?,?,?,?) ON CONFLICT(uid,digest)
                             DO UPDATE SET state=excluded.state,message=excluded.message''',(*key,wire.canonical(incoming),state,item['message']))
            identity.refresh_groups(c,[item['group_root'] for item in plan['items'] if item['group_root'] and item['action'] not in {'review','ignored'}])
            native,_=self._native(c);resolver=lambda record:[self._full(c,row) for row in self._resolve(native,record)]
            for decision in bundle['decisions']:
                digest=wire.digest(decision);old=c.execute('SELECT state FROM catalogue_decisions WHERE uid=? AND digest=?',(decision['uid'],digest)).fetchone()
                status={'state':old['state'],'message':'Previously retained local result.'} if old and old['state'] in {'applied','ignored'} else choices.inspect(c,self.service,decision,resolver)
                if include_decisions and status['state']=='ready':status=choices.apply_ready(c,self.db,self.service,decision,resolver)
                result['decisions_applied' if status['state']=='applied' else 'decisions_pending']+=1
                c.execute('''INSERT INTO catalogue_decisions(uid,digest,decision_json,state,message) VALUES(?,?,?,?,?)
                             ON CONFLICT(uid,digest) DO UPDATE SET state=excluded.state,message=excluded.message''',(decision['uid'],digest,wire.canonical(decision),status['state'],status['message']))
            encoded=wire.canonical(result)
            c.execute('UPDATE catalogue_exchange_stages SET result_json=? WHERE id=?',(encoded,stage_id))
            c.execute('INSERT OR REPLACE INTO catalogue_exchange_receipts VALUES(?,?,?)',(wire.digest(bundle),encoded,now_iso()))
        self.service._invalidate_discovery_caches(hard=False)
        return result

    def decisions(self,*,offset=0,limit=30):
        with self.db.catalog() as c:
            native,_=self._native(c)
            rows=c.execute('SELECT * FROM catalogue_decisions ORDER BY rowid DESC LIMIT ? OFFSET ?',(limit,offset)).fetchall()
            # _decision_view inspects each pending decision once; inspection is read-only.
            items=[self._decision_view(c,dict(row),native) for row in rows]
            return {'items':items,'total':c.execute('SELECT COUNT(*) FROM catalogue_decisions').fetchone()[0],'offset':offset,'limit':limit}

    def _decision_view(self,c,row,native):
        decision=json.loads(row['decision_json']);resolver=lambda record:self._resolve(native,record)
        status={'state':row['state'],'message':row['message']} if row['state'] in {'applied','ignored'} else choices.inspect(c,self.service,decision,resolver)
        mapped=[identity.targets(c,[value]) for value in decision['entries']]
        records=[resolver(value) for value in decision['records']]
        proof=[]
        for record in [value for rows in records for value in rows]:
            binding=c.execute('SELECT * FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?',(record['source_id'],record['source_manga_id'])).fetchone()
            proof.append([record['entry_id'],record['source_id'],record['source_manga_id'],record['fact']['reference'],dict(binding) if binding else None])
        policies=[tuple(r) for r in c.execute('SELECT source_id,revision FROM gallery_policies ORDER BY source_id')]
        can_apply=status['state']=='ready' or status['state']=='pending' and decision['kind'] in {'group','source-match','separate'} and bool(records) and all(len(values)==1 for values in records)
        if decision['kind']=='group':
            for value in [r for values in records for r in values]:
                if value['fact']['gallery'] and not gallery_policy.effective(self.db,value['source_id'])['enabled']:
                    can_apply=False
        return {'uid':row['uid'],'digest':row['digest'],'kind':decision['kind'],'decision':decision,**status,
                'can_apply':can_apply,
                'entry_ids':sorted({eid for values in mapped for eid in values}),
                'local_records':[{'entry_id':r['entry_id'],'title':r['source_title'],'source':r.get('source_name') or r['source_id']} for values in records for r in values],
                'revision':wire.digest([row,status,mapped,proof,policies])}

    def decision(self,uid,digest):
        with self.db.catalog() as c:
            row=c.execute('SELECT * FROM catalogue_decisions WHERE uid=? AND digest=?',(uid,digest)).fetchone()
            if not row:raise KeyError(uid)
            native,_=self._native(c);return self._decision_view(c,dict(row),native)

    def apply_decision(self,uid,digest,revision,*,action='apply',task_id=None):
        """Explicit structural choices use the established recoverable merge/split path."""
        from .task_registry import task_registry
        wire.check_uid(uid,'decision')
        if action not in {'apply','ignore'}:raise ValueError('Choose Apply or Keep local')
        with self.service._catalog_maintenance_lock:
            with self.db.catalog() as c:
                row=c.execute('SELECT * FROM catalogue_decisions WHERE uid=? AND digest=?',(uid,digest)).fetchone()
                if not row:raise KeyError(uid)
                native,_=self._native(c);view=self._decision_view(c,dict(row),native)
                if view['revision']!=revision:raise ValueError('Local records or this retained choice changed; refresh before applying')
                if row['state'] in {'applied','ignored'}:return view
                decision=view['decision'];resolver=lambda record:[self._full(c,r) for r in self._resolve(native,record)]
                if action=='ignore':status={'state':'ignored','message':'Local choices retained; incoming evidence remains in history.'}
                elif view['state']=='conflict':raise ValueError(view['message'])
                elif view['state']=='ready':status=choices.apply_ready(c,self.db,self.service,decision,resolver)
                else:status=None
                if status:
                    c.execute('UPDATE catalogue_decisions SET state=?,message=?,applied_revision=? WHERE uid=? AND digest=?',
                              (status['state'],status['message'],revision,uid,digest))
                    self.service._invalidate_discovery_caches(hard=False)
                    return {**view,**status}
                resolved=[resolver(value) for value in decision['records']]
                if not resolved or any(len(values)!=1 for values in resolved):raise ValueError('Resolve each public source reference to exactly one local record before applying this choice')
                records=[values[0] for values in resolved];kind=decision['kind']
                owners=sorted({int(r['entry_id']) for r in records})
                targets=identity.targets(c,decision['entries'])
                if len(owners)>32:raise ValueError('Review this large group in smaller local groups before applying it')
                if kind=='group' and len(records)>64:raise ValueError('Review this large group in smaller local groups of at most 64 records')
                if kind not in {'group','source-match','separate'}:raise ValueError(view['message'])
                if kind=='group':
                    for record in records:
                        if c.execute('SELECT 1 FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?',(record['source_id'],record['source_manga_id'])).fetchone():raise ValueError('A local manual source binding is retained; review it before combining imported members')
                c.execute("UPDATE catalogue_decisions SET state='pending',message='Applying the reviewed local record mapping',applied_revision=? WHERE uid=? AND digest=?",(revision,uid,digest))
            # No catalogue transaction spans an origins write. Existing merge/split
            # journals own cross-file recovery; cancellation drains the worker.
            if kind=='group':
                payloads=[self.service._gallery_record_payload(r) for r in records]
                from . import gallery_policy
                gallery_policy.grouping_gate(self.db,payloads)
                for index,left in enumerate(owners):
                    for right in owners[index+1:]:
                        if task_id and task_registry.cancel_requested(task_id):raise RuntimeError('Cancelled by user')
                        if self.service._duplicate_review(left,right)=='not_duplicate':raise ValueError('A local Separate decision is retained')
                        conflict=self.service._snapshot_edition_conflict(self.service._entry_duplicate_evidence_snapshot(left),self.service._entry_duplicate_evidence_snapshot(right))
                        if conflict:raise ValueError(conflict)
                if any(p.get('_gallery_metadata') for p in payloads):
                    for index,left in enumerate(payloads):
                        for right in payloads[index+1:]:
                            if task_id and task_registry.cancel_requested(task_id):raise RuntimeError('Cancelled by user')
                            evidence=self.service._gallery_evidence(left,[right])
                            if not evidence or not evidence.get('eligible'):raise ValueError('Current native gallery evidence and source criteria do not support this imported group')
                target=owners[0]
                for other in owners[1:]:
                    if task_id and task_registry.cancel_requested(task_id):raise RuntimeError('Cancelled by user')
                    target=self.service.merge_entries(target,other,'reviewed-catalogue-group')
                with self.db.catalog() as c:identity.link(c,target,decision['entries'])
            elif kind=='source-match':
                if len(targets)!=1:raise ValueError('Choose one mapped local target before applying this source decision')
                target=targets[0];record=records[0]
                if record['entry_id']!=target:
                    with self.db.catalog() as c:
                        common=c.execute('SELECT 1 FROM catalogue_source_facts WHERE entry_id=? AND source_id=? AND reference=?',(target,record['source_id'],record['fact']['reference'])).fetchone()
                    if not identity.unverified(self.db,target) or not common:raise ValueError('This move needs an explicit local Match matrix review')
                    self.service.merge_entries(target,record['entry_id'],'reviewed-catalogue-source')
            else:
                if len(targets)<1:raise ValueError('The target entry is not mapped locally')
                target=targets[0];record=records[0]
                if record['entry_id']==target:self.service.unmatch_duplicate_records(target,[{'source_id':record['source_id'],'source_manga_id':record['source_manga_id']}])
            with self.db.catalog() as c:
                native,_=self._native(c);resolver=lambda record:[self._full(c,r) for r in self._resolve(native,record)]
                status=choices.apply_ready(c,self.db,self.service,decision,resolver)
                if kind=='source-match' and status['state']=='conflict':
                    mapped=identity.targets(c,decision['entries']);resolved=[resolver(value) for value in decision['records']]
                    if len(mapped)==1 and resolved and all(len(values)==1 and values[0]['entry_id']==mapped[0] for values in resolved):
                        status={'state':'applied','message':'Verified local record linked; its existing manual binding was retained.'}
                if status['state']=='applied':
                    mapped=identity.targets(c,decision['entries'])
                    if len(mapped)==1:
                        actual={(r['source_id'],r['fact']['reference']) for r in native.by_entry.get(mapped[0],[])}
                        for retained in c.execute("SELECT uid,digest,record_json FROM catalogue_import_records WHERE state='review'").fetchall():
                            record=json.loads(retained['record_json'])
                            if record['uid'] not in decision['entries']:continue
                            if {(r['source_id'],r['reference']) for r in record['sources']}<=actual:
                                self._associate(c,mapped[0],record)
                                c.execute("UPDATE catalogue_import_records SET state='imported',message='Mapped through a reviewed local group' WHERE uid=? AND digest=?",(retained['uid'],retained['digest']))
                c.execute('UPDATE catalogue_decisions SET state=?,message=?,applied_revision=? WHERE uid=? AND digest=?',
                          (status['state'],status['message'],revision,uid,digest))
            self.service._invalidate_discovery_caches(hard=False)
            return status

    def records(self,*,offset=0,limit=30):
        with self.db.catalog() as c:
            native,_=self._native(c)
            rows=c.execute('SELECT * FROM catalogue_import_records ORDER BY rowid DESC LIMIT ? OFFSET ?',(limit,offset)).fetchall()
            items=[]
            for row in rows:
                record=json.loads(row['record_json']);resolution=self._resolution(c,record);current=self._resolved_record(record,resolution)
                targets=identity.targets(c,[current['uid']]);policies=[tuple(r) for r in c.execute('SELECT source_id,revision FROM gallery_policies ORDER BY source_id')]
                candidates=sorted({int(r['entry_id']) for fact in record['sources'] for r in self._resolve(native,fact)})
                items.append({**dict(row),'record':record,'resolved_uid':current['uid'],'entry_ids':targets,
                              'local_candidates':[{'id':eid,'title':c.execute('SELECT preferred_title FROM entries WHERE id=?',(eid,)).fetchone()[0]} for eid in candidates],
                              'can_link':len(targets)==1 and identity.unverified(self.db,targets[0]),
                              'revision':wire.digest([dict(row),targets,resolution,policies,candidates])})
            return {'items':items,'total':c.execute('SELECT COUNT(*) FROM catalogue_import_records').fetchone()[0],'offset':offset,'limit':limit}

    def review_record(self,uid,digest,revision,*,action='separate'):
        if action not in {'separate','ignore'}:raise ValueError('Choose Add separately or Keep local')
        with self.service._catalog_maintenance_lock,self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE');row=c.execute('SELECT * FROM catalogue_import_records WHERE uid=? AND digest=?',(uid,digest)).fetchone()
            if not row:raise KeyError(uid)
            record=json.loads(row['record_json']);resolution=self._resolution(c,record);current=self._resolved_record(record,resolution)
            targets=identity.targets(c,[current['uid']]);policies=[tuple(r) for r in c.execute('SELECT source_id,revision FROM gallery_policies ORDER BY source_id')]
            native,_=self._native(c);candidates=sorted({int(r['entry_id']) for fact in record['sources'] for r in self._resolve(native,fact)})
            if wire.digest([dict(row),targets,resolution,policies,candidates])!=revision:raise ValueError('Incoming version or local gallery rules changed; refresh before reviewing')
            if row['state']!='review':return {'added':0,'state':row['state'],'entry_ids':targets}
            created=[]
            if action=='ignore':resolution={'ignored':True};state='ignored';message='Local records retained; incoming version kept in history.'
            else:
                for unit in self._units(record):
                    key=self._source_keys(unit) or wire.digest(unit)
                    chosen='asc1:entry:autosourcarr:'+str(uuid.uuid5(uuid.UUID(unit['uid'].rsplit(':',1)[-1]),'separate:'+wire.canonical(key)))
                    remap={'uid':chosen,'aliases':[chosen],'lineage':[{'uid':chosen,'records':[{'source_id':r['source_id'],'reference':r['reference']} for r in unit['sources']]}]}
                    value={**unit,**remap};existing=identity.targets(c,[chosen])
                    eid=existing[0] if len(existing)==1 else self._add(c,value)
                    self._associate(c,eid,value);created.append(eid)
                    part_resolution={'remap':remap,'source_keys':self._source_keys(unit)}
                    c.execute('INSERT OR REPLACE INTO catalogue_record_resolutions VALUES(?,?,?)',(unit['uid'],wire.digest(unit),wire.canonical(part_resolution)))
                    if unit['uid']==record['uid']:resolution=part_resolution
                state='imported';message='Added separately under a new portable identity; original IDs and local records are unchanged.'
            if resolution:c.execute('INSERT OR REPLACE INTO catalogue_record_resolutions VALUES(?,?,?)',(uid,digest,wire.canonical(resolution)))
            c.execute('UPDATE catalogue_import_records SET state=?,message=? WHERE uid=? AND digest=?',(state,message,uid,digest))
        self.service._invalidate_discovery_caches(hard=False)
        return {'added':len(created),'state':state,'entry_ids':created}

    def prepare_link(self,uid,digest,revision,target):
        with self.service._catalog_maintenance_lock,self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE');row=c.execute('SELECT * FROM catalogue_import_records WHERE uid=? AND digest=?',(uid,digest)).fetchone()
            if not row:raise KeyError(uid)
            record=json.loads(row['record_json']);resolution=self._resolution(c,record);current=self._resolved_record(record,resolution)
            targets=identity.targets(c,[current['uid']]);native,_=self._native(c)
            candidates=sorted({int(r['entry_id']) for fact in record['sources'] for r in self._resolve(native,fact)})
            policies=[tuple(r) for r in c.execute('SELECT source_id,revision FROM gallery_policies ORDER BY source_id')]
            if wire.digest([dict(row),targets,resolution,policies,candidates])!=revision:raise ValueError('Local source records changed; refresh before linking')
            if len(targets)!=1 or not identity.unverified(self.db,targets[0]) or target not in candidates:raise ValueError('Choose a verified counterpart for one unverified local entry')
            if any(eid!=target for eid in candidates):raise ValueError('The references span several local records; review their grouping first')
            local_title=c.execute('SELECT title FROM preferred_title_overrides WHERE entry_id=?',(target,)).fetchone()
            imported_title=c.execute('SELECT title FROM preferred_title_overrides WHERE entry_id=?',(targets[0],)).fetchone()
            if local_title and imported_title and local_title[0]!=imported_title[0]:raise ValueError('The two manual titles differ; retain the native local title and review the imported override before linking')
            fact=next(f for f in current['sources'] if any(r['entry_id']==target for r in self._resolve(native,f)))
            choice={'uid':wire.uid('decision'),'kind':'source-match','entries':[current['uid']],'records':[fact],
                    'value':{'note':'Verified local counterpart explicitly selected in Database sharing'}}
            wire.decision(choice);choice_digest=wire.digest(choice)
            c.execute('INSERT INTO catalogue_decisions(uid,digest,decision_json,state,message) VALUES(?,?,?,?,?)',
                      (choice['uid'],choice_digest,wire.canonical(choice),'pending','Verified local source link requested'))
            stored=c.execute('SELECT * FROM catalogue_decisions WHERE uid=? AND digest=?',(choice['uid'],choice_digest)).fetchone()
            return self._decision_view(c,dict(stored),native)

    def status(self):
        with self.db.catalog() as c:
            origin=c.execute('SELECT uid FROM catalogue_origin WHERE id=1').fetchone()
            return {'origin':origin[0] if origin else None,'entries':c.execute('SELECT COUNT(*) FROM entries').fetchone()[0],
                    'files_added':c.execute('SELECT COUNT(*) FROM catalogue_exchange_receipts').fetchone()[0],
                    'retained_records':c.execute("SELECT COUNT(*) FROM catalogue_import_records WHERE state='review'").fetchone()[0],
                    'pending_decisions':c.execute("SELECT COUNT(*) FROM catalogue_decisions WHERE state NOT IN ('applied','ignored')").fetchone()[0],
                    'note':'Catalogue sharing adds records and portable decisions. Existing local reader IDs, accounts, progress, downloaded files and source settings stay local.'}
