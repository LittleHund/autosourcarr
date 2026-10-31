"""Capacity, event-loop and restart regressions using disposable real storage."""
import asyncio
import threading
from unittest.mock import AsyncMock

import httpx
import pytest

from app import service as module, translation
from app.config import settings
from app.suwayomi import SuwayomiClient, bounded_map, SourceWorkDeferred, BatchDeadlineExceeded, background_source_work
from app.task_registry import TaskRegistry
from test_v087_recovery import env


@pytest.mark.asyncio
@pytest.mark.parametrize('background', [False, True])
async def test_mapper_source_admission_never_penalizes_an_uncontacted_source(env, monkeypatch, background):
    service, _ = env
    client = SuwayomiClient('http://source.invalid', work_limit=2)
    monkeypatch.setattr(settings, 'source_failure_threshold', 1)
    semaphore = client._work_semaphores()[1 if background else 0]
    acquired = []
    for _ in range(1 if background else 2):
        await semaphore.acquire()
        acquired.append(semaphore)
    work = AsyncMock(return_value='unused')
    entered = asyncio.Event()
    async def source_request(_):
        entered.set()
        if background:
            async with background_source_work():
                return await client._run_source_work(work)
        return await client._run_source_work(work)
    job = asyncio.create_task(bounded_map(['healthy'], source_request, limit=1,
        per_item_timeout=.01, overall_timeout=.04))
    await entered.wait()
    try:
        rows = await job
        error = rows[0][2]
        assert isinstance(error, (SourceWorkDeferred, BatchDeadlineExceeded))
        work.assert_not_called()
        service._record_source_result('healthy', error)
        assert not service._source_in_cooldown('healthy')
        assert 'healthy' not in service._source_failures
    finally:
        for owned in acquired: owned.release()
        await client.close()


@pytest.mark.asyncio
async def test_mapper_execution_clock_begins_after_source_admission(env, monkeypatch):
    service, _ = env
    client = SuwayomiClient('http://source.invalid', work_limit=1)
    total, _ = client._work_semaphores()
    await total.acquire()
    called = []
    async def work():
        called.append(True)
        return 'done'
    job = asyncio.create_task(bounded_map(['healthy'], lambda _:client._run_source_work(work),
        limit=1, per_item_timeout=.01, overall_timeout=.2))
    await asyncio.sleep(.03)
    assert not job.done(), 'Execution timeout charged the admission wait'
    total.release()
    try:
        assert (await job)[0] == ('healthy','done',None)
        assert called == [True]
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_mapper_actual_source_execution_timeout_still_counts(env, monkeypatch):
    service, _ = env
    monkeypatch.setattr(settings,'source_failure_threshold',1)
    client = SuwayomiClient('http://source.invalid',work_limit=1)
    entered = asyncio.Event()
    async def work():
        entered.set()
        await asyncio.Event().wait()
    try:
        rows = await bounded_map(['healthy'], lambda _:client._run_source_work(work),
            limit=1, per_item_timeout=.01, overall_timeout=.2)
        error = rows[0][2]
        assert entered.is_set() and isinstance(error,TimeoutError)
        assert not getattr(error,'local_source_deferral',False)
        service._record_source_result('healthy',error)
        assert service._source_in_cooldown('healthy')
        assert not client._work_semaphores()[0].locked()
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_translation_real_writer_contention_does_not_stop_independent_loop_work(env, monkeypatch):
    _, database = env
    monkeypatch.setattr(translation,'db',database)
    monkeypatch.setattr(settings,'translation_mode','external')
    monkeypatch.setattr(settings,'translation_api_url','http://translation.invalid')
    client = translation.TranslationClient()
    await client.client.aclose()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(
        lambda request:httpx.Response(200,json={'translatedText':'English result'})))
    held, release = threading.Event(), threading.Event()
    def hold_writer():
        with database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            held.set()
            assert release.wait(2)
        database.close_current_thread()
    writer = threading.Thread(target=hold_writer)
    writer.start()
    assert await asyncio.to_thread(held.wait,1)
    ticks = []
    async def watchdog():
        await asyncio.sleep(.10)
        ticks.append(True)
        release.set()
    watch = asyncio.create_task(watchdog())
    job = asyncio.create_task(client.translate('原文','en'))
    try:
        await asyncio.sleep(.02)
        assert not job.done()
        await asyncio.wait_for(asyncio.sleep(.005),.1)
        # The independent coroutine must run before the writer's two-second bound.
        assert await asyncio.wait_for(job,.5) == 'English result'
        assert ticks
        with database.catalog() as c:
            assert c.execute('SELECT translated_text FROM translation_cache').fetchone()[0]=='English result'
    finally:
        release.set()
        await watch
        await asyncio.to_thread(writer.join,2)
        await client.close()


@pytest.mark.asyncio
async def test_translation_cancel_drains_the_admitted_real_cache_write(env, monkeypatch):
    _, database = env
    monkeypatch.setattr(translation,'db',database)
    monkeypatch.setattr(settings,'translation_mode','external')
    monkeypatch.setattr(settings,'translation_api_url','http://translation.invalid')
    client = translation.TranslationClient()
    await client.client.aclose()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(
        lambda request:httpx.Response(200,json={'translatedText':'English result'})))
    entered, release = threading.Event(), threading.Event()
    persist = client._persist_translation
    def admitted(*args):
        entered.set()
        assert release.wait(2)
        persist(*args)
    monkeypatch.setattr(client,'_persist_translation',admitted)
    job = asyncio.create_task(client.translate('原文','en'))
    try:
        assert await asyncio.to_thread(entered.wait,1)
        job.cancel()
        await asyncio.sleep(.01)
        assert not job.done()
        release.set()
        with pytest.raises(asyncio.CancelledError):await job
        with database.catalog() as c:
            assert c.execute('SELECT translated_text FROM translation_cache').fetchone()[0]=='English result'
    finally:
        release.set()
        await asyncio.gather(job,return_exceptions=True)
        await client.close()


def test_translation_transport_uses_configured_pool_limits(monkeypatch):
    monkeypatch.setattr(settings,'translation_mode','external')
    monkeypatch.setattr(settings,'translation_api_url','http://translation.invalid')
    monkeypatch.setattr(settings,'translation_concurrency',3)
    client = translation.TranslationClient()
    try:
        assert client.client._transport._pool._max_connections==5
        assert client.client._transport._pool._max_keepalive_connections==3
    finally:
        asyncio.run(client.close())


@pytest.mark.asyncio
@pytest.mark.parametrize('started', [False, True])
async def test_system_cancelled_metadata_group_never_reports_success(env, monkeypatch, started):
    service, _ = env
    registry = module.task_registry
    gate = asyncio.Event()
    work = AsyncMock()
    monkeypatch.setattr(service,'_wait_for_background_idle',lambda:gate.wait())
    monkeypatch.setattr(service,'enrich_entry_metadata',work)
    tid=registry.create('Repair missing metadata',total=3,origin='manual')
    service.schedule_metadata_enrichment([1,2,3],task_id=tid,_prepared=([],[1,2,3]))
    if started:await asyncio.sleep(0)
    await service.cancel_background_tasks()
    row=next(item for item in registry.snapshot()['items'] if item['id']==tid)
    assert row['state']=='failed' and 'cancel' in row['message'].lower()
    assert row['current']==0
    work.assert_not_called()
    assert not service._metadata_tasks


@pytest.mark.asyncio
@pytest.mark.parametrize('failed', [False, True])
async def test_metadata_group_reports_actual_worker_outcomes(env, monkeypatch, failed):
    service, _ = env
    registry=module.task_registry
    work=AsyncMock(side_effect=RuntimeError('broken source') if failed else None)
    monkeypatch.setattr(service,'enrich_entry_metadata',work)
    tid=registry.create('Repair missing metadata',total=2,origin='manual')
    service.schedule_metadata_enrichment([1,2],task_id=tid,_prepared=([],[1,2]))
    await asyncio.gather(*list(service._metadata_tasks.values()),return_exceptions=True)
    await asyncio.sleep(0)
    row=next(item for item in registry.snapshot()['items'] if item['id']==tid)
    assert row['state']==('failed' if failed else 'completed')
    assert row['current']==2 and work.await_count==2
    assert ('broken source' in row['error']) == failed


@pytest.mark.asyncio
async def test_metadata_group_mixed_success_failure_and_cancel_is_not_complete(env, monkeypatch):
    service, _ = env
    registry = module.task_registry
    blocked = asyncio.Event()
    entered = asyncio.Event()
    async def enrich(eid):
        if eid == 1: return
        if eid == 2: raise RuntimeError('broken source')
        entered.set()
        await blocked.wait()
    monkeypatch.setattr(service,'enrich_entry_metadata',enrich)
    tid=registry.create('Repair missing metadata',total=3,origin='manual')
    service.schedule_metadata_enrichment([1,2,3],task_id=tid,_prepared=([],[1,2,3]))
    await entered.wait()
    await asyncio.sleep(0)
    await service.cancel_background_tasks()
    row=next(item for item in registry.snapshot()['items'] if item['id']==tid)
    assert row['state']=='failed' and 'cancel' in row['message'].lower()
    assert row['current']==2 and row['total']==3
    assert not service._metadata_tasks


def import_rows(database, specs):
    from app.db import now_iso
    with database.catalog() as c:
        c.execute("INSERT INTO import_runs(id,created_at) VALUES('run',?)",(now_iso(),))
        ids=[]
        for position,spec in enumerate(specs):
            cur=c.execute('''INSERT INTO import_items(import_id,position,title,state,entry_id,
                suggestion_state,suggestion_checked,candidate_ids_json,decision_revision,updated_at)
                VALUES('run',?,?,?,?,?,?,?,?,?)''',
                (position,'Imported work',spec.get('state','unresolved'),spec.get('entry_id'),
                 spec.get('suggestion_state','queued'),spec.get('suggestion_checked',0),
                 spec.get('candidate_ids_json','[]'),spec.get('decision_revision',4),now_iso()))
            ids.append(cur.lastrowid)
    return ids


@pytest.mark.asyncio
async def test_normal_restart_replays_interrupted_actions_with_current_pipeline_marker(env, monkeypatch):
    from test_v087_recovery import entry
    service,database=env
    eid=entry(service,'anchor',0,attach=False)
    ids=import_rows(database,[{'state':'resolving','entry_id':eid},{'state':'retrying'},
        {'suggestion_state':'done','suggestion_checked':1,'candidate_ids_json':'[55]'},
        {'suggestion_state':'paused'},{'state':'skipped'}])
    database.set_setting('import_candidate_pipeline_version','0.2.84')
    seen=[]
    async def resolve(run,item,*args,task_id=None,decision_revision=None):
        seen.append((item,decision_revision))
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(item,decision_revision))
        return {'state':'added','reason':'Added'}
    monkeypatch.setattr(service,'match_import_item',resolve)
    monkeypatch.setattr(service,'retry_import_item',resolve)
    discovery=[]
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda run,**kw:discovery.append((run,kw)))
    before=[service._read_import_item('run',item) for item in ids[2:]]
    assert service.schedule_import_candidate_pipeline_upgrade(delay=0) is None
    assert service.schedule_import_discovery_resume(delay=0)
    assert not service.schedule_import_discovery_resume(delay=0)
    await service._refresh_tasks['__import-discovery-resume__']
    await asyncio.gather(*list(service._refresh_tasks.values()))
    assert sorted(seen)==[(ids[0],5),(ids[1],5)]
    assert [service._read_import_item('run',item)['state'] for item in ids[:2]]==['added','added']
    assert [service._read_import_item('run',item) for item in ids[2:]]==before
    assert discovery==[('run',{'resume_only':True})]


@pytest.mark.asyncio
async def test_restart_snapshot_does_not_overwrite_a_newer_skip_before_dispatch(env, monkeypatch):
    service,database=env
    (item,)=import_rows(database,[{'state':'retrying'}])
    run_blocking=module.run_blocking
    async def inject_skip(function,*args,**kwargs):
        result=await run_blocking(function,*args,**kwargs)
        if function.__name__=='recover_pending':service.skip_import_item('run',item)
        return result
    monkeypatch.setattr(module,'run_blocking',inject_skip)
    work=AsyncMock()
    monkeypatch.setattr(service,'retry_import_item',work)
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda *a,**kw:None)
    assert service.schedule_import_discovery_resume(delay=0)
    await service._refresh_tasks['__import-discovery-resume__']
    row=service._read_import_item('run',item)
    assert row['state']=='skipped' and row['decision_revision']==5
    work.assert_not_called()
    assert service._import_resolve_pending==0


@pytest.mark.asyncio
async def test_restart_worker_does_not_reset_a_runtime_started_during_admission(env, monkeypatch):
    service,database=env
    (item,)=import_rows(database,[{'state':'unresolved'}])
    admitted,release=threading.Event(),threading.Event()
    work_gate=asyncio.Event()
    started=asyncio.Event()
    run_blocking=module.run_blocking
    async def block_recovery(function,*args,**kwargs):
        if function.__name__=='recover_pending':
            def delayed():
                admitted.set()
                assert release.wait(2)
                return function(*args,**kwargs)
            return await run_blocking(delayed)
        return await run_blocking(function,*args,**kwargs)
    monkeypatch.setattr(module,'run_blocking',block_recovery)
    async def retry(run,current,*args,task_id=None,decision_revision=None):
        started.set()
        await work_gate.wait()
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(current,decision_revision))
        return {'state':'added'}
    monkeypatch.setattr(service,'retry_import_item',retry)
    discovery=[]
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda *a,**kw:discovery.append(a))
    assert service.schedule_import_discovery_resume(delay=0)
    recovery=service._refresh_tasks['__import-discovery-resume__']
    try:
        assert await asyncio.to_thread(admitted.wait,1)
        result=service.schedule_import_item_retry('run',item)
        assert result['queued']
        await started.wait()
        release.set()
        await recovery
        row=service._read_import_item('run',item)
        assert row['state']=='retrying' and row['decision_revision']==5
        assert not discovery
    finally:
        release.set();work_gate.set()
        await asyncio.gather(*list(service._refresh_tasks.values()),return_exceptions=True)


@pytest.mark.asyncio
@pytest.mark.parametrize('later_action',['retry','match'])
async def test_restart_missing_match_target_does_not_stop_later_actions_or_discovery(env,monkeypatch,later_action):
    from test_v087_recovery import entry
    service,database=env
    later={'state':'retrying'}
    if later_action=='match':later={'state':'resolving','entry_id':entry(service,'anchor',0,attach=False)}
    ids=import_rows(database,[{'state':'resolving','entry_id':999999},later,{'state':'unresolved'}])
    seen=[]
    async def finish(run,item,*args,task_id=None,decision_revision=None):
        seen.append(item)
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(item,decision_revision))
        return {'state':'added','reason':'done'}
    monkeypatch.setattr(service,'retry_import_item',finish)
    monkeypatch.setattr(service,'match_import_item',finish)
    discovery=[]
    def prepare(run,**kw):
        rows=service._prepare_import_discovery(run,**kw)
        discovery.append((run,[row['id'] for row in rows]))
        return 'discovery-queued'
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',prepare)
    assert service.schedule_import_discovery_resume(delay=0)
    await service._refresh_tasks['__import-discovery-resume__']
    await asyncio.gather(*list(service._refresh_tasks.values()))
    missing=service._read_import_item('run',ids[0])
    assert missing['state']=='unresolved' and missing['decision_revision']==4
    assert missing['entry_id']==999999 and missing['suggestion_state']=='paused'
    assert '999999' in missing['reason'] and 'new Match or Retry' in missing['reason']
    assert seen==[ids[1]]
    assert service._read_import_item('run',ids[1])['state']=='added'
    assert discovery==[('run',[ids[2]])]
    aggregate=next(row for row in module.task_registry.snapshot()['items'] if row['name']=='Resume interrupted import work')
    assert aggregate['state']=='failed' and aggregate['current']==aggregate['total']==3
    assert 'Recovered 1' in aggregate['message'] and 'resumed discovery for 1' in aggregate['message']
    assert '1 interrupted actions need Retry or a new Match' in aggregate['message']


@pytest.mark.asyncio
async def test_restart_missing_target_failure_annotation_preserves_a_newer_skip(env,monkeypatch):
    service,database=env
    ids=import_rows(database,[{'state':'resolving','entry_id':999999},{'state':'retrying'},{'state':'unresolved'}])
    title=service.entry_preferred_title
    saved=[]
    def skip_before_validation(eid):
        if eid==999999:
            service.skip_import_item('run',ids[0])
            saved.append(service._read_import_item('run',ids[0]))
        return title(eid)
    monkeypatch.setattr(service,'entry_preferred_title',skip_before_validation)
    seen=[]
    async def retry(run,item,*args,task_id=None,decision_revision=None):
        seen.append(item)
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(item,decision_revision))
        return {'state':'added'}
    monkeypatch.setattr(service,'retry_import_item',retry)
    discovery=[]
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda run,**kw:discovery.append(run))
    assert service.schedule_import_discovery_resume(delay=0)
    await service._refresh_tasks['__import-discovery-resume__']
    await asyncio.gather(*list(service._refresh_tasks.values()))
    row=service._read_import_item('run',ids[0])
    assert row==saved[0] and row['state']=='skipped' and row['decision_revision']==5
    assert seen==[ids[1]] and discovery==['run']


@pytest.mark.asyncio
async def test_restart_generic_dispatch_failure_keeps_later_retry_and_discovery_running(env,monkeypatch):
    service,database=env
    ids=import_rows(database,[{'state':'retrying'},{'state':'retrying'},{'state':'unresolved'}])
    schedule=service.schedule_import_item_retry
    def fail_first(run,item,**kw):
        if item==ids[0]:raise RuntimeError('queue unavailable')
        return schedule(run,item,**kw)
    monkeypatch.setattr(service,'schedule_import_item_retry',fail_first)
    seen=[]
    async def retry(run,item,*args,task_id=None,decision_revision=None):
        seen.append(item)
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(item,decision_revision))
        return {'state':'added'}
    monkeypatch.setattr(service,'retry_import_item',retry)
    discovery=[]
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda run,**kw:discovery.append(run))
    assert service.schedule_import_discovery_resume(delay=0)
    await service._refresh_tasks['__import-discovery-resume__']
    await asyncio.gather(*list(service._refresh_tasks.values()))
    failed=service._read_import_item('run',ids[0])
    assert failed['state']=='unresolved' and failed['decision_revision']==4 and failed['suggestion_state']=='paused'
    assert 'Interrupted Retry could not resume' in failed['reason']
    assert seen==[ids[1]] and discovery==['run']


@pytest.mark.asyncio
async def test_restart_failure_worker_does_not_pause_a_new_runtime_started_during_admission(env,monkeypatch):
    service,database=env
    ids=import_rows(database,[{'state':'resolving','entry_id':999999},{'state':'unresolved'}])
    admitted,release=threading.Event(),threading.Event()
    work_gate=asyncio.Event()
    started=asyncio.Event()
    run_blocking=module.run_blocking
    async def delay_failure_note(function,*args,**kwargs):
        if function.__name__=='record_dispatch_failure':
            def delayed():
                admitted.set()
                assert release.wait(2)
                return function(*args,**kwargs)
            return await run_blocking(delayed)
        return await run_blocking(function,*args,**kwargs)
    monkeypatch.setattr(module,'run_blocking',delay_failure_note)
    async def retry(run,item,*args,task_id=None,decision_revision=None):
        started.set()
        await work_gate.wait()
        with database.catalog() as c:
            c.execute("UPDATE import_items SET state='added' WHERE id=? AND decision_revision=?",(item,decision_revision))
        return {'state':'added'}
    monkeypatch.setattr(service,'retry_import_item',retry)
    discovery=[]
    monkeypatch.setattr(service,'schedule_import_translation_enrichment',lambda run,**kw:discovery.append(run))
    assert service.schedule_import_discovery_resume(delay=0)
    recovery=service._refresh_tasks['__import-discovery-resume__']
    try:
        assert await asyncio.to_thread(admitted.wait,1)
        result=service.schedule_import_item_retry('run',ids[0])
        assert result['queued']
        await started.wait()
        before=service._read_import_item('run',ids[0])
        release.set()
        await recovery
        after=service._read_import_item('run',ids[0])
        assert after==before and after['state']=='retrying' and after['decision_revision']==5
        assert after['suggestion_state']=='queued' and discovery==['run']
    finally:
        release.set();work_gate.set()
        await asyncio.gather(*list(service._refresh_tasks.values()),return_exceptions=True)
