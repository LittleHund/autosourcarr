import asyncio
import json
import threading
from types import SimpleNamespace
import pytest
from test_v087_recovery import env,source,entry
from app.due_scheduler import DueScheduler,CrawlPages
from app import metadata_catalog,quality
from app.task_journal import TaskJournal


def test_pending_page_tail_restart_failure_and_empty_page(env):
    _,database=env;store=CrawlPages(database)
    payload={'mangas':[{'id':n,'title':str(n)} for n in range(1,51)],'hasNextPage':True}
    store.save_page('latest|source',1,payload)
    for manga in store.ready('latest|source',30):store.acknowledge('latest|source',manga['id'],manga['id']!=5)
    assert store.finish('latest|source') is None
    store=CrawlPages(database)
    assert len(store.load('latest|source')['items'])==21
    assert [m['id'] for m in store.ready('latest|source',30)]==list(range(31,51))
    for manga in store.ready('latest|source',30):store.acknowledge('latest|source',manga['id'],True)
    with database.catalog() as c:
        value=json.loads(c.execute('SELECT payload_json FROM crawl_pending').fetchone()[0]);value['items'][0]['retry_at']=''
        c.execute('UPDATE crawl_pending SET payload_json=?',(json.dumps(value),))
    store.acknowledge('latest|source',5,True)
    assert store.finish('latest|source')==2
    store.save_page('latest|source',2,{'mangas':[],'hasNextPage':False})
    assert store.finish('latest|source')==1


def test_pending_rejects_invalid_oversize_without_cursor_loss(env):
    _,database=env;store=CrawlPages(database)
    for payload in ({'mangas':[]},{'mangas':[None],'hasNextPage':False},
                    {'mangas':[{'id':1,'description':'x'*1100000}],'hasNextPage':False}):
        with pytest.raises(ValueError):store.save_page('work',1,payload)
        assert store.load('work') is None


def test_independent_due_clocks_and_deferral_preserve_success(env):
    _,database=env;due=DueScheduler(database)
    due.attempted('library',1);success=due.finished('library',1,complete=True,success=True)
    assert not due.eligible('library',1)
    assert due.eligible('catalog_recent','x') and due.eligible('catalog_backlog','x')
    due.attempted('library',1);deferred=due.finished('library',1,deferred=True,reason='capacity')
    assert deferred['failures']==0
    assert deferred['last_complete']==success['last_complete'] and deferred['last_success']==success['last_success']
    assert deferred['last_attempt']>=success['last_attempt']
    assert due.eligible('library',1,configuration='changed-policy')


def test_recent_and_backlog_fair_for_even_number_of_source_pairs(env):
    from app.due_scheduler import crawl_lane
    _,database=env;due=DueScheduler(database)
    for key in ('a','b','c','d'):
        assert crawl_lane(due,key,'v1')=='recent'
        due.attempted('catalog_recent',key,'v1');due.finished('catalog_recent',key,complete=True,interval=1800)
        assert crawl_lane(due,key,'v1')=='backlog'
        due.attempted('catalog_backlog',key,'v1');due.finished('catalog_backlog',key,complete=True,interval=10)
        with database.catalog() as c:c.execute("UPDATE due_work SET next_due='2000-01-01' WHERE kind='catalog_recent' AND work_key=?",(key,))
        assert crawl_lane(due,key,'v1')=='recent'


def test_journal_unchanged_flush_is_noop_and_clear_removes(env):
    _,database=env;journal=TaskJournal(database,None)
    records=[{'id':'finished','updated_at':'2026-01-01','state':'completed'}]
    journal.write(records)
    with database.catalog() as c:before=c.total_changes
    journal.write(records)
    with database.catalog() as c:assert c.total_changes==before
    journal.write([])
    with database.catalog() as c:assert c.execute('SELECT COUNT(*) FROM general_task_history').fetchone()[0]==0


@pytest.mark.asyncio
async def test_metadata_inflight_shares_only_equal_policy_and_cancellation(monkeypatch):
    client=metadata_catalog.MetadataCatalogClient();calls=[];started=asyncio.Event();release=asyncio.Event()
    monkeypatch.setattr(client,'enabled_provider_keys',lambda:['anilist'])
    monkeypatch.setattr(client,'_http_client',lambda:None)
    async def search(_client,query,limit):
        calls.append((query,limit));started.set();await release.wait();return [{'title':'result'}]
    monkeypatch.setattr(client,'_search_anilist',search)
    a=asyncio.create_task(client.search_provider('anilist','Exact query'))
    await started.wait()
    b=asyncio.create_task(client.search_provider('anilist','Exact query'))
    await asyncio.sleep(0);a.cancel()
    with pytest.raises(asyncio.CancelledError):await a
    assert not b.done();release.set();assert await b==[{'title':'result'}]
    assert len(calls)==1 and not client._inflight
    await client.search_provider('anilist','Exact query',force_revision=True)
    assert len(calls)==2
    await client.close()


@pytest.mark.asyncio
async def test_metadata_failed_owner_evicted_and_last_waiter_drained(monkeypatch):
    client=metadata_catalog.MetadataCatalogClient();started=asyncio.Event();drained=asyncio.Event()
    monkeypatch.setattr(client,'enabled_provider_keys',lambda:['anilist']);monkeypatch.setattr(client,'_http_client',lambda:None)
    async def search(*args):
        started.set()
        try:await asyncio.Future()
        finally:drained.set()
    monkeypatch.setattr(client,'_search_anilist',search)
    task=asyncio.create_task(client.search_provider('anilist','q'));await started.wait();task.cancel()
    with pytest.raises(asyncio.CancelledError):await task
    assert drained.is_set() and not client._inflight
    async def failed(*args):raise ValueError('fixture')
    monkeypatch.setattr(client,'_search_anilist',failed)
    with pytest.raises(ValueError):await client.search_provider('anilist','q')
    assert not client._inflight
    await client.close()


@pytest.mark.asyncio
async def test_forced_metadata_refresh_fences_late_ordinary_cache_commit(env,monkeypatch):
    from app import service as service_module
    service,_=env;started=asyncio.Event();release=asyncio.Event();calls=[]
    async def search(*args,**kwargs):
        calls.append(kwargs['force_revision'])
        if len(calls)==1:
            started.set();await release.wait();return [{'title':'older response'}]
        return [{'title':'forced fresh response'}]
    monkeypatch.setattr(service_module.metadata_catalog,'search_provider',search)
    old=asyncio.create_task(service._metadata_provider_search('anilist','Exact work'))
    await started.wait()
    fresh=await service._metadata_provider_search('anilist','Exact work',force=True)
    release.set();assert await old==[{'title':'older response'}]
    assert service._metadata_cache_read('anilist','Exact work')==fresh==[{'title':'forced fresh response'}]
    assert calls[0] is not calls[1]


def candidate(database):
    with database.origins() as o:
        vid=o.execute("INSERT INTO virtual_chapters(entry_id,chapter_key,display_name,created_at,updated_at) VALUES(1,'one','One','now','now')").lastrowid
        cid=o.execute("INSERT INTO chapter_candidates(virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,quality_score,first_seen,last_seen) VALUES(?,'one','One',1,1,'One',75,'now','now')",(vid,)).lastrowid
        o.executemany('INSERT INTO candidate_pages VALUES(?,?,?,?)',[(cid,0,'/one','now'),(cid,1,'/two','now')])
        o.execute("INSERT INTO quality_samples VALUES(?,2,0,1000,1000,100000,'a','b','2026-01-01')",(cid,))
    return cid


@pytest.mark.asyncio
async def test_quality_timeout_keeps_previous_score_and_age(env,monkeypatch):
    _,database=env;cid=candidate(database);monkeypatch.setattr(quality,'db',database)
    async def fake(items,*args,**kwargs):return [(p,None,TimeoutError()) for p in items]
    monkeypatch.setattr(quality,'bounded_map',fake)
    result=await quality.analyze_candidate(cid)
    assert result['quality_score']==75 and result['broken_pages']==0 and result['unobserved_pages']==2
    assert not result['complete'] and result['analyzed_at']=='2026-01-01'
    with database.origins() as o:assert o.execute('SELECT quality_score FROM chapter_candidates WHERE id=?',(cid,)).fetchone()[0]==75
    with database.catalog() as c:assert c.execute('SELECT outcome FROM quality_attempts').fetchone()[0]=='incomplete'


@pytest.mark.asyncio
async def test_received_corrupt_images_can_update_score(env,monkeypatch):
    _,database=env;cid=candidate(database);monkeypatch.setattr(quality,'db',database)
    async def fake(items,*args,**kwargs):return [(p,None,quality.BrokenImage('fixture')) for p in items]
    monkeypatch.setattr(quality,'bounded_map',fake)
    result=await quality.analyze_candidate(cid)
    assert result['complete'] and result['broken_pages']==2 and result['quality_score']==0


def test_decode_errors_and_limit_refusals_are_distinct():
    with pytest.raises(quality.BrokenImage):quality._inspect_image(b'not an image')
    with pytest.raises(ValueError) as error:quality._inspect_image(b'')
    assert not isinstance(error.value,quality.BrokenImage)


@pytest.mark.asyncio
async def test_decode_slot_drains_real_thread_before_releasing(monkeypatch):
    started=threading.Event();release=threading.Event();calls=[]
    monkeypatch.setattr(quality.settings,'quality_analysis_concurrency',1)
    def inspect(content):
        calls.append(content);started.set()
        assert release.wait(3)
        return 1,1,1,''
    monkeypatch.setattr(quality,'_inspect_image',inspect)
    first=asyncio.create_task(quality._decode(b'one'))
    assert await asyncio.to_thread(started.wait,2)
    first.cancel();second=asyncio.create_task(quality._decode(b'two'))
    await asyncio.sleep(.02)
    assert calls==[b'one'] and not first.done() and not second.done()
    release.set()
    with pytest.raises(asyncio.CancelledError):await first
    assert await second==(1,1,1,'') and calls==[b'one',b'two']


def test_runtime_metrics_labels_and_sample_buffers_are_bounded():
    from app.runtime_observability import RuntimeMetrics
    metrics=RuntimeMetrics()
    for _ in range(300):
        with metrics.phase('projection'):pass
    row=next(r for r in metrics.snapshot()['phases'] if r['phase']=='projection')
    assert row['count']==300 and row['samples']==128
    with pytest.raises(ValueError):
        with metrics.phase('https://private.invalid/title/token'):pass


@pytest.mark.asyncio
@pytest.mark.parametrize('version,batch',[('2.3.2243',False),('2.4.2366',True)])
async def test_engine_capability_fixtures_never_claim_native_qualification(env,version,batch):
    from app import source_engine_contract
    _,database=env
    fields=['updateManga','enqueueChapterDownloads','fetchMangaAndChapters','fetchChapterPages','reorderChapterDownload']
    if batch:fields.append('reorderChapterDownloads')
    class Response:
        def json(self):return {'version':version}
        def raise_for_status(self):pass
    async def get(*args,**kwargs):return Response()
    async def graphql(*args,**kwargs):return {'__schema':{'mutationType':{'fields':[{'name':name} for name in fields]}}}
    client=SimpleNamespace(base_url='http://fixture.invalid',client=SimpleNamespace(get=get),graphql=graphql)
    result=await source_engine_contract.check(database,client,'main')
    assert result['contract_ok'] and result['batch_queue_reorder']==batch
    assert result['native_qualified'] is False and source_engine_contract.status(database)['pin_promoted'] is False
