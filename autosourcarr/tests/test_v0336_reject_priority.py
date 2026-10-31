import asyncio
from unittest.mock import AsyncMock

import pytest

from test_v087_recovery import env,source,entry


@pytest.mark.asyncio
async def test_reject_cleanup_progresses_while_automatic_lane_is_full(env,monkeypatch):
    service,database = env
    source(database,'seed')
    eid=entry(service,'seed',301)
    service._create_import_run('reject-priority','fixture.tachibk')
    iid=service._store_import_item('reject-priority',1,{'title':'Monster List','source':0},
        state='added',reason='Verified addition',entry_id=eid,generate_candidates=False)
    with database.catalog() as connection:
        connection.execute('UPDATE import_items SET library_add_verified=1 WHERE id=?',(iid,))
    cleaned,discovered=asyncio.Event(),asyncio.Event()
    async def cleanup(*args,**kwargs):
        cleaned.set()
        return {'removed':True,'reason':'Verified new addition removed'}
    async def alternatives(*args,**kwargs):
        discovered.set()
        assert kwargs['automatic'] is True
        return {'suggestions':[]}
    monkeypatch.setattr(service,'_remove_import_added_entry_from_main_library',cleanup)
    monkeypatch.setattr(service,'import_item_live_suggestions',alternatives)
    service._background_task_semaphore=asyncio.Semaphore(1)
    await service._background_task_semaphore.acquire()
    queued=service.schedule_import_item_reject('reject-priority',iid)
    runtime=service._refresh_tasks[f'__import-reject__:reject-priority:{iid}']
    try:
        assert queued['queued']
        assert service._read_import_item('reject-priority',iid)['state']=='unresolved'
        await asyncio.wait_for(cleaned.wait(),.4)
        assert not discovered.is_set()
        # Waiting for automatic suggestions must release the manual lane.
        await asyncio.wait_for(service._manual_task_semaphore.acquire(),.4)
        service._manual_task_semaphore.release()
    finally:
        service._background_task_semaphore.release()
        await asyncio.wait_for(runtime,2)
    assert discovered.is_set()
