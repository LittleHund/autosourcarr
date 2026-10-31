import asyncio
import gzip

import httpx
import pytest

from app.lifecycle import cleanup_steps
from app.asset_decode import buffered_asset
from app.cover_cache import CoverCache


@pytest.mark.asyncio
async def test_cleanup_attempts_all_steps_and_retains_first_failure():
    calls=[]
    async def stop(name, fail=False):
        calls.append(name)
        if fail: raise ValueError(name)
    with pytest.raises(ValueError,match='first'):
        await cleanup_steps([('first',lambda:stop('first',True)),('second',lambda:stop('second',True)),('last',lambda:stop('last'))])
    assert calls==['first','second','last']


@pytest.mark.asyncio
async def test_cleanup_drains_after_repeated_parent_cancellation():
    entered=asyncio.Event(); release=asyncio.Event(); calls=[]
    async def slow():
        entered.set(); await release.wait(); calls.append('drained')
    task=asyncio.create_task(cleanup_steps([('slow',slow),('last',lambda:asyncio.sleep(0))]))
    await entered.wait(); task.cancel(); await asyncio.sleep(0); task.cancel()
    assert not task.done()
    release.set()
    with pytest.raises(asyncio.CancelledError): await task
    assert calls==['drained']


@pytest.mark.asyncio
async def test_cleanup_does_not_mask_original_startup_error():
    async def fail(): raise ValueError('cleanup')
    await cleanup_steps([('fail',fail)],primary=RuntimeError('startup'))


@pytest.mark.asyncio
async def test_cover_backlog_is_bounded_and_coalesced(tmp_path):
    cache=CoverCache(max_pending=2,loader_deadline=.5); cache.root=tmp_path
    entered=asyncio.Event(); release=asyncio.Event(); calls=0
    async def load():
        nonlocal calls
        calls+=1; entered.set(); await release.wait(); return b'cover','image/png'
    await cache.get('a',load,wait=0); await entered.wait()
    await cache.get('a',load,wait=0)
    for i in range(25): await cache.get(str(i),load,wait=0)
    assert len(cache._tasks)==2
    assert len(cache._generations)==2
    release.set(); await asyncio.gather(*list(cache._tasks.values()))
    assert calls==2
    assert not cache._tasks


@pytest.mark.asyncio
async def test_cover_total_deadline_cancels_owned_loader(tmp_path):
    cache=CoverCache(max_pending=2,loader_deadline=.025); cache.root=tmp_path
    stopped=asyncio.Event()
    async def trickle():
        try:
            while True: await asyncio.sleep(.002)
        finally: stopped.set()
    assert await cache.get('slow',trickle,wait=.2) is None
    assert stopped.is_set() and not cache._tasks
    assert not list(tmp_path.iterdir())


class Body(httpx.AsyncByteStream):
    def __init__(self,data): self.data=data
    async def __aiter__(self):
        for i in range(0,len(self.data),7): yield self.data[i:i+7]


@pytest.mark.asyncio
async def test_buffered_gzip_has_decoded_cap_and_supports_valid_small_input():
    response=httpx.Response(200,headers={'content-encoding':'gzip'},stream=Body(gzip.compress(b'x'*1048576)))
    with pytest.raises(ValueError,match='limit'): await buffered_asset(response,131072)
    response=httpx.Response(200,headers={'content-encoding':'gzip'},stream=Body(gzip.compress(b'normal image')))
    assert await buffered_asset(response,100)==b'normal image'


@pytest.mark.asyncio
async def test_buffered_compression_rejects_truncation_and_stacked_encoding():
    response=httpx.Response(200,headers={'content-encoding':'gzip'},stream=Body(gzip.compress(b'hello')[:-3]))
    with pytest.raises(ValueError,match='Truncated'): await buffered_asset(response,100)
    response=httpx.Response(200,headers={'content-encoding':'gzip, br'},stream=Body(b'garbage'))
    with pytest.raises(ValueError,match='encoding'): await buffered_asset(response,100)
