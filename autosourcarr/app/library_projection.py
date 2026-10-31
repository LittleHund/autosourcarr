"""Local, globally filtered library projection with stable keyset pages."""
import base64
import hashlib
import json
from .runtime_observability import timed


def fingerprint(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()[:24]


def shown_groups(sources):
    """The same language families/custom groups shown in the source controls.

    Counts of unrelated or disabled sources cannot stand in for membership.
    A shared hostname alone is deliberately not a source identity.
    """
    families={}
    for source in sources:
        if not (source['available'] and source['enabled_language'] and
                (source['enabled_search'] or source['enabled_chapters'])):
            continue
        key=((source.get('name') or source['source_id']).strip().lower(),
             (source.get('base_url') or '').strip().lower())
        families.setdefault(key,[]).append(source)
    groups={}
    for key,variants in families.items():
        assigned={s.get('group_id') for s in variants}
        group=('custom',next(iter(assigned))) if len(assigned)==1 and None not in assigned else ('family',key)
        groups.setdefault(group,[]).extend(str(s['source_id']) for s in variants)
    return [sorted(values) for _,values in sorted(groups.items(),key=lambda item:str(item[0]))]


def canonical_snapshot(service,payload):
    """Keep a cached old main-library URL visible after an exact catalog merge."""
    stats={}
    for key,value in (payload.get('stats_by_entry') or {}).items():
        canonical=str(service.canonical_entry_id(int(key)))
        existing=stats.get(canonical,{})
        mapped={**value,'entry_id':int(canonical)}
        for field in ('chapter_count','downloaded_chapter_count'):
            counts=[v for v in (existing.get(field),mapped.get(field)) if v is not None]
            mapped[field]=max(counts) if counts else None
        stats[canonical]=mapped
    return {**payload,'entry_ids':service.canonical_entry_ids(payload.get('entry_ids') or []),'stats_by_entry':stats}


@timed('projection')
def page(database, subscription, *, search='', source='', scope='all', sort='updated',
         publication='any', title_state='any', match_status='any', cursor='', limit=60,
         duplicates=(),source_count='',record_count=''):
    if sort not in {'title','updated','matches'}: raise ValueError('Unknown library sort')
    if scope not in {'all','catalog','missing_downloads','source_gaps','unmatched','verification','monitored'}:
        raise ValueError('Unknown library view')
    limit=max(1,min(120,int(limit)))
    query={'search':search[:200], 'source':source[:200], 'scope':scope,'sort':sort,
           'publication':publication,'title_state':title_state,'match_status':match_status}
    query.update(source_count=source_count,record_count=record_count)
    ids=list(dict.fromkeys(int(i) for i in subscription.get('entry_ids',[]) if int(i)>0))
    with database.catalog() as conn:
        sources=[dict(r) for r in conn.execute('SELECT source_id,name,base_url,lang,group_id,priority,available,enabled_language,enabled_search,enabled_chapters FROM source_config ORDER BY name,lang')]
    groups=shown_groups(sources)
    stamp=fingerprint([query, sorted(ids),groups])
    args={**query,'ids':json.dumps(ids),'limit':limit+1,
          'groups':json.dumps(groups),
          'duplicates':json.dumps(list(duplicates))}
    conditions=[]
    if scope!='catalog': conditions.append('e.id IN (SELECT value FROM json_each(:ids))')
    if search:
        args['term']='%'+search[:200].replace('\\','\\\\').replace('%','\\%').replace('_','\\_')+'%'
        conditions.append("(e.preferred_title LIKE :term ESCAPE '\\' OR EXISTS(SELECT 1 FROM titles t WHERE t.entry_id=e.id AND t.title LIKE :term ESCAPE '\\'))")
    if source:
        conditions.append('EXISTS(SELECT 1 FROM records r WHERE r.entry_id=e.id AND r.source_id=:source)')
    for value,column,keyname in [(source_count,'source_count','exact_source_count'),(record_count,'match_count','exact_record_count')]:
        if value!='':
            try:
                args[keyname]=int(value)
                if not 0<=args[keyname]<=1000000000:raise ValueError()
            except (TypeError,ValueError) as exc:raise ValueError('Invalid exact match count') from exc
            conditions.append('COALESCE(m.'+column+',0)=:'+keyname)
    if title_state in {'manual','automatic'}:
        conditions.append('e.manual_preferred_title='+('1' if title_state=='manual' else '0'))
    # Preserve the existing Suwayomi/Tachiyomi integer contract. Publishing
    # finished (4) shares the Finished filter with Completed (2); Hiatus is 6.
    statuses={'ongoing':(1,),'finished':(2,4),'licensed':(3,),'hiatus':(6,),'cancelled':(5,),'unknown':(0,)}
    if publication in statuses:
        args['status']=json.dumps(statuses[publication]); conditions.append('e.status IN (SELECT value FROM json_each(:status))')
    if scope=='unmatched': conditions.append('COALESCE(m.match_count,0)=0')
    if scope=='monitored': conditions.append('lt.monitored=1')
    if scope=='source_gaps': conditions.append("json_extract(lt.observation_json,'$.coverage_state')='checked' AND json_extract(lt.observation_json,'$.source_gaps')>0")
    if scope=='missing_downloads': conditions.append("json_extract(lt.observation_json,'$.download_state')='checked' AND json_extract(lt.observation_json,'$.missing_downloads')>0")
    if scope=='verification': conditions.append("(lt.entry_id IS NULL OR COALESCE(json_extract(lt.observation_json,'$.download_state'),'unknown')<>'checked' OR COALESCE(json_extract(lt.observation_json,'$.coverage_state'),'unknown')<>'checked')")
    if match_status=='manual': conditions.append('m.manual_count>0')
    if match_status=='multiple_records': conditions.append('m.match_count>m.source_count')
    if match_status=='duplicates': conditions.append('e.id IN (SELECT value FROM json_each(:duplicates))')
    if match_status in {'gaps','full'}:
        missing='EXISTS(SELECT 1 FROM json_each(:groups) g WHERE NOT EXISTS(SELECT 1 FROM records r WHERE r.entry_id=e.id AND r.source_id IN(SELECT value FROM json_each(g.value))))'
        conditions.append(('json_array_length(:groups)>0 AND NOT '+missing) if match_status=='full' else missing)
    where=' AND '.join(conditions) or '1'
    cte='''WITH records AS (
      SELECT entry_id,source_id,locked FROM source_matches WHERE active=1
      UNION ALL SELECT entry_id,source_id,locked FROM source_variants WHERE active=1
    ), matched AS (SELECT entry_id,COUNT(*) match_count,COUNT(DISTINCT source_id) source_count,
       SUM(locked) manual_count FROM records GROUP BY entry_id), filtered AS (
      SELECT e.id,e.preferred_title,substr(e.description,1,420) description,e.thumbnail_url,e.status,e.updated_at,e.manual_preferred_title,
       COALESCE(m.match_count,0) match_count,COALESCE(m.source_count,0) source_count,
       COALESCE(lt.monitored,0) monitored,COALESCE(lt.revision,0) revision,
       COALESCE(lt.observation_json,'{}') observation_json,
       lower(e.preferred_title) title_key
      FROM entries e LEFT JOIN matched m ON m.entry_id=e.id
      LEFT JOIN library_tracking lt ON lt.entry_id=e.id WHERE '''+where+') '
    order={'title':('title_key','ASC','>'),'updated':('updated_at','DESC','<'),'matches':('source_count','DESC','<')}[sort]
    key,direction,comparison=order
    after=''
    if cursor:
        try:
            data=json.loads(base64.urlsafe_b64decode(cursor.encode()).decode())
            if data['stamp']!=stamp or type(data['id']) is not int: raise ValueError()
            args.update(after_key=data['key'],after_id=data['id'])
        except (ValueError,KeyError,TypeError,UnicodeError) as exc: raise ValueError('Library changed; start again from the first page') from exc
        after=f'WHERE ({key},id) {comparison} (:after_key,:after_id)'
    with database.catalog() as conn:
        aggregate=(sort=='matches' or source_count!='' or record_count!='' or scope=='unmatched' or
                   match_status in {'manual','multiple_records'})
        if aggregate:
            total=conn.execute(cte+'SELECT COUNT(*) FROM filtered',args).fetchone()[0]
            rows=[dict(r) for r in conn.execute(cte+f'SELECT * FROM filtered {after} ORDER BY {key} {direction},id {direction} LIMIT :limit',args)]
        else:
            # Filters and the exact total still cover the entire requested library.
            # Only match aggregation and metadata hydration are limited to its page.
            base='''WITH records AS (
              SELECT entry_id,source_id,locked FROM source_matches WHERE active=1
              UNION ALL SELECT entry_id,source_id,locked FROM source_variants WHERE active=1
            ), filtered AS (
              SELECT e.id,e.preferred_title,substr(e.description,1,420) description,e.thumbnail_url,e.status,e.updated_at,e.manual_preferred_title,
               COALESCE(lt.monitored,0) monitored,COALESCE(lt.revision,0) revision,
               COALESCE(lt.observation_json,'{}') observation_json,lower(e.preferred_title) title_key
              FROM entries e LEFT JOIN library_tracking lt ON lt.entry_id=e.id WHERE '''+where+') '
            total=conn.execute(base+'SELECT COUNT(*) FROM filtered',args).fetchone()[0]
            select=base+f''', selected AS (SELECT * FROM filtered {after} ORDER BY {key} {direction},id {direction} LIMIT :limit),
              matched AS (SELECT entry_id,COUNT(*) match_count,COUNT(DISTINCT source_id) source_count
                FROM records WHERE entry_id IN(SELECT id FROM selected) GROUP BY entry_id)
              SELECT s.*,COALESCE(m.match_count,0) match_count,COALESCE(m.source_count,0) source_count
              FROM selected s LEFT JOIN matched m ON m.entry_id=s.id ORDER BY s.{key} {direction},s.id {direction}'''
            rows=[dict(r) for r in conn.execute(select,args)]
    more=len(rows)>limit; rows=rows[:limit]
    next_cursor=''
    if more and rows:
        last=rows[-1]
        next_cursor=base64.urlsafe_b64encode(json.dumps({'stamp':stamp,'key':last[key],'id':last['id']},separators=(',',':')).encode()).decode()
    for row in rows:
        row['status_label']={0:'Unknown',1:'Ongoing',2:'Completed',3:'Licensed',4:'Publishing finished',5:'Cancelled',6:'Hiatus'}.get(row['status'],'Unknown')
        row['entry_match_count']=row['match_count']
        row['match_count']=row['source_count']
        row['observation']=json.loads(row.pop('observation_json'))
        row['library_stats']=(subscription.get('stats_by_entry') or {}).get(str(row['id']),{})
        row.pop('title_key',None)
    with database.catalog() as c:
        page_ids=json.dumps([r['id'] for r in rows])
        records=[dict(r) for r in c.execute('''SELECT entry_id,source_id,source_title,locked FROM source_matches WHERE active=1 AND entry_id IN(SELECT value FROM json_each(?))
         UNION ALL SELECT entry_id,source_id,source_title,locked FROM source_variants WHERE active=1 AND entry_id IN(SELECT value FROM json_each(?))''',(page_ids,page_ids))]
        clocks={r['work_key']:dict(r) for r in c.execute("SELECT * FROM due_work WHERE kind='library' AND work_key IN(SELECT CAST(value AS TEXT) FROM json_each(?))",(page_ids,))}
        complete={r['entry_id']:r['observed_at'] for r in c.execute('SELECT entry_id,observed_at FROM library_complete_observations WHERE entry_id IN(SELECT value FROM json_each(?))',(page_ids,))}
    for row in rows:
        row['due']=clocks.get(str(row['id']),{})
        row['last_complete_observed_at']=complete.get(row['id'],'')
    from .metadata_fields import overlay_many
    rows=overlay_many(database,rows)
    return {'entries':rows,'total':total,'next_cursor':next_cursor,'limit':limit,'sources':sources,'matches':records,'variants':[],
            'subscription':{k:v for k,v in subscription.items() if k not in {'items','entry_ids','stats_by_entry'}},'scope':scope}
