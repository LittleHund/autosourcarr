import pytest
import asyncio
import sqlite3
from contextlib import asynccontextmanager,closing
import httpx
from fastapi import FastAPI,Header,HTTPException
from test_v087_recovery import env,source,entry
from app import identity_review as review,portable_identity as portable
from app.config import settings
from app.db import Database
from app.instance_backup import InstanceBackups
from app.library_manager import LibraryManager
from app.reimagined_api import router


def fixture(env):
    service,database=env;source(database,'17');eid=entry(service,'17',123,'Review fixture')
    with database.catalog() as c:c.execute('INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(?,?,?,?,?)',(eid,'anilist','12','work','now'))
    return service,database,eid


def test_confirm_exclude_reset_affects_only_portable_associations(env):
    service,database,eid=fixture(env)
    with database.catalog() as c:before=[tuple(r) for r in c.execute('SELECT * FROM identity_links')]
    state=review.read(database,service,eid);ref='asc1:work:anilist:12'
    saved=review.save(database,service,eid,ref,'confirmed',state['revision'],'Checked original publication')
    assert next(r for r in saved['references'] if r['id']==ref)['decision']=='confirmed'
    excluded=review.save(database,service,eid,ref,'excluded',saved['revision'],'Wrong catalogue association')
    assert ref not in {r['id'] for r in review.decorate(database,service.get_entry(eid))['portable_identity']['references']}
    assert portable.resolve(database,service,ref)['items']==[]
    review.save(database,service,eid,ref,'reset',excluded['revision'],'Restore inherited association')
    assert portable.resolve(database,service,ref)['items'][0]['id']==eid
    with database.catalog() as c:assert [tuple(r) for r in c.execute('SELECT * FROM identity_links')]==before


def test_revision_and_evidence_changes_reject_stale_confirmation(env):
    service,database,eid=fixture(env);state=review.read(database,service,eid)
    with database.catalog() as c:c.execute('UPDATE identity_links SET external_id=? WHERE entry_id=?',('13',eid))
    with pytest.raises(ValueError):review.save(database,service,eid,'asc1:work:anilist:12','confirmed',state['revision'],'Outdated proof')


def test_manual_cross_reference_requires_review_before_export(env):
    service,database,eid=fixture(env);state=review.read(database,service,eid);ref='asc1:work:kitsu:456'
    added=review.save(database,service,eid,ref,'needs-review',state['revision'],'Reference supplied for this work')
    assert ref not in {r['id'] for r in review.decorate(database,service.get_entry(eid))['portable_identity']['references']}
    confirmed=review.save(database,service,eid,ref,'confirmed',added['revision'],'Verified publication and edition scope')
    assert portable.resolve(database,service,ref)['items'][0]['id']==eid
    with pytest.raises(ValueError):review.save(database,service,eid,'title:fixture','confirmed',confirmed['revision'],'No title hashes')


def test_shared_work_id_is_review_information_not_a_merge(env):
    service,database,eid=fixture(env);source(database,'18');other=entry(service,'18',999,'Different edition')
    with database.catalog() as c:c.execute('INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(?,?,?,?,?)',(other,'anilist','12','work','now'))
    state=review.read(database,service,eid);ref=next(r for r in state['references'] if r['id']=='asc1:work:anilist:12')
    assert ref['shared_with'][0]['id']==other
    assert service.get_entry(other)['id']==other and service.get_entry(eid)['id']==eid


def test_merge_retains_conflicting_decisions_and_restore_requires_fresh_review(env):
    service,database,eid=fixture(env);source(database,'18');other=entry(service,'18',999,'Review fixture')
    ref='asc1:work:anilist:12'
    state=review.read(database,service,eid)
    review.save(database,service,eid,ref,'excluded',state['revision'],'Surviving reviewed edition')
    state=review.read(database,service,other)
    state=review.save(database,service,other,ref,'needs-review',state['revision'],'Absorbed publication evidence')
    review.save(database,service,other,ref,'confirmed',state['revision'],'Confirmed in separate entry')
    target=service.review_duplicate(eid,other,'duplicate')['canonical_entry_id']
    assert target==eid
    state=review.read(database,service,target)
    assert next(r for r in state['references'] if r['id']==ref)['decision']=='excluded'
    retained=next(r for r in state['history'] if r['old_entry_id']==other and r['decision']=='confirmed')
    restored=review.save(database,service,target,ref,'restore',state['revision'],restore_id=retained['id'])
    assert next(r for r in restored['references'] if r['id']==ref)['decision']=='needs-review'
    assert not any(r['association']=='confirmed on this instance' for r in review.decorate(database,service.get_entry(target))['portable_identity']['references'])


def test_additive_schema_and_reopen_preserve_original_values(env):
    service,database,eid=fixture(env)
    with database.catalog() as c:
        before={t:[tuple(r) for r in c.execute('SELECT * FROM '+t)] for t in ('entries','source_matches','identity_links','titles')}
        c.execute('DROP TABLE portable_identity_reviews');c.execute('DROP TABLE portable_identity_review_history')
        c.executescript(review.SCHEMA)
        assert {t:[tuple(r) for r in c.execute('SELECT * FROM '+t)] for t in before}==before
    state=review.read(database,service,eid)
    review.save(database,service,eid,'asc1:work:anilist:12','confirmed',state['revision'],'Retain across reopen')
    reopened=Database()
    try:
        assert review.read(reopened,service,eid)['references']==review.read(database,service,eid)['references']
        with reopened.catalog() as c:
            assert {t:[tuple(r) for r in c.execute('SELECT * FROM '+t)] for t in before}==before
            assert not c.execute('PRAGMA foreign_key_check').fetchall()
    finally:reopened.close_current_thread()


def test_backup_roundtrip_retains_identity_reviews_and_both_databases(env,tmp_path):
    service,database,eid=fixture(env);state=review.read(database,service,eid)
    review.save(database,service,eid,'asc1:work:anilist:12','excluded',state['revision'],'Private local review note')
    roots={name:tmp_path/'roundtrip'/name for name in ('config','data','db')}
    for root in roots.values():root.mkdir(parents=True)
    (roots['config']/'autosourcarr.env').write_text('FIXTURE=retained')
    for original in (settings.catalog_path,settings.origins_path):
        with closing(sqlite3.connect(original)) as src,closing(sqlite3.connect(roots['db']/original.name)) as dest:src.backup(dest)
    def values():
        result=[]
        for name in ('catalog.sqlite','chapter-origins.sqlite'):
            with closing(sqlite3.connect(roots['db']/name)) as c:result.append(list(c.iterdump()))
        return result
    before=values();backups=InstanceBackups(roots,'0.4.1');saved=backups.create('retained fixture passphrase')
    staged=backups.stage(backups.export(saved['id']).read_bytes(),'retained fixture passphrase')
    with closing(sqlite3.connect(roots['db']/'catalog.sqlite')) as c:c.execute('DELETE FROM portable_identity_reviews');c.commit()
    backups.arm(staged['id'],staged['revision'],'RESTORE THIS INSTANCE');backups.apply_pending()
    assert values()==before


def test_catalog_rebuild_retains_dormant_reviews_full_reset_clears(env):
    service,database,eid=fixture(env);state=review.read(database,service,eid)
    review.save(database,service,eid,'asc1:work:anilist:12','excluded',state['revision'],'Retained after catalog rebuild')
    service.reset_database()
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM portable_identity_reviews').fetchone()[0]==1
        assert c.execute('SELECT COUNT(*) FROM portable_identity_review_history').fetchone()[0]==1
    service.full_reset_database()
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM portable_identity_reviews').fetchone()[0]==0
        assert c.execute('SELECT COUNT(*) FROM portable_identity_review_history').fetchone()[0]==0


@pytest.mark.asyncio
async def test_authenticated_api_cas_and_view_only_search(env):
    service,database,eid=fixture(env);manager=LibraryManager(database,service,None)
    async def guard(x_admin_token:str=Header('')):
        if x_admin_token!='fixture-secret':raise HTTPException(401,'Admin token required')
    @asynccontextmanager
    async def paused():yield
    app=FastAPI();app.include_router(router(database,service,manager,settings,guard,asyncio.Lock(),paused,lambda:None))
    headers={'X-Admin-Token':'fixture-secret'}
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        base='/api/admin/identity-review';url=base+'/'+str(eid)
        assert (await client.get(url)).status_code==401
        assert (await client.post(url,json={})).status_code==401
        result=(await client.get(base+'?search=Review&limit=20',headers=headers)).json()
        assert result['total']==1 and result['items'][0]['id']==eid
        assert (await client.get(base+'?search=%25',headers=headers)).json()['total']==0
        state=(await client.get(url,headers=headers)).json()
        body={'reference':'asc1:work:anilist:12','decision':'excluded','revision':state['revision'],'note':'Reviewed this edition'}
        response=await client.post(url,json=body,headers=headers)
        assert response.status_code==200
        assert (await client.post(url,json=body,headers=headers)).status_code==409
        assert portable.resolve(database,service,body['reference'])['items']==[]
