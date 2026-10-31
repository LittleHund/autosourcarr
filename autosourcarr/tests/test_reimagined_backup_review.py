import json
import sqlite3
from pathlib import Path

import pytest
from test_v087_recovery import env,source,entry
from app.instance_backup import InstanceBackups,repair_idle_for_snapshot
from app.review_workbench import preview,apply,undo


def test_encrypted_backup_preserves_wal_rows_keys_and_rehearses_restore(tmp_path):
    roots={name:tmp_path/name for name in ('config','data','engine','db')}
    for root in roots.values(): root.mkdir()
    (roots['config']/'.extension-signing').mkdir()
    (roots['config']/'.extension-signing/key.jks').write_bytes(b'private signing identity')
    (roots['config']/'autosourcarr.env').write_text('ADMIN_TOKEN=private-test')
    sources=[]
    for name,table in [('catalog.sqlite','entries'),('chapter-origins.sqlite','virtual_chapters')]:
        c=sqlite3.connect(roots['db']/name);c.execute('PRAGMA journal_mode=WAL')
        c.execute(f'CREATE TABLE {table}(id INTEGER PRIMARY KEY)');c.execute(f'INSERT INTO {table} VALUES(7)');c.commit();sources.append(c)
    backups=InstanceBackups(roots,'0.3.45')
    receipt=backups.create('a sufficiently long passphrase')
    encrypted=backups.export(receipt['id']).read_bytes()
    assert b'private-test' not in encrypted and b'private signing identity' not in encrypted
    with pytest.raises(ValueError,match='passphrase'):backups.stage(encrypted,'wrong passphrase')
    staged=backups.stage(encrypted,'a sufficiently long passphrase')
    assert staged['integrity']=='ok' and staged['databases']==2
    for c in sources:c.close()
    (roots['config']/'.extension-signing/key.jks').write_bytes(b'changed')
    backups.arm(staged['id'],staged['revision'],'RESTORE THIS INSTANCE')
    backups.apply_pending()
    assert (roots['config']/'.extension-signing/key.jks').read_bytes()==b'private signing identity'
    with sqlite3.connect(roots['db']/'catalog.sqlite') as c:assert c.execute('SELECT id FROM entries').fetchall()==[(7,)]
    assert not backups.pending_path.exists()


def test_restore_detects_changed_staging_before_overwriting_any_live_file(tmp_path):
    roots={name:tmp_path/name for name in ('config','data','db')}
    for root in roots.values():root.mkdir()
    for name,table in [('catalog.sqlite','entries'),('chapter-origins.sqlite','virtual_chapters')]:
        with sqlite3.connect(roots['db']/name) as c:c.execute(f'CREATE TABLE {table}(id)')
    (roots['config']/'autosourcarr.env').write_text('KEEP=this')
    backups=InstanceBackups(roots,'0.3.45');saved=backups.create('another long passphrase')
    staged=backups.stage(backups.export(saved['id']).read_bytes(),'another long passphrase')
    backups.arm(staged['id'],staged['revision'],'RESTORE THIS INSTANCE')
    (backups.root/'staged'/staged['id']/'config/autosourcarr.env').write_text('tampered')
    with pytest.raises(ValueError,match='hash'):backups.apply_pending()
    assert (roots['config']/'autosourcarr.env').read_text()=='KEEP=this'


def test_bulk_separate_preview_is_exact_and_undo_refuses_later_changes(env):
    service,database=env;source(database,'one')
    a=entry(service,'one',221,'Title A');b=entry(service,'one',222,'Title B')
    proposal=preview(database,service,[[a,b]])
    result=apply(database,service,[[a,b]],proposal['revision'])
    with database.catalog() as c:assert c.execute('SELECT decision FROM duplicate_reviews').fetchone()[0]=='not_duplicate'
    undo(database,service,result['id'])
    with database.catalog() as c:assert c.execute('SELECT COUNT(*) FROM duplicate_reviews').fetchone()[0]==0
    proposal=preview(database,service,[[a,b]]);result=apply(database,service,[[a,b]],proposal['revision'])
    service.review_duplicate(a,b,'clear')
    with pytest.raises(ValueError,match='changed'):undo(database,service,result['id'])


def test_bulk_review_stale_identity_fails_whole_batch(env):
    service,database=env;source(database,'one')
    a=entry(service,'one',223,'Title A');b=entry(service,'one',224,'Title B')
    proposal=preview(database,service,[[a,b]])
    service.review_duplicate(a,b,'not_duplicate')
    with pytest.raises(ValueError,match='changed'):apply(database,service,[[a,b]],proposal['revision'])


def test_backup_admission_requires_explicit_idle_repair_ownership():
    idle={'active':None,'pending_jobs':False,'account_busy':False,'cleanup_uncertain':False}
    assert repair_idle_for_snapshot(idle)
    assert not repair_idle_for_snapshot({})
    for key in ('pending_jobs','account_busy','cleanup_uncertain'):
        assert not repair_idle_for_snapshot({**idle,key:True})
        assert not repair_idle_for_snapshot({**idle,key:None})
    assert not repair_idle_for_snapshot({**idle,'active':'f'*32})
