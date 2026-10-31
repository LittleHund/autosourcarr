"""Real client/SQLite verification gate, owned hourly retries and source fallback."""
import asyncio
import json
from types import SimpleNamespace

import httpx
import pytest
from app.source_attention import SourceAttention
from app.source_errors import assistance_reason
from app.suwayomi import SuwayomiClient, SuwayomiError, HumanAssistanceRequired
from test_v087_recovery import env, source, entry

WAF = 'Shape-selecting captcha detected. Open in WebView to solve manually or turn on the setting to solve automatically.'


@pytest.mark.parametrize('message,expected', [
    (WAF, 'WAF'), ('403 {"error":"captcha_required"}', 'WAF'),
    ('challenge at /@waf/challenge', 'WAF'), ('Failed to solve shape-selecting captcha', 'WAF'),
    ('CFClearance$CloudflareBypassException', 'Cloudflare'), ('human verification required', 'CAPTCHA'),
    ('HTTP error 403', ''), ('HTTP error 521', ''), ('HTTP 429 Too many requests', ''),
    ("Unknown type 'Long'", ''), ('ReadTimeout at /health', ''),
])
def test_classify_only_confirmed_verification(message, expected):
    reason = assistance_reason(RuntimeError(message))
    assert reason.startswith(expected) if expected else not reason


def setup(env, monkeypatch, handler, *, limit=2):
    _, database = env
    source(database, 'mf-en');source(database, 'mf-fr');source(database, 'other')
    with database.catalog() as conn:
        conn.execute("UPDATE source_config SET name='MangaFire',base_url='https://mangafire.to' WHERE source_id LIKE 'mf-%'")
        conn.execute("UPDATE source_config SET base_url='https://other.test' WHERE source_id='other'")
    now=[100000.0]
    attention=SourceAttention(database,clock=lambda:now[0])
    client=SuwayomiClient('http://engine.test',work_limit=limit)
    client.attention=attention
    async def graphql(query, variables=None):
        return await handler(query,variables or {})
    monkeypatch.setattr(client,'graphql',graphql)
    return attention,client,now


@pytest.mark.asyncio
async def test_pauses_whole_website_all_operations_and_retains_after_restart(env,monkeypatch):
    calls=[]
    async def upstream(q,v):calls.append(v);raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired) as error:await client.fetch_source_manga('mf-en','SEARCH',1,'example')
        assert error.value.retry_after==3600
        attention.learn({'manga':{'id':55,'sourceId':'mf-fr'},'chapters':[{'id':77,'mangaId':55}]})
        for request in [client.fetch_source_manga('mf-fr','POPULAR'),client.fetch_manga_and_chapters(55),client.fetch_chapter_pages(77)]:
            with pytest.raises(HumanAssistanceRequired):await request
        assert len(calls)==1
        assert (await attention.snapshot())['items'][0]['occurrences']==1
        restarted=SourceAttention(env[1],clock=lambda:now[0]);client.attention=restarted
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-fr','POPULAR')
        assert len(calls)==1
        # A different website still gets its own first attempt.
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('other','POPULAR')
        assert len(calls)==2 and (await restarted.snapshot())['count']==2
    finally:await client.close()


@pytest.mark.asyncio
async def test_waiting_source_requests_recheck_gate_after_admission(env,monkeypatch):
    started,release=asyncio.Event(),asyncio.Event();calls=[]
    async def upstream(q,v):calls.append(v);started.set();await release.wait();raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream,limit=1)
    jobs=[asyncio.create_task(client.fetch_source_manga('mf-en','POPULAR')) for _ in range(8)]
    try:
        await started.wait();await asyncio.sleep(.02);release.set()
        results=await asyncio.gather(*jobs,return_exceptions=True)
        assert all(isinstance(x,HumanAssistanceRequired) for x in results)
        assert len(calls)==1
        assert client._work_sem._value==1
    finally:release.set();await asyncio.gather(*jobs,return_exceptions=True);await client.close()


@pytest.mark.asyncio
async def test_one_hourly_probe_without_browser_and_manual_single_flight(env,monkeypatch):
    calls=[];release=asyncio.Event();probe_started=asyncio.Event();state={'fail':True,'hold':False}
    async def upstream(q,v):
        calls.append(v)
        if state['hold']:probe_started.set();await release.wait()
        if state['fail']:raise SuwayomiError(WAF)
        return {'fetchSourceManga':{'mangas':[],'hasNextPage':False}}
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        key=(await attention.snapshot())['items'][0]['id']
        now[0]+=3599;await attention.tick(client);assert len(calls)==1
        now[0]+=1;state['hold']=True
        tick=asyncio.create_task(attention.tick(client));await probe_started.wait()
        duplicate=await asyncio.gather(*(attention.retry(key,client,manual=True) for _ in range(5)))
        assert duplicate==[False]*5
        release.set();await tick
        assert len(calls)==2
        row=(await attention.snapshot())['items'][0]
        assert row['next_retry_at']==now[0]+3600 and row['occurrences']==2
        await attention.tick(client);assert len(calls)==2
        state['fail']=False;state['hold']=False
        assert await attention.retry(key,client,manual=True)
        assert (await attention.snapshot())['count']==0
        await client.fetch_source_manga('mf-fr','POPULAR');assert len(calls)==4
    finally:release.set();await client.close()


@pytest.mark.asyncio
async def test_probe_cancel_releases_ownership_but_retains_hourly_deadline(env,monkeypatch):
    admitted=asyncio.Event();state={'hold':False}
    async def upstream(q,v):
        if state['hold']:admitted.set();await asyncio.Event().wait()
        raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        key=(await attention.snapshot())['items'][0]['id'];state['hold']=True
        now[0]+=100
        task=asyncio.create_task(attention.retry(key,client,manual=True));await admitted.wait();task.cancel()
        with pytest.raises(asyncio.CancelledError):await task
        row=(await attention.snapshot())['items'][0]
        assert not row['retrying'] and row['next_retry_at']==now[0]+3600
        restarted=SourceAttention(env[1],clock=lambda:now[0]);assert (await restarted.snapshot())['items'][0]['next_retry_at']==row['next_retry_at']
        assert client._work_sem._value==client.work_limit
    finally:await client.close()


@pytest.mark.asyncio
async def test_stale_probe_success_does_not_clear_new_challenge(env,monkeypatch):
    admitted,release=asyncio.Event(),asyncio.Event();state={'hold':False}
    async def upstream(q,v):
        if state['hold']:admitted.set();await release.wait();return {'fetchSourceManga':{'mangas':[]}}
        raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        row=attention.records[next(iter(attention.records))];key=row['id'];state['hold']=True
        task=asyncio.create_task(attention.retry(key,client,manual=True));await admitted.wait()
        # Another already-admitted source operation fails after this probe starts.
        with pytest.raises(HumanAssistanceRequired):await attention.failed(dict(row),row['operation'],SuwayomiError(WAF))
        release.set();await task
        assert (await attention.snapshot())['count']==1
    finally:release.set();await client.close()


@pytest.mark.asyncio
async def test_cold_chapter_and_manga_context_comes_from_indexed_saved_records(env,monkeypatch):
    async def upstream(q,v):raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    service,database=env;eid=entry(service,'mf-en',120)
    service._ingest_chapters(eid,{'source_id':'mf-en','source_name':'MangaFire','source_manga_id':120},None,[{'id':121,'name':'One','chapterNumber':1}])
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-fr','POPULAR')
        with pytest.raises(HumanAssistanceRequired):await client.fetch_manga_and_chapters(120)
        with pytest.raises(HumanAssistanceRequired):await client.fetch_chapter_pages(121)
        assert (await attention.snapshot())['items'][0]['occurrences']==1
    finally:await client.close()


@pytest.mark.asyncio
async def test_reader_fallback_uses_other_source_after_human_deferral(env,monkeypatch):
    from app import service as module
    s,d=env;source(d,'blocked');source(d,'working');eid=entry(s,'blocked',20)
    s._upsert_match(eid,'working',{'id':30,'title':'Monster List','url':'/30'},1.0,'manual',True)
    for sid,mid,cid in [('blocked',20,21),('working',30,31)]:
        s._ingest_chapters(eid,{'source_id':sid,'source_name':sid,'source_manga_id':mid},None,[{'id':cid,'name':'One','chapterNumber':1}])
    with d.origins() as o:
        virtual=o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE source_chapter_id=21').fetchone()[0]
        o.execute('UPDATE chapter_candidates SET source_chapter_index=0')
    calls=[]
    async def pages(cid):
        calls.append(cid)
        if cid==21:raise HumanAssistanceRequired({'id':'waf','name':'blocked','reason':'WAF needs help'},3600)
        return ['https://working.test/page.jpg']
    monkeypatch.setattr(module.source_host,'fetch_chapter_pages',pages)
    result=await s.resolve_pages(virtual)
    assert result['candidate']['source_id']=='working' and 31 in calls
    assert not s._page_lookup_failures


@pytest.mark.asyncio
async def test_http_attention_requires_admin_and_reader_deferral_is_quiet(env,monkeypatch):
    from app import main
    from app.config import settings
    from app.error_registry import ErrorRegistry
    async def upstream(q,v):raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    monkeypatch.setattr(main,'source_attention',attention);monkeypatch.setattr(main,'source_host',client)
    monkeypatch.setattr(main,'service',env[0]);monkeypatch.setattr(settings,'admin_token','secret')
    registry=ErrorRegistry();monkeypatch.setattr(main,'error_registry',registry)
    try:
        with pytest.raises(HumanAssistanceRequired) as error:await client.fetch_source_manga('mf-en','POPULAR')
        async def pages(_):raise error.value
        monkeypatch.setattr(env[0],'resolve_pages',pages)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://app.test') as web:
            assert (await web.get('/api/admin/attention')).status_code==401
            result=await web.get('/api/admin/attention',headers={'X-Admin-Token':'secret'})
            assert result.status_code==200 and result.json()['count']==1
            for _ in range(3):
                result=await web.get('/api/v1/chapters/8/pages')
                assert result.status_code==503 and result.headers['retry-after']=='3600'
                assert result.json()['needs_attention'] is True
            assert registry.snapshot()['active_count']==0
    finally:await client.close()

@pytest.mark.asyncio
@pytest.mark.parametrize('method',['reset_database','full_reset_database'])
async def test_catalog_reset_retains_website_pause(env,monkeypatch,method):
    async def upstream(q,v):raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        before=await attention.snapshot()
        await asyncio.to_thread(getattr(env[0],method))
        restarted=SourceAttention(env[1],clock=lambda:now[0])
        assert await restarted.snapshot()==before
    finally:await client.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('kind',['browse','manga','pages'])
async def test_incomplete_probe_payload_never_resolves_attention(env,monkeypatch,kind):
    state={'fail':True}
    async def upstream(q,v):
        if state['fail']:raise SuwayomiError(WAF)
        return {}
    attention,client,now=setup(env,monkeypatch,upstream)
    attention.learn({'manga':{'id':55,'sourceId':'mf-en'},'chapters':[{'id':77,'mangaId':55}]})
    try:
        with pytest.raises(HumanAssistanceRequired):
            if kind=='browse':await client.fetch_source_manga('mf-en','POPULAR')
            elif kind=='manga':await client.fetch_manga_and_chapters(55)
            else:await client.fetch_chapter_pages(77)
        row=(await attention.snapshot())['items'][0];state['fail']=False
        assert not await attention.retry(row['id'],client,manual=True)
        assert (await attention.snapshot())['count']==1
    finally:await client.close()


@pytest.mark.asyncio
async def test_image_paths_cannot_bypass_known_pause(env,monkeypatch):
    async def upstream(q,v):raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    attention.learn({'manga':{'id':55,'sourceId':'mf-en'}})
    calls=[]
    async def transport(request):calls.append(request.url);return httpx.Response(200,content=b'image')
    await client.client.aclose();client.client=httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        for request in [client.open_page_image_stream(55,0,0),client.fetch_page_image(55,0,0),client.fetch_manga_thumbnail(55),client.fetch_external_binary('https://mangafire.to/image.jpg')]:
            with pytest.raises(HumanAssistanceRequired):await request
        assert not calls
        # Unrelated CDN images still work.
        assert (await client.fetch_external_binary('https://images.other.test/page.jpg'))[0]==b'image'
        assert len(calls)==1
    finally:await client.close()


@pytest.mark.asyncio
async def test_provisional_manga_pause_follows_learned_source_identity(env,monkeypatch):
    calls=[]
    async def upstream(q,v):calls.append(v);raise SuwayomiError(WAF)
    attention,client,now=setup(env,monkeypatch,upstream)
    try:
        with pytest.raises(HumanAssistanceRequired):await client.fetch_manga_and_chapters(98765)
        attention.learn({'manga':{'id':98765,'sourceId':'mf-en'}})
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-en','POPULAR')
        with pytest.raises(HumanAssistanceRequired):await client.fetch_source_manga('mf-fr','POPULAR')
        assert len(calls)==1
        row=(await attention.snapshot())['items'][0]
        assert row['website']=='mangafire.to' and row['next_retry_at']==now[0]+3600
    finally:await client.close()
