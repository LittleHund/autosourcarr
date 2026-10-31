"""Presentation ownership only. Core matching reads original source evidence."""
import json
from .db import now_iso

FIELDS={'description':'description','artwork':'thumbnail_url','author':'author','artist':'artist'}


def read(database,entry_id):
    with database.catalog() as c:
        if not c.execute('SELECT 1 FROM entries WHERE id=?',(entry_id,)).fetchone():raise KeyError(entry_id)
        row=c.execute('SELECT * FROM display_metadata WHERE entry_id=?',(entry_id,)).fetchone()
        history=[{**dict(r),'fields':json.loads(r['fields_json'])} for r in c.execute(
            'SELECT * FROM display_metadata_history WHERE entry_id=? ORDER BY id DESC LIMIT 50',(entry_id,))]
    return {'entry_id':entry_id,'revision':row['revision'] if row else 0,
            'fields':json.loads(row['fields_json']) if row else {},'history':history}


def record(connection,entry_id,sid,mid):
    for table in ('source_matches','source_variants'):
        row=connection.execute(f'SELECT description,thumbnail_url,author,artist FROM {table} WHERE entry_id=? AND source_id=? AND source_manga_id=? AND active=1',(entry_id,str(sid),int(mid))).fetchone()
        if row:return dict(row)
    return None


def save(database,service,entry_id,patch,revision,*,restore_id=None):
    if not isinstance(patch,dict) or set(patch)-FIELDS.keys():raise ValueError('Unknown display field')
    if restore_id is not None and patch:raise ValueError('Restore a snapshot separately from field edits')
    with service._catalog_maintenance_lock, database.catalog() as c:
        c.execute('BEGIN IMMEDIATE');old=read(database,entry_id)
        if old['revision']!=revision:raise ValueError('Metadata ownership changed; reload before saving')
        fields=dict(old['fields'])
        if restore_id is not None:
            row=c.execute('SELECT fields_json FROM display_metadata_history WHERE entry_id=? AND id=?',(entry_id,restore_id)).fetchone()
            if not row:raise ValueError('Metadata snapshot is unavailable')
            fields=json.loads(row[0])
        else:
            for name,choice in patch.items():
                if not isinstance(choice,dict) or choice.get('mode') not in {'inherited','locked','source'} or set(choice)-{'mode','value','clear','source_id','source_manga_id'}:raise ValueError('Invalid field ownership')
                mode=choice['mode']
                if mode=='inherited':fields.pop(name,None);continue
                if mode=='source':
                    sid=choice.get('source_id');mid=choice.get('source_manga_id')
                    if not isinstance(sid,str) or type(mid) is not int or not record(c,entry_id,sid,mid):raise ValueError('Select an attached source record')
                    fields[name]={'mode':'source','source_id':sid,'source_manga_id':mid};continue
                if type(choice.get('clear',False)) is not bool:raise ValueError('Clear must be explicit')
                value=choice.get('value')
                if choice.get('clear'):value=''
                if not isinstance(value,str) or len(value)>12000:raise ValueError('A locked field requires a value or explicit clear')
                if not value and not choice.get('clear'):raise ValueError('Choose a locked value or explicitly clear the field')
                if name=='artwork' and value:
                    # Artwork can only be pinned from accepted source evidence;
                    # this does not introduce arbitrary remote fetch permissions.
                    values=[r[0] for table in ('source_matches','source_variants') for r in c.execute(f'SELECT thumbnail_url FROM {table} WHERE entry_id=? AND active=1',(entry_id,))]
                    if value not in values:raise ValueError('Choose artwork from an attached record')
                fields[name]={'mode':'locked','value':value}
        c.execute('INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES(?,?,?,?,?)',
                  (entry_id,entry_id,json.dumps(old['fields']),'rollback' if restore_id is not None else 'before edit',now_iso()))
        c.execute('INSERT OR REPLACE INTO display_metadata VALUES(?,?,?,?)',(entry_id,revision+1,json.dumps(fields),now_iso()))
    return read(database,entry_id)


def overlay(database,entry):
    result=dict(entry);eid=int(entry.get('canonical_id') or entry['id'])
    with database.catalog() as c:
        row=c.execute('SELECT revision,fields_json FROM display_metadata WHERE entry_id=?',(eid,)).fetchone()
        if not row:return result
        fields=json.loads(row['fields_json']);provenance={}
        for name,choice in fields.items():
            if name not in FIELDS:continue
            column=FIELDS[name]
            if choice['mode']=='locked':result[column]=choice['value'];provenance[name]={'mode':'locked'}
            else:
                source=record(c,eid,choice['source_id'],choice['source_manga_id'])
                # Omitted/empty remote values leave the current fallback intact.
                if source and source.get(column):result[column]=source[column]
                provenance[name]={**choice,'available':bool(source and source.get(column))}
        result['display_ownership']={'revision':row['revision'],'fields':provenance}
    return result


def reparent(c,target,duplicate):
    row=c.execute('SELECT * FROM display_metadata WHERE entry_id=?',(duplicate,)).fetchone()
    c.execute('UPDATE display_metadata_history SET entry_id=? WHERE entry_id=?',(target,duplicate))
    if not row:return
    c.execute('INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES(?,?,?,?,?)',
              (target,duplicate,row['fields_json'],'absorbed ownership; surviving fields win conflicts',now_iso()))
    current=c.execute('SELECT * FROM display_metadata WHERE entry_id=?',(target,)).fetchone()
    fields=json.loads(row['fields_json']);fields.update(json.loads(current['fields_json']) if current else {})
    c.execute('INSERT OR REPLACE INTO display_metadata VALUES(?,?,?,?)',
              (target,max(row['revision'],current['revision'] if current else 0)+1,json.dumps(fields),now_iso()))
    c.execute('DELETE FROM display_metadata WHERE entry_id=?',(duplicate,))


def overlay_many(database,entries):
    with database.catalog() as c:
        ids={r[0] for r in c.execute('SELECT entry_id FROM display_metadata WHERE entry_id IN(SELECT value FROM json_each(?))',
                                   (json.dumps([row['id'] for row in entries]),))}
    return [overlay(database,row) if row['id'] in ids else row for row in entries]


def artwork_source(database,entry_id):
    with database.catalog() as c:
        row=c.execute('SELECT fields_json FROM display_metadata WHERE entry_id=?',(entry_id,)).fetchone()
        choice=json.loads(row[0]).get('artwork') if row else None
        if not choice:return None
        for table in ('source_matches','source_variants'):
            records=c.execute(f'SELECT source_id,source_manga_id,thumbnail_url FROM {table} WHERE entry_id=? AND active=1',(entry_id,)).fetchall()
            for record in records:
                if choice['mode']=='source' and record['source_id']==choice['source_id'] and record['source_manga_id']==choice['source_manga_id'] or choice['mode']=='locked' and choice['value'] and record['thumbnail_url']==choice['value']:
                    return record['source_manga_id']
        return 0
