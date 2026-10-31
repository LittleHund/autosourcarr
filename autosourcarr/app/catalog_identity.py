"""Portable lineage and imported evidence, separate from native reader/source IDs."""
import uuid
from . import catalog_exchange_format as wire

SCHEMA='''
CREATE TABLE IF NOT EXISTS catalogue_origin (id INTEGER PRIMARY KEY CHECK(id=1), uid TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS catalogue_entry_ids (
 uid TEXT NOT NULL, entry_id INTEGER NOT NULL, primary_id INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(uid,entry_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalogue_primary ON catalogue_entry_ids(entry_id) WHERE primary_id=1;
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalogue_primary_uid ON catalogue_entry_ids(uid) WHERE primary_id=1;
CREATE TABLE IF NOT EXISTS catalogue_uid_records (
 uid TEXT NOT NULL,source_id TEXT NOT NULL,reference TEXT NOT NULL,PRIMARY KEY(uid,source_id,reference)
);
CREATE TABLE IF NOT EXISTS catalogue_record_bindings (
 source_id TEXT NOT NULL,source_manga_id INTEGER NOT NULL,reference TEXT NOT NULL,entry_id INTEGER NOT NULL,
 PRIMARY KEY(source_id,source_manga_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_verified_reference ON catalogue_record_bindings(source_id,reference,entry_id);
CREATE TABLE IF NOT EXISTS catalogue_shared_entries (entry_id INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS catalogue_public_refs (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, PRIMARY KEY(entry_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_public_reference ON catalogue_public_refs(reference,entry_id);
CREATE TABLE IF NOT EXISTS catalogue_source_facts (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, source_id TEXT NOT NULL, fact_json TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference,source_id)
);
CREATE TABLE IF NOT EXISTS catalogue_import_records (
 uid TEXT NOT NULL, digest TEXT NOT NULL, record_json TEXT NOT NULL,
 state TEXT NOT NULL, message TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_record_resolutions (
 uid TEXT NOT NULL,digest TEXT NOT NULL,resolution_json TEXT NOT NULL,PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_decisions (
 uid TEXT NOT NULL, digest TEXT NOT NULL, decision_json TEXT NOT NULL,
 state TEXT NOT NULL DEFAULT 'pending', message TEXT NOT NULL DEFAULT '',
 applied_revision TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_exchange_stages (
 id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, revision TEXT NOT NULL,
 created_at REAL NOT NULL, result_json TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS catalogue_exchange_receipts (
 digest TEXT PRIMARY KEY, result_json TEXT NOT NULL, applied_at TEXT NOT NULL
);
'''
DATA_TABLES=['catalogue_exchange_stages','catalogue_exchange_receipts','catalogue_decisions','catalogue_import_records',
             'catalogue_source_facts','catalogue_public_refs','catalogue_shared_entries','catalogue_entry_ids','catalogue_uid_records','catalogue_record_bindings','catalogue_record_resolutions']


def origin(c):
    row=c.execute('SELECT uid FROM catalogue_origin WHERE id=1').fetchone()
    if row:return row[0]
    value=wire.uid('origin');c.execute('INSERT INTO catalogue_origin VALUES(1,?)',(value,));return value


def ensure(c,eid,preferred=None):
    row=c.execute('SELECT uid FROM catalogue_entry_ids WHERE entry_id=? AND primary_id=1',(eid,)).fetchone()
    if row:return row[0]
    value=wire.check_uid(preferred) if preferred else wire.uid()
    c.execute('UPDATE catalogue_entry_ids SET primary_id=0 WHERE uid=? AND primary_id=1 AND NOT EXISTS(SELECT 1 FROM entries e WHERE e.id=catalogue_entry_ids.entry_id)',(value,))
    c.execute('INSERT INTO catalogue_entry_ids VALUES(?,?,1) ON CONFLICT(uid,entry_id) DO UPDATE SET primary_id=1',(value,eid))
    return value


def link(c,eid,values):
    for value in set(values):c.execute('INSERT OR IGNORE INTO catalogue_entry_ids VALUES(?,?,0)',(wire.check_uid(value),eid))


def ids(c,eid):return [r[0] for r in c.execute('SELECT uid FROM catalogue_entry_ids WHERE entry_id=? ORDER BY primary_id DESC,uid',(eid,))]


def targets(c,values):
    result=set()
    for value in set(values):
        primary={int(r[0]) for r in c.execute('SELECT i.entry_id FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=? AND i.primary_id=1',(value,))}
        if primary:result.update(primary);continue
        covered=set()
        semantics=c.execute('SELECT source_id,reference FROM catalogue_uid_records WHERE uid=?',(value,)).fetchall()
        for row in semantics:
            sid,reference=row['source_id'],row['reference']
            owners={int(r[0]) for r in c.execute('''SELECT b.entry_id FROM catalogue_record_bindings b
              WHERE b.source_id=? AND b.reference=? AND
               (EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=b.entry_id AND m.source_id=b.source_id AND m.source_manga_id=b.source_manga_id AND m.active=1)
                OR EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=b.entry_id AND v.source_id=b.source_id AND v.source_manga_id=b.source_manga_id AND v.active=1))''',(sid,reference))}
            if not owners:owners={int(r[0]) for r in c.execute('SELECT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id WHERE f.source_id=? AND f.reference=?',(sid,reference))}
            covered.update(owners)
        result.update(covered if semantics else {int(r[0]) for r in c.execute('SELECT i.entry_id FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=?',(value,))})
    return sorted(result)


def record_binding(c,eid,sid,mid,reference):
    c.execute('DELETE FROM catalogue_record_bindings WHERE source_id=? AND source_manga_id=? AND reference<>?',(str(sid),int(mid),reference))
    c.execute('''INSERT INTO catalogue_record_bindings VALUES(?,?,?,?) ON CONFLICT(source_id,source_manga_id,reference)
                 DO UPDATE SET entry_id=excluded.entry_id WHERE entry_id<>excluded.entry_id''',(str(sid),int(mid),reference,eid))


def bind_native(c,eid,sid,manga):
    from .portable_identity import gallery_reference,record_id
    row={'source_manga_url':manga.get('url') or '', 'real_url':manga.get('realUrl') or ''}
    source=c.execute('SELECT base_url FROM source_config WHERE source_id=?',(str(sid),)).fetchone()
    if source:row['base_url']=source[0]
    try:reference=gallery_reference(row) or record_id(str(sid),row['source_manga_url']);wire.reference(reference)
    except ValueError:return
    record_binding(c,eid,sid,manga['id'],reference)
    primary=ensure(c,eid)
    c.execute('INSERT OR IGNORE INTO catalogue_uid_records VALUES(?,?,?)',(primary,str(sid),reference))


def lineage(c,values):
    return [{'uid':value,'records':[dict(row) for row in c.execute('SELECT source_id,reference FROM catalogue_uid_records WHERE uid=? ORDER BY source_id,reference',(value,))]} for value in values]


def refresh_groups(c,roots):
    """An old combined ID remains group lineage; each separate entry gets one ID."""
    for root in set(roots):
        primary=c.execute('SELECT entry_id FROM catalogue_entry_ids WHERE uid=? AND primary_id=1',(root,)).fetchone()
        if not primary:continue
        c.execute('UPDATE catalogue_entry_ids SET primary_id=0 WHERE uid=?',(root,))
        owners=targets(c,[root])
        if len(owners)<2:
            c.execute('UPDATE catalogue_entry_ids SET primary_id=1 WHERE uid=? AND entry_id=?',(root,primary['entry_id']));continue
        for eid in owners:
            keys=sorted({(r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_source_facts WHERE entry_id=?',(eid,))}|
                        {(r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_record_bindings WHERE entry_id=?',(eid,))})
            chosen='asc1:entry:autosourcarr:'+str(uuid.uuid5(uuid.UUID(root.rsplit(':',1)[-1]),wire.canonical(keys)))
            if not c.execute('SELECT 1 FROM catalogue_entry_ids WHERE entry_id=? AND primary_id=1',(eid,)).fetchone():
                occupied=c.execute('SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=? AND i.primary_id=1',(chosen,)).fetchone()
                ensure(c,eid,None if occupied else chosen)
            for sid,ref in keys:c.execute('INSERT OR IGNORE INTO catalogue_uid_records VALUES(?,?,?)',(chosen,sid,ref))


def references(database,eid):
    with database.catalog() as c:
        result=[{'id':r[0],'scope':r[0].split(':')[1],'provider':r[0].split(':')[2],'association':'shared catalogue; not native source proof'} for r in c.execute('SELECT reference FROM catalogue_public_refs WHERE entry_id=? ORDER BY reference',(eid,))]
        result +=[{'id':value,'scope':'entry','provider':'autosourcarr','association':'portable entry lineage'} for value in ids(c,eid) if targets(c,[value])==[eid]]
    return result


def lookup(database,service,reference):
    with database.catalog() as c:
        if reference.startswith('asc1:entry:autosourcarr:'):values=targets(c,[reference])
        else:values=[int(r[0]) for r in c.execute('SELECT r.entry_id FROM catalogue_public_refs r JOIN entries e ON e.id=r.entry_id WHERE reference=?',(reference,))]
    return {service.canonical_entry_id(eid) for eid in values}


def unverified(database,eid):
    with database.catalog() as c:
        return bool(c.execute('''SELECT 1 FROM catalogue_shared_entries s JOIN entries e ON e.id=s.entry_id WHERE s.entry_id=?
          AND NOT EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=s.entry_id AND m.active=1)
          AND NOT EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=s.entry_id AND v.active=1)''',(eid,)).fetchone())


def decision_uid(c,kind,key):
    namespace=uuid.UUID(origin(c).rsplit(':',1)[-1])
    return 'asc1:decision:autosourcarr:'+str(uuid.uuid5(namespace,kind+':'+str(key)))


def reparent(c,target,duplicate):
    primary=c.execute('SELECT uid FROM catalogue_entry_ids WHERE entry_id=? AND primary_id=1',(target,)).fetchone()
    rows=c.execute('SELECT uid,primary_id FROM catalogue_entry_ids WHERE entry_id=?',(duplicate,)).fetchall()
    c.execute('UPDATE catalogue_entry_ids SET primary_id=0 WHERE entry_id=?',(duplicate,))
    for row in rows:
        chosen=int(not primary and row['primary_id'])
        c.execute('INSERT INTO catalogue_entry_ids VALUES(?,?,?) ON CONFLICT(uid,entry_id) DO UPDATE SET primary_id=MAX(primary_id,excluded.primary_id)',(row['uid'],target,chosen))
        if chosen:primary=row
    c.execute('DELETE FROM catalogue_entry_ids WHERE entry_id=?',(duplicate,))
    c.execute('UPDATE catalogue_record_bindings SET entry_id=? WHERE entry_id=?',(target,duplicate))
    for table,columns in [('catalogue_public_refs','reference'),('catalogue_source_facts','reference,source_id,fact_json')]:
        c.execute(f'INSERT OR IGNORE INTO {table} SELECT ?,{columns} FROM {table} WHERE entry_id=?',(target,duplicate))
        c.execute(f'DELETE FROM {table} WHERE entry_id=?',(duplicate,))
    if c.execute('SELECT 1 FROM catalogue_shared_entries WHERE entry_id=?',(duplicate,)).fetchone():
        c.execute('INSERT OR IGNORE INTO catalogue_shared_entries VALUES(?)',(target,));c.execute('DELETE FROM catalogue_shared_entries WHERE entry_id=?',(duplicate,))
    primary=ensure(c,target)
    c.execute('INSERT OR IGNORE INTO catalogue_uid_records SELECT ?,source_id,reference FROM catalogue_record_bindings WHERE entry_id=?',(primary,target))


def split(c,original,new_id,source_pairs):
    """Move exact record aliases; retain a former group's UID as group lineage."""
    parent=ensure(c,original);selected=set(source_pairs);moved=[]
    for row in c.execute('SELECT * FROM catalogue_record_bindings WHERE entry_id=?',(original,)).fetchall():
        if (row['source_id'],row['source_manga_id']) in selected:
            c.execute('UPDATE catalogue_record_bindings SET entry_id=? WHERE source_id=? AND source_manga_id=? AND reference=?',(new_id,row['source_id'],row['source_manga_id'],row['reference']))
            moved.append((row['source_id'],row['reference']))
    for sid,ref in moved:
        c.execute('INSERT OR IGNORE INTO catalogue_source_facts SELECT ?,reference,source_id,fact_json FROM catalogue_source_facts WHERE entry_id=? AND source_id=? AND reference=?',(new_id,original,sid,ref))
        c.execute('DELETE FROM catalogue_source_facts WHERE entry_id=? AND source_id=? AND reference=?',(original,sid,ref))
        c.execute('INSERT OR IGNORE INTO catalogue_public_refs VALUES(?,?)',(new_id,ref))
        remains=c.execute('SELECT 1 FROM catalogue_record_bindings WHERE entry_id=? AND reference=? UNION ALL SELECT 1 FROM catalogue_source_facts WHERE entry_id=? AND reference=? LIMIT 1',(original,ref,original,ref)).fetchone()
        if not remains:c.execute('DELETE FROM catalogue_public_refs WHERE entry_id=? AND reference=?',(original,ref))
    for value in ids(c,original):
        semantics={(r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_uid_records WHERE uid=?',(value,))}
        if semantics and semantics<=set(moved):
            c.execute('UPDATE catalogue_entry_ids SET entry_id=?,primary_id=0 WHERE entry_id=? AND uid=?',(new_id,original,value))
        elif semantics&set(moved):link(c,new_id,[value])
    for eid in (original,new_id):
        members=sorted((r[0],r[1]) for r in c.execute('SELECT source_id,reference FROM catalogue_record_bindings WHERE entry_id=?',(eid,)))
        if members:
            child='asc1:entry:autosourcarr:'+str(uuid.uuid5(uuid.UUID(parent.rsplit(':',1)[-1]),wire.canonical(members)))
            c.execute('UPDATE catalogue_entry_ids SET primary_id=0 WHERE entry_id=?',(eid,))
            occupied=c.execute('SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=? AND i.primary_id=1 AND i.entry_id<>?',(child,eid)).fetchone()
            if occupied:ensure(c,eid);link(c,eid,[child])
            else:ensure(c,eid,child)
            for sid,ref in members:c.execute('INSERT OR IGNORE INTO catalogue_uid_records VALUES(?,?,?)',(child,sid,ref))
        else:ensure(c,eid)
