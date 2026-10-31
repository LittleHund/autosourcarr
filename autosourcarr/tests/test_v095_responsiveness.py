"""Storage contention must not freeze independent HTTP work or lose ownership."""
import asyncio
import threading
import time
from collections import OrderedDict

import pytest
from app import main, source_engine as engine
from app.config import Settings
from app.read_lane import ReadLane
from app.service import service


@pytest.mark.asyncio
async def test_source_cover_db_wait_does_not_block_engine_status(monkeypatch):
    entered, release = threading.Event(), threading.Event()
    loop_thread = threading.get_ident()
    def hint(mid):
        assert threading.get_ident() != loop_thread
        entered.set()
        assert release.wait(2)
        return {"source_manga_id": mid}
    async def uncached(key, load): return await load()
    async def image(*args, **kwargs): return b'fixture', 'image/png'
    lane = ReadLane(workers=1)
    monkeypatch.setattr(main, 'cover_read_lane', lane)
    monkeypatch.setattr(service, 'source_cover_hint', hint)
    monkeypatch.setattr(main.cover_cache, 'get', uncached)
    monkeypatch.setattr(main, '_load_cover', image)
    request = asyncio.create_task(main.proxy_source_cover(42))
    try:
        assert await asyncio.to_thread(entered.wait, 1)
        status = await asyncio.wait_for(main.admin_source_engine(), .2)
        assert 'mode' in status and not request.done()
    finally:
        release.set()
        await request
        await lane.close()


@pytest.mark.asyncio
async def test_cached_unified_cover_never_queries_database(monkeypatch):
    async def cached(key):return b'image', 'image/png'
    monkeypatch.setattr(main.cover_cache, 'read_async', cached)
    monkeypatch.setattr(service, 'canonical_entry_id', lambda *_: pytest.fail('cached cover queried database'))
    result = await main.proxy_cover(123)
    assert result.body == b'image'


@pytest.mark.asyncio
async def test_cached_source_cover_does_not_fetch_hint(monkeypatch):
    async def cached(key, load):return b'image', 'image/png'
    monkeypatch.setattr(main.cover_cache, 'get', cached)
    monkeypatch.setattr(service, 'source_cover_hint', lambda *_: pytest.fail('cache hit fetched hint'))
    assert (await main.proxy_source_cover(123)).body == b'image'


@pytest.mark.asyncio
async def test_fingerprint_cache_write_is_offloop_and_drained_on_cancel(monkeypatch):
    entered, release, finished = threading.Event(), threading.Event(), threading.Event()
    monkeypatch.setattr(main.settings, 'exact_thumbnail_matching', True)
    loop_thread = threading.get_ident()
    def read(key):
        assert threading.get_ident() != loop_thread
        return None
    def write(key, result, url):
        assert threading.get_ident() != loop_thread
        entered.set()
        assert release.wait(2)
        finished.set()
        return result
    async def image(*args, **kw):return b'image', 'image/png'
    import app.service as module
    monkeypatch.setattr(service, '_thumbnail_fingerprint_cache_get', read)
    monkeypatch.setattr(service, '_thumbnail_fingerprint_cache_put', write)
    monkeypatch.setattr(module.source_host, 'fetch_external_binary', image)
    monkeypatch.setattr(module, 'exact_thumbnail_fingerprint', lambda _: {'fingerprint':'fixture'})
    job = asyncio.create_task(service._thumbnail_fingerprint_for_url('https://source.invalid/cover'))
    try:
        assert await asyncio.to_thread(entered.wait, 1)
        job.cancel()
        await asyncio.sleep(.02)
        assert not job.done() and not finished.is_set()
    finally:
        release.set()
        with pytest.raises(asyncio.CancelledError):await job
    assert finished.is_set()


@pytest.mark.asyncio
async def test_cover_recovery_schedules_on_loop_with_single_lookup(monkeypatch):
    loop_thread = threading.get_ident()
    lookups, scheduled = [], []
    monkeypatch.setattr(service, '_cover_recovery_attempts', OrderedDict(), raising=False)
    monkeypatch.setattr(service, '_background_stopping', False)
    def candidate(eid):
        assert threading.get_ident() != loop_thread
        lookups.append(eid)
        return {'source_id':'test','source_manga_id':42}
    def schedule(*args, **kwargs):
        assert threading.get_ident() == loop_thread
        scheduled.append(args)
        return True
    monkeypatch.setattr(service, 'entry_cover_recovery_candidate', candidate)
    monkeypatch.setattr(service, 'schedule_source_metadata_hydration', schedule)
    assert await service.recover_entry_cover(999)
    assert not await service.recover_entry_cover(999)
    assert lookups == [999] and scheduled == [('test', 42)]


def test_integrated_is_the_real_default(monkeypatch):
    monkeypatch.delenv('SOURCE_ENGINE_MODE', raising=False)
    value = Settings(source_suwayomi_url='http://old-source-host:4567')
    assert value.source_engine_mode == 'integrated'
    assert value.effective_source_url == 'http://127.0.0.1:4567'


@pytest.mark.asyncio
async def test_missing_image_is_setup_required_without_restart_loop(monkeypatch, tmp_path):
    monkeypatch.setattr(engine.settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(engine, 'ENGINE_COMMAND', str(tmp_path/'missing'))
    recorded=[]
    monkeypatch.setattr(engine.error_registry, 'record', lambda *a, **kw:recorded.append((a,kw)))
    runner=engine.SourceEngine()
    runner.start()
    await asyncio.sleep(0)
    assert runner.state == 'setup-required' and runner.task is None
    assert runner.restarts == 0 and len(recorded) == 1
    assert not runner.snapshot()['runtime_available']
    assert not runner.snapshot()['separate_container_required']
    assert 'no integrated source runtime' in runner.error


def test_legacy_wrapper_starts_diagnostics_without_external_fallback(tmp_path):
    import json
    import os
    import subprocess
    from pathlib import Path
    wrapper = Path(__file__).resolve().parents[1]/'start-truenas.sh'
    app = tmp_path/'application'; (app/'app').mkdir(parents=True)
    deps = tmp_path/'deps'
    # Stub the already-tested dependency installer; execute the actual shell path.
    (app/'app/dependency_cache.py').write_text('import sys; print(sys.argv[1])')
    (app/'uvicorn.py').write_text('import json,os,sys; print(json.dumps({"args":sys.argv[1:],"mode":os.environ["SOURCE_ENGINE_MODE"]}))')
    env = {**os.environ, 'SOURCE_ENGINE_MODE':'integrated', 'AUTOSOURCARR_APP_DIR':str(app), 'AUTOSOURCARR_DEPS_DIR':str(deps)}
    if Path('/home/suwayomi/startup_script.sh').exists():
        pytest.skip('This fixture expects a Python-only host')
    result = subprocess.run(['sh',str(wrapper)],env=env,capture_output=True,text=True)
    assert result.returncode == 0, result.stderr
    assert 'starting the UI in setup-required mode' in result.stderr
    launched=json.loads(result.stdout)
    assert launched['mode']=='integrated'
    assert launched['args'][0]=='app.main:app'
