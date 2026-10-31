"""Foreground import retries fence every external add against current decisions."""
import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from app import service as module
from test_v087_recovery import env, entry
from test_v0326_performance_tasks import import_rows


@pytest.mark.asyncio
@pytest.mark.parametrize('outcome',['not_applied_timeout','applied_timeout','successful','applied_timeout_lookup_fails'])
async def test_foreground_import_skip_during_issued_add_prevents_retries_and_preserves_observed_state(env,monkeypatch,outcome):
    service,database=env
    target=entry(service,'anchor',0,attach=False)
    (item,)=import_rows(database,[{'state':'unresolved'}])
    issued,release=asyncio.Event(),asyncio.Event()
    membership=False
    writes=[]
    reads=[]
    async def set_library(mid,in_library):
        nonlocal membership
        writes.append((mid,in_library))
        assert in_library, 'A superseded import must never undo an uncertain external write'
        if len(writes)==1:
            if outcome.startswith('applied'):membership=True
            issued.set()
            await release.wait()
            if outcome!='successful':raise httpx.ReadTimeout('Response was lost')
        membership=True
        return {'id':mid,'inLibrary':True}
    async def library():
        reads.append(True)
        if outcome.endswith('lookup_fails'):raise RuntimeError('Temporary read failure')
        return [{'id':44,'inLibrary':True}] if membership else []
    main=SimpleNamespace(set_manga_in_library=set_library,library_mangas=library)
    monkeypatch.setattr(module,'main_suwayomi',main)
    monkeypatch.setattr(service,'_bind_backup_source_to_entry',AsyncMock(return_value={'entry_id':target}))
    monkeypatch.setattr(service,'_main_autosourcarr_source_id',AsyncMock(return_value='autosourcarr'))
    monkeypatch.setattr(service,'_main_manga_for_autosourcarr_entry',AsyncMock(return_value={'id':44,'inLibrary':False}))
    service._subbed_cache=(time.monotonic(),{'entry_ids':[]})
    pending=asyncio.create_task(service.match_import_item('run',item,target))
    try:
        await asyncio.wait_for(issued.wait(),1)
        service.skip_import_item('run',item)
        newer=service._read_import_item('run',item)
        release.set()
        result=await asyncio.wait_for(pending,1)
        assert result['superseded']
        assert service._read_import_item('run',item)==newer
        assert newer['state']=='skipped' and newer['decision_revision']==6
        assert writes==[(44,True)]
        assert membership==(outcome!='not_applied_timeout')
        assert len(reads)==(0 if outcome=='successful' else 1)
        assert service._subbed_cache is None
    finally:
        release.set()
        await asyncio.gather(pending,return_exceptions=True)


@pytest.mark.asyncio
async def test_foreground_import_skip_during_retry_delay_stops_the_next_add(env,monkeypatch):
    service,database=env
    target=entry(service,'anchor',0,attach=False)
    (item,)=import_rows(database,[{'state':'unresolved'}])
    waiting,resume=asyncio.Event(),asyncio.Event()
    original_sleep=asyncio.sleep
    async def controlled_sleep(delay,*args,**kwargs):
        if delay==.2:
            waiting.set()
            await resume.wait()
            return
        return await original_sleep(delay,*args,**kwargs)
    monkeypatch.setattr(module.asyncio,'sleep',controlled_sleep)
    writes=[]
    async def set_library(mid,in_library):
        writes.append((mid,in_library))
        if len(writes)==1:raise httpx.ConnectError('First request did not reach the server')
        return {'id':mid,'inLibrary':True}
    main=SimpleNamespace(set_manga_in_library=set_library,library_mangas=AsyncMock(return_value=[]))
    monkeypatch.setattr(module,'main_suwayomi',main)
    monkeypatch.setattr(service,'_bind_backup_source_to_entry',AsyncMock(return_value={'entry_id':target}))
    monkeypatch.setattr(service,'_main_autosourcarr_source_id',AsyncMock(return_value='autosourcarr'))
    monkeypatch.setattr(service,'_main_manga_for_autosourcarr_entry',AsyncMock(return_value={'id':44,'inLibrary':False}))
    pending=asyncio.create_task(service.match_import_item('run',item,target))
    try:
        await asyncio.wait_for(waiting.wait(),1)
        service.skip_import_item('run',item)
        newer=service._read_import_item('run',item)
        resume.set()
        assert (await asyncio.wait_for(pending,1))['superseded']
        assert writes==[(44,True)] and service._read_import_item('run',item)==newer
        main.library_mangas.assert_awaited_once()
    finally:
        resume.set()
        await asyncio.gather(pending,return_exceptions=True)


@pytest.mark.asyncio
async def test_library_retry_guard_stale_before_first_request_has_no_external_effect(env,monkeypatch):
    service,database=env
    (item,)=import_rows(database,[{'state':'unresolved'}])
    service.skip_import_item('run',item)
    main=SimpleNamespace(set_manga_in_library=AsyncMock(),library_mangas=AsyncMock())
    monkeypatch.setattr(module,'main_suwayomi',main)
    result=await service._ensure_main_suwayomi_library(44,decision=('run',item,4))
    assert result['superseded'] and 'inLibrary' not in result
    main.set_manga_in_library.assert_not_called()
    main.library_mangas.assert_not_called()


@pytest.mark.asyncio
async def test_library_retry_without_an_import_decision_keeps_legacy_confirmation_fallback(env,monkeypatch):
    service,_=env
    main=SimpleNamespace(set_manga_in_library=AsyncMock(side_effect=[httpx.ReadTimeout('lost'),{'id':44},{'id':44}]),
        library_mangas=AsyncMock(return_value=[{'id':44,'title':'Existing member'}]))
    monkeypatch.setattr(module,'main_suwayomi',main)
    monkeypatch.setattr(module.asyncio,'sleep',AsyncMock())
    result=await service._ensure_main_suwayomi_library(44)
    assert result=={'id':44,'title':'Existing member'}
    assert main.set_manga_in_library.await_count==3
    assert all(call.args==(44,True) for call in main.set_manga_in_library.await_args_list)
    main.library_mangas.assert_awaited_once()
