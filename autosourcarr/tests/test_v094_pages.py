import asyncio
from unittest.mock import AsyncMock
import httpx
import pytest
from app import service as module
from app.service import ChapterPagesBlocked
from app.source_errors import chapter_retry_delay
from app.error_registry import ErrorRegistry
from test_v087_recovery import env
from test_v091_pages import chapter_fixture


@pytest.mark.parametrize('code,delay',[(403,30),(429,60),(521,30)])
def test_typed_http_failure_keeps_cause_and_context_on_retry(env,monkeypatch,code,delay):
    service,_,vid=chapter_fixture(env,monkeypatch)
    response=httpx.Response(code,request=httpx.Request('GET','https://source.example/chapter'))
    error=httpx.HTTPStatusError(f'Upstream refused ({code})',request=response.request,response=response)
    fetch=AsyncMock(side_effect=error);monkeypatch.setattr(module.source_host,'fetch_chapter_pages',fetch)
    async def scenario():
        for _ in range(4):
            with pytest.raises(ChapterPagesBlocked) as caught:await service.resolve_pages(vid)
            assert f'HTTP {code}' in str(caught.value)
            assert caught.value.source_context['source_id']=='a'
            assert 1<=caught.value.retry_after<=delay
        assert fetch.await_count==1
    asyncio.run(scenario())


def test_403_allows_another_candidate(env,monkeypatch):
    service,_,vid=chapter_fixture(env,monkeypatch,fallback=True)
    fetch=AsyncMock(side_effect=[RuntimeError('HTTP error 403'),['https://image.example/1']])
    monkeypatch.setattr(module.source_host,'fetch_chapter_pages',fetch)
    result=asyncio.run(service.resolve_pages(vid))
    assert result['candidate']['source_id']=='b' and fetch.await_count==2


def test_stale_fallback_keeps_failed_source_context(env,monkeypatch):
    service,database,vid=chapter_fixture(env,monkeypatch)
    fetch=AsyncMock(return_value=['https://image.example/1']);monkeypatch.setattr(module.source_host,'fetch_chapter_pages',fetch)
    async def scenario():
        await service.resolve_pages(vid)
        with database.origins() as c:c.execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00Z'")
        fetch.side_effect=RuntimeError('HTTP error 403')
        result=await service.resolve_pages(vid)
        assert result['page_cache_stale'] and result['source_warning_context']['source_id']=='a'
        assert '403' in result['source_warning']
    asyncio.run(scenario())


def test_unknown_failure_not_cooled_and_snapshot_owns_context():
    assert chapter_retry_delay(RuntimeError('chapter id 403 not found'))==0
    registry=ErrorRegistry();registry.record('Source','403',source_context={'source_id':'one','secret':'never'})
    row=registry.snapshot()['items'][0];row['source_context']['source_id']='changed'
    assert registry.snapshot()['items'][0]['source_context']=={'source_id':'one'}
