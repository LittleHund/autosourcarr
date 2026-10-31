import json
import sqlite3
from contextlib import closing
import pytest
from test_v087_recovery import env,source,entry
from test_reimagined_arr_runtime import candidate
from app import metadata_fields,decision_preview,profile_upgrade,serving_profiles
from app.db import Database,now_iso
from app.reimagined_state import SCHEMA
from app.instance_backup import InstanceBackups


def dump(database):
    with database.catalog() as c:catalog=list(c.iterdump())
    with database.origins() as o:origins=list(o.iterdump())
    return catalog,origins


def test_display_ownership_omission_explicit_clear_stale_revision_and_rollback(env):
    service,database=env;source(database,'one');eid=entry(service,'one',1,'Original Display Work')
    evidence=service._entry_duplicate_evidence_snapshot(eid)
    saved=metadata_fields.save(database,service,eid,{'description':{'mode':'locked','value':'Display only synopsis'}},0)
    saved=metadata_fields.save(database,service,eid,{'author':{'mode':'locked','value':'Display author'}},saved['revision'])
    assert metadata_fields.overlay(database,service.get_entry(eid))['description']=='Display only synopsis'
    assert service._entry_duplicate_evidence_snapshot(eid)==evidence
    with pytest.raises(ValueError,match='changed'):metadata_fields.save(database,service,eid,{},0)
    with pytest.raises(ValueError,match='value'):metadata_fields.save(database,service,eid,{'description':{'mode':'locked'}},saved['revision'])
    cleared=metadata_fields.save(database,service,eid,{'description':{'mode':'locked','clear':True}},saved['revision'])
    assert metadata_fields.overlay(database,service.get_entry(eid))['description']==''
    restored=metadata_fields.save(database,service,eid,{},cleared['revision'],restore_id=cleared['history'][0]['id'])
    assert restored['fields']['description']['value']=='Display only synopsis'


def test_display_source_omission_artwork_allowlist_and_merge_conflicts(env):
    service,database=env;source(database,'one');source(database,'two')
    a=entry(service,'one',1,'The Display Merge Work');b=entry(service,'two',2,'The Display Merge Work')
    metadata_fields.save(database,service,a,{'author':{'mode':'locked','value':'Survivor'}},0)
    metadata_fields.save(database,service,b,{'author':{'mode':'locked','value':'Absorbed'},'artist':{'mode':'source','source_id':'two','source_manga_id':2}},0)
    with pytest.raises(ValueError,match='attached'):metadata_fields.save(database,service,a,{'artwork':{'mode':'locked','value':'http://private.invalid/arbitrary'}},1)
    canonical=service.review_duplicate(a,b,'duplicate')['canonical_entry_id']
    fields=metadata_fields.read(database,canonical)
    assert fields['fields']['author']['value'] in {'Survivor','Absorbed'}
    assert any(row['reason'].startswith('absorbed') for row in fields['history'])
    before=metadata_fields.overlay(database,service.get_entry(canonical))
    with database.catalog() as c:c.execute("UPDATE source_matches SET artist='' WHERE entry_id=?",(canonical,))
    after=metadata_fields.overlay(database,service.get_entry(canonical))
    assert after['artist']==service.get_entry(canonical)['artist']
    assert before['preferred_title']==after['preferred_title']


def test_real_policy_preview_is_read_only_and_apply_checks_current_evidence(env):
    service,database=env;source(database,'one');source(database,'two')
    a=entry(service,'one',1,'The Exhaustive Regression Work');b=entry(service,'two',2,'The Exhaustive Regression Work')
    draft={'scope':'source','subject':'one','direction':'external','rule':{'enabled':False,'checks':{}}}
    before=dump(database);result=decision_preview.preview(database,service,draft,[[a,b]])
    assert dump(database)==before
    assert result['pairs'][0]['before']==service._auto_merge_group_evidence(a,b,policy_values={},policy_sources={r['source_id']:r for r in service.list_sources()})
    assert not result['pairs'][0]['after']['eligible']
    with database.catalog() as c:c.execute("UPDATE source_matches SET description='Changed original source evidence' WHERE entry_id=?",(a,))
    with pytest.raises(ValueError,match='changed'):decision_preview.apply(database,service,draft,[[a,b]],result['revision'])
    fresh=decision_preview.preview(database,service,draft,[[a,b]])
    assert decision_preview.apply(database,service,draft,[[a,b]],fresh['revision'])['applied']
    with database.catalog() as c:assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0]==2
    service.review_duplicate(a,b,'not_duplicate')
    inherited={**draft,'rule':None}
    assert not decision_preview.preview(database,service,inherited,[[a,b]])['pairs'][0]['after']['eligible']


def test_upgrade_requires_optin_complete_fresh_samples_and_retains_origins(env):
    service,database=env;source(database,'one');source(database,'two');eid=entry(service,'one',1)
    with database.catalog() as c:c.execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(?,'two',2,'Monster List','now')",(eid,))
    old=candidate(database)
    with database.origins() as o:
        vid=o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE id=?',(old,)).fetchone()[0]
        target=o.execute("INSERT INTO chapter_candidates(virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,quality_score,first_seen,last_seen) VALUES(?,'two','Two',2,2,'One',95,'now','now')",(vid,)).lastrowid
        o.execute('UPDATE quality_samples SET analyzed_at=?',(now_iso(),))
        o.execute("INSERT INTO quality_samples VALUES(?,2,0,2000,2000,200000,'c','d',?)",(target,now_iso()))
        o.execute("INSERT INTO served_origin VALUES(?,?,?,'initial')",(vid,old,now_iso()))
    assert not profile_upgrade.preview(database,service,eid,vid,target)['eligible']
    profile_upgrade.save(database,service,eid,{**profile_upgrade.DEFAULT,'enabled':True},0)
    proposal=profile_upgrade.preview(database,service,eid,vid,target)
    assert proposal['eligible'],proposal['blockers']
    origins=dump(database)[1]
    profile_upgrade.apply(database,service,eid,vid,target,proposal['revision'])
    assert dump(database)[1]==origins
    assert service.chapter_details(vid)['would_serve_candidate_id']==target
    blocked=profile_upgrade.preview(database,service,eid,vid,old)
    assert not blocked['eligible'] and any('cooldown' in reason for reason in blocked['blockers'])
    with database.catalog() as c:c.execute("UPDATE source_matches SET locked=1 WHERE entry_id=?",(eid,))
    assert any('locked' in reason for reason in profile_upgrade.preview(database,service,eid,vid,target)['blockers'])


def test_profile_quality_uses_100_point_units(env):
    service,database=env;source(database,'one');eid=entry(service,'one',1)
    serving_profiles.save(database,service,eid,{'quality':'quality'},0)
    rows=[{'source_id':'one','quality_score':25,'resolver_score':0},{'source_id':'one','quality_score':75,'resolver_score':0}]
    serving_profiles.rank(database,eid,rows)
    assert [r['resolver_score'] for r in rows]==[5,15]


def test_populated_additive_migration_preserves_values_and_relationships(env,tmp_path):
    service,database=env;source(database,'one');eid=entry(service,'one',1,'Persistent Original Work');cid=candidate(database)
    service.set_preferred_title(eid,'Persistent Manual Title')
    with database.catalog() as c:
        c.execute("INSERT INTO import_runs(id,filename,state,created_at) VALUES('fixture','fixture.backup','completed','now')")
        c.execute("INSERT INTO import_items(import_id,title,entry_id,state,updated_at) VALUES('fixture','Imported',?,'added','now')",(eid,))
    old_tables=['entries','titles','source_matches','import_runs','import_items','preferred_title_overrides','manual_title_scopes']
    with database.catalog() as c:
        for name in ('quality_attempts','due_work','crawl_pending','library_complete_observations','display_metadata',
                     'display_metadata_history','serving_upgrade_policies','serving_choices','serving_upgrade_history','policy_preview_history'):
            c.execute('DROP TABLE '+name)
    with database.catalog() as c:before={name:[tuple(r) for r in c.execute('SELECT * FROM '+name)] for name in old_tables}
    origins=dump(database)[1]
    # Replay exactly the additive release DDL against populated old tables. A
    # foreign_key_check alone would not catch a valid but accidental cascade.
    with database.catalog() as c:
        c.executescript(SCHEMA)
        assert {name:[tuple(r) for r in c.execute('SELECT * FROM '+name)] for name in old_tables}==before
        assert not c.execute('PRAGMA foreign_key_check').fetchall()
    assert dump(database)[1]==origins
    reopened=Database()
    try:
        with reopened.catalog() as c:
            assert {name:[tuple(r) for r in c.execute('SELECT * FROM '+name)] for name in old_tables}==before
    finally:reopened.close_current_thread()
    with database.origins() as o:
        assert o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE id=?',(cid,)).fetchone()
        assert o.execute('SELECT candidate_id FROM quality_samples WHERE candidate_id=?',(cid,)).fetchone()


def test_populated_backup_roundtrip_keeps_both_database_relationships(env,tmp_path):
    service,database=env;source(database,'one');eid=entry(service,'one',1);candidate(database)
    metadata_fields.save(database,service,eid,{'author':{'mode':'locked','value':'Retained author'}},0)
    roots={name:tmp_path/'roundtrip'/name for name in ('config','data','db')}
    for root in roots.values():root.mkdir(parents=True)
    (roots['config']/'autosourcarr.env').write_text('FIXTURE=preserve')
    from app.config import settings
    for original in (settings.catalog_path,settings.origins_path):
        with closing(sqlite3.connect(original)) as src,closing(sqlite3.connect(roots['db']/original.name)) as dest:src.backup(dest)
    def values():
        result=[]
        for name in ('catalog.sqlite','chapter-origins.sqlite'):
            with closing(sqlite3.connect(roots['db']/name)) as c:result.append(list(c.iterdump()))
        return result
    before=values();backups=InstanceBackups(roots,'0.3.47');saved=backups.create('retained fixture passphrase')
    staged=backups.stage(backups.export(saved['id']).read_bytes(),'retained fixture passphrase')
    with closing(sqlite3.connect(roots['db']/'catalog.sqlite')) as c:
        c.execute('DELETE FROM display_metadata');c.commit()
    backups.arm(staged['id'],staged['revision'],'RESTORE THIS INSTANCE');backups.apply_pending()
    assert values()==before
    assert (roots['config']/'autosourcarr.env').read_text()=='FIXTURE=preserve'


def test_rebuild_retains_display_and_upgrade_policy_full_reset_clears(env):
    service,database=env;source(database,'one');eid=entry(service,'one',1)
    metadata_fields.save(database,service,eid,{'author':{'mode':'locked','value':'Kept'}},0)
    profile_upgrade.save(database,service,eid,{**profile_upgrade.DEFAULT,'enabled':True},0)
    service.reset_database()
    with database.catalog() as c:
        assert c.execute('SELECT fields_json FROM display_metadata').fetchone()
        assert c.execute('SELECT policy_json FROM serving_upgrade_policies').fetchone()
    service.full_reset_database()
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM display_metadata').fetchone()[0]==0
        assert c.execute('SELECT COUNT(*) FROM serving_upgrade_policies').fetchone()[0]==0
