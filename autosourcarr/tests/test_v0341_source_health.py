"""Source health is observed evidence, independent of source preferences."""
import asyncio
import io
import json
import threading
from contextlib import asynccontextmanager
from types import SimpleNamespace

import httpx
from PIL import Image
import pytest
from unittest.mock import AsyncMock
from fastapi import FastAPI, HTTPException

from test_v085_database import database


@pytest.fixture
def store(database):
    from app.source_health import HealthStore
    with database.catalog() as conn:
        conn.execute("INSERT INTO source_config(source_id,name,lang,last_seen) VALUES('1','Example','en','2026-10-11')")
    return HealthStore(database)


def test_dates_are_unknown_until_observed_and_last_pass_survives_failure_and_reopen(store):
    rows = store.snapshot()['items']
    assert rows[0]['state'] == 'unchecked' and not rows[0]['checked_at']
    assert not rows[0]['last_repaired_at']
    store.finish('1', 'working', 'Samples passed', [{'stage': 'catalog', 'state': 'passed'}], 'manual', stamp=100)
    store.finish('1', 'inconclusive', 'Request timed out', [], 'automatic', stamp=200)
    from app.source_health import HealthStore
    row = HealthStore(store.db).snapshot()['items'][0]
    assert row['checked_at'] == 200 and row['last_success_at'] == 100
    assert row['state'] == 'inconclusive' and len(row['history']) == 2
    with store.db.catalog() as conn:
        config = dict(conn.execute("SELECT * FROM source_config WHERE source_id='1'").fetchone())
    assert config['enabled_language'] == config['enabled_search'] == config['enabled_chapters'] == 1


def test_health_lists_only_installed_variants_and_retains_removed_source_history(store):
    with store.db.catalog() as conn:
        conn.execute("INSERT INTO source_config(source_id,name,lang,last_seen,available) VALUES('2','Removed','en','2026-10-11',0)")
        conn.execute("INSERT INTO source_config(source_id,name,lang,last_seen,enabled_language) VALUES('3','Disabled language','fr','2026-10-11',0)")
    store.finish('2', 'working', 'Retained sample', [], 'manual', stamp=100)
    with store.db.catalog() as conn:
        before = dict(conn.execute("SELECT * FROM source_health WHERE source_id='2'").fetchone())
    rows = {row['source_id']: row for row in store.snapshot()['items']}
    assert set(rows) == {'1', '3'}, 'Health lists installed sources, including disabled languages'
    assert not rows['3']['eligible'], 'Display membership must not enable daily checks'
    with store.db.catalog() as conn:
        assert dict(conn.execute("SELECT * FROM source_health WHERE source_id='2'").fetchone()) == before
        conn.execute("UPDATE source_config SET available=1 WHERE source_id='2'")
    row = next(row for row in store.snapshot()['items'] if row['source_id'] == '2')
    assert row['checked_at'] == row['last_success_at'] == 100
    assert row['history'] == json.loads(before['history_json'])


def test_ready_candidate_is_not_a_fix_and_confirmed_install_invalidates_old_pass(store):
    store.finish('1', 'working', 'Passed', [], 'manual', stamp=100)
    job = {'id': 'a'*32, 'state': 'ready', 'updated_at': 200, 'expected_ids': ['1'], 'pkg_name': 'eu.kanade.tachiyomi.extension.en.example'}
    store.repairs([job], {})
    assert not store.snapshot()['items'][0]['last_repaired_at']
    job['state'] = 'installed'; store.repairs([job], {})
    row = store.snapshot()['items'][0]
    assert row['last_repaired_at'] == 200 and row['repair_job_id'] == 'a'*32
    assert row['needs_recheck']
    store.repairs([job], {})
    assert store.snapshot()['items'][0]['last_repaired_at'] == 200


def test_lost_worker_ack_uses_confirmed_parent_install_timestamp(store):
    operation = {'id': 'b'*32, 'state': 'installed', 'installed_at': 300,
                 'extension': {'pkgName': 'eu.kanade.tachiyomi.extension.en.example', 'source': {'nodes': [{'id': '1'}]}}}
    store.repairs([], operation)
    assert store.snapshot()['items'][0]['last_repaired_at'] == 300
    operation['state'] = 'installing'; operation['installed_at'] = 400
    store.repairs([], operation)
    assert store.snapshot()['items'][0]['last_repaired_at'] == 300


def png():
    output = io.BytesIO(); Image.new('RGB', (4, 4)).save(output, 'PNG'); return output.getvalue()


class Client:
    def __init__(self): self.calls = []
    async def fetch_source_manga(self, sid, kind, **kw):
        self.calls.append(kind)
        return {'mangas': [{'id': 10, 'sourceId': sid, 'title': 'Private sample title'}]}
    async def fetch_manga_and_chapters(self, mid):
        self.calls.append('metadata')
        return {'manga': {'id': mid, 'sourceId': '1', 'title': 'Private sample title', 'url': '/manga/10','description':'Sample description'},
                'chapters': [{'id': 20, 'name': 'Chapter 1', 'url': '/chapter/20'}]}
    async def fetch_chapter_pages(self, cid):
        self.calls.append('pages'); return ['/api/v1/manga/10/chapter/0/page/0']
    async def fetch_binary(self, route):
        self.calls.append(route); return png(), 'image/png'
    async def _run_source_work(self, fn): return await fn()


@pytest.mark.asyncio
async def test_actual_probe_checks_catalog_search_metadata_cover_pages_image_without_retaining_titles():
    from app.source_health import probe_source
    client = Client(); checks = []
    await probe_source(client, {'source_id': '1'}, checks, pace=0)
    assert len(client.calls) == 6
    assert {row['stage'] for row in checks} == {'catalog', 'search', 'metadata/chapters', 'cover', 'page list', 'page image'}
    assert all(row['state'] == 'passed' for row in checks)
    assert 'Private sample title' not in json.dumps(checks)


@pytest.mark.asyncio
async def test_empty_catalog_is_inconclusive_and_never_marked_working():
    from app.source_health import probe_source, Inconclusive
    client = Client()
    async def empty(*a, **kw): return {'mangas': []}
    client.fetch_source_manga = empty
    with pytest.raises(Inconclusive):
        await probe_source(client, {'source_id': '1'}, [], pace=0)


@pytest.mark.asyncio
async def test_latest_is_checked_only_when_source_declares_support():
    from app.source_health import probe_source
    client=Client(); checks=[]
    await probe_source(client,{'source_id':'1','supports_latest':True},checks,pace=0)
    assert 'LATEST' in client.calls and len(client.calls)==7
    assert any(check['stage']=='latest catalog' for check in checks)


@pytest.mark.asyncio
async def test_external_page_route_and_invalid_image_fail_without_external_fetch():
    from app.source_health import probe_source
    client = Client()
    async def bad(*a): return ['http://private.invalid/secret']
    client.fetch_chapter_pages = bad
    with pytest.raises(ValueError): await probe_source(client, {'source_id': '1'}, [], pace=0)
    assert not any('private.invalid' in call for call in client.calls)
    client = Client()
    async def html(*a): return b'<html>login required</html>', 'text/html'
    client.fetch_binary = html
    with pytest.raises(ValueError): await probe_source(client, {'source_id': '1'}, [], pace=0)


def test_local_deferral_timeout_verification_and_rate_limit_are_distinct():
    from app.source_health import classify
    from app.suwayomi import SourceWorkDeferred, HumanAssistanceRequired
    assert classify(SourceWorkDeferred('capacity')) == 'deferred'
    assert classify(TimeoutError('timeout')) == 'inconclusive'
    assert classify(HumanAssistanceRequired({'id':'a','name':'Example','reason':'CAPTCHA'}, 60)) == 'verification'
    response = httpx.Response(429, request=httpx.Request('GET', 'http://test'))
    assert classify(httpx.HTTPStatusError('rate limit', request=response.request, response=response)) == 'rate_limited'


def test_valid_image_exceeding_local_pixel_budget_is_inconclusive(monkeypatch):
    import app.source_health as module
    def oversized(*args,**kwargs): raise ValueError('Image exceeds the analysis limit for decoded pixels')
    monkeypatch.setattr(module,'open_raster',oversized)
    with pytest.raises(module.Inconclusive): module.verify_image(b'bounded fixture')
    assert module.classify(RuntimeError('Asset/sample exceeds 32 MiB buffered-analysis limit'))=='inconclusive'


class Owner:
    _background_stopping=False
    def __init__(self):
        self.idle=asyncio.Event(); self.idle.set()
    def _track_auxiliary(self, coroutine): return asyncio.create_task(coroutine)
    async def _wait_for_background_idle(self): await self.idle.wait()
    @asynccontextmanager
    async def _manual_task_slot(self, **kwargs): yield
    @asynccontextmanager
    async def _background_task_slot(self, **kwargs): yield


@pytest.fixture
def manager(store,monkeypatch):
    import app.source_health as module
    from app.task_registry import TaskRegistry
    from app import source_maintenance
    monkeypatch.setattr(module,'task_registry',TaskRegistry())
    monkeypatch.setattr(source_maintenance,'inventory',AsyncMock(return_value=[{'isInstalled':True,'pkgName':'eu.kanade.tachiyomi.extension.en.example','versionName':'1.4.1','source':{'nodes':[{'id':'1'}]}}]))
    probe=AsyncMock()
    monkeypatch.setattr(module,'probe_source',probe)
    return module.SourceHealth(store,Client(),Owner()),probe,module.task_registry


@pytest.mark.asyncio
async def test_manual_checks_progress_while_daily_check_waits_for_idle(manager):
    health,probe,registry=manager; rows=health.store.snapshot()['items']
    health.owner().idle.clear()
    health.queue(rows,'automatic'); await asyncio.sleep(.03)
    result=health.queue(rows,'manual')
    await asyncio.wait_for(health.jobs['manual'][1],2)
    probe.assert_awaited_once()
    assert health.store.snapshot()['items'][0]['state']=='working'
    health.owner().idle.set(); await asyncio.wait_for(health.jobs['automatic'][1],2)
    assert probe.await_count==1,'Daily check must not repeat the newly completed manual sample'
    assert registry.snapshot()['running']==0


@pytest.mark.asyncio
async def test_daily_queue_rechecks_language_selection_after_waiting_for_idle(manager):
    health,probe,_=manager
    health.owner().idle.clear(); health.queue(health.store.snapshot()['items'],'automatic')
    await asyncio.sleep(.03)
    with health.store.db.catalog() as conn: conn.execute("UPDATE source_config SET enabled_language=0 WHERE source_id='1'")
    health.owner().idle.set(); await health.jobs['automatic'][1]
    probe.assert_not_awaited()
    assert health.store.snapshot()['items'][0]['state']=='unchecked'


@pytest.mark.asyncio
async def test_shutdown_cancels_and_drains_probe_then_retains_cancelled_evidence(manager):
    health,probe,registry=manager; entered=asyncio.Event(); drained=asyncio.Event()
    async def pending(*args,**kwargs):
        entered.set()
        try: await asyncio.Future()
        finally: drained.set()
    probe.side_effect=pending
    health.queue(health.store.snapshot()['items'],'manual')
    await asyncio.wait_for(entered.wait(),2)
    await health.stop()
    assert drained.is_set() and not health.checking
    assert health.store.snapshot()['items'][0]['state']=='cancelled'
    assert registry.snapshot()['running']==0
    assert all(not lock.locked() for lock in health.locks.values())


@pytest.mark.asyncio
async def test_shutdown_drains_an_already_owned_database_result_write(manager,monkeypatch):
    health,_,registry=manager; entered=threading.Event(); release=threading.Event()
    original=health.store.finish
    def slow(*args,**kwargs):
        entered.set(); assert release.wait(3); return original(*args,**kwargs)
    monkeypatch.setattr(health.store,'finish',slow)
    health.queue(health.store.snapshot()['items'],'manual')
    try:
        for _ in range(200):
            if entered.is_set(): break
            await asyncio.sleep(.01)
        assert entered.is_set()
        stopped=asyncio.create_task(health.stop()); await asyncio.sleep(.03)
        assert not stopped.done(),'Shutdown abandoned a database thread that already owns a result'
        release.set(); await asyncio.wait_for(stopped,2)
        assert health.store.snapshot()['items'][0]['state']=='working'
        assert registry.snapshot()['running']==0
    finally: release.set()


@pytest.mark.asyncio
async def test_local_deferral_remains_retryable_and_does_not_become_outage(manager):
    from app.suwayomi import SourceWorkDeferred
    health,probe,_=manager; probe.side_effect=SourceWorkDeferred('Local lane busy')
    health.queue(health.store.snapshot()['items'],'manual'); await health.jobs['manual'][1]
    row=health.store.snapshot()['items'][0]
    assert row['state']=='deferred' and not row['last_success_at']
    assert 0<row['next_check_at']-row['checked_at']<=300


def test_daily_reservation_survives_reopen_and_disabling_preserves_health(store):
    from app.source_health import HealthStore
    store.reserve('1'); reopened=HealthStore(store.db)
    assert reopened.snapshot()['items'][0]['next_check_at']>0
    store.configure(False)
    assert not reopened.daily()['enabled']
    assert reopened.snapshot()['items'][0]['state']=='unchecked'


def test_repair_during_check_requires_recheck_even_if_check_finished_later(store):
    store.repairs([{'id':'a'*32,'state':'installed','updated_at':150,'expected_ids':['1']}],{})
    store.finish('1','working','Passed',[],'manual',stamp=200,started_at=100)
    assert store.snapshot()['items'][0]['needs_recheck']


def test_schema_upgrade_preserves_existing_source_preferences_without_fabricating_dates(database):
    from app.db import Database
    with database.catalog() as conn:
        conn.execute('DROP TABLE source_health')
        conn.execute("INSERT INTO source_config(source_id,name,last_seen,enabled_search,enabled_chapters) VALUES('9','Kept','old',0,1)")
    reopened=Database()
    try:
        with reopened.catalog() as conn:
            assert tuple(conn.execute("SELECT enabled_search,enabled_chapters FROM source_config WHERE source_id='9'").fetchone())==(0,1)
            assert conn.execute('SELECT count(*) FROM source_health').fetchone()[0]==0
    finally: reopened.close_current_thread()


@pytest.mark.asyncio
async def test_health_routes_keep_admin_auth_and_validate_admission_without_live_requests(manager,monkeypatch):
    import app.source_health as module
    health,probe,_=manager; monkeypatch.setattr(module,'source_health',health)
    async def guard(): return None
    app=FastAPI(); app.include_router(module.health_router(guard,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        assert (await client.get('/api/admin/source-health')).status_code==200
        assert (await client.put('/api/admin/source-health/daily',json={'enabled':'false'})).status_code==400
        assert (await client.post('/api/admin/source-health/check',json={'source_ids':['missing']})).status_code==400
        assert (await client.post('/api/admin/source-health/check',json={'source_ids':['999']})).status_code==404
        assert (await client.post('/api/admin/source-health/check',content=b'x'*17000)).status_code==413
        assert (await client.post('/api/admin/source-health/check',json={'all_enabled':True})).status_code==202
        await health.jobs['manual'][1]
    probe.assert_awaited_once()
    async def deny(): raise HTTPException(401,'Authentication required')
    denied=FastAPI(); denied.include_router(module.health_router(deny,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=denied),base_url='http://test') as client:
        assert (await client.get('/api/admin/source-health')).status_code==401
        assert (await client.post('/api/admin/source-health/check',json={'all_enabled':True})).status_code==401
