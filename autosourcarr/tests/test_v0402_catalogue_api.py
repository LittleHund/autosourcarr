import asyncio
import json
from contextlib import asynccontextmanager
import httpx
import pytest
from fastapi import FastAPI, Header, HTTPException

from test_v0402_catalogue_exchange import worlds
from app import catalog_exchange_format as wire
from app.config import settings
from app.library_manager import LibraryManager
from app.reimagined_api import router


@pytest.mark.asyncio
async def test_admin_only_streamed_preview_atomic_apply_reconciliation_and_gallery_cas(worlds, monkeypatch):
    a, b = worlds('sender'), worlds('receiver')
    with a.active():
        a.native('API shared publication', '/api-work')
        raw = a.dump()
    with b.active():
        b.native('Other local work', '/local', 900)
        manager = LibraryManager(b.db, b.service, None)
        async def guard(x_admin_token: str = Header('')):
            if x_admin_token != 'test-only-secret':
                raise HTTPException(401, 'Authentication required')
        @asynccontextmanager
        async def paused():
            yield
        app = FastAPI()
        app.include_router(router(b.db, b.service, manager, settings, guard, asyncio.Lock(), paused, lambda: None))
        scheduled = []
        def schedule(sid):
            scheduled.append(sid)
            return {'queued': True, 'task_id': 'fixture-task'}
        monkeypatch.setattr(b.service, 'schedule_gallery_layout', schedule)
        headers = {'X-Admin-Token': 'test-only-secret'}
        base = '/api/admin/catalog-sharing'
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://fixture') as client:
            for method, path in [('GET', '/status'), ('POST', '/export'), ('POST', '/preview'), ('POST', '/apply'), ('GET', '/decisions'), ('GET', '/records')]:
                assert (await client.request(method, base+path)).status_code == 401
            assert (await client.post(base+'/preview', content=b'not a catalogue', headers=headers)).status_code == 409
            monkeypatch.setattr(wire, 'MAX_BYTES', len(raw)-1)
            assert (await client.post(base+'/preview', content=raw, headers=headers)).status_code == 413
            monkeypatch.setattr(wire, 'MAX_BYTES', 64*1024*1024)
            preview = (await client.post(base+'/preview', content=raw, headers=headers)).json()
            assert preview['added'] == 1 and b.count() == 1
            body = {k: preview[k] for k in ('id', 'revision')}
            result = await client.post(base+'/apply', json=body, headers=headers)
            assert result.status_code == 200 and result.json()['added'] == 1 and b.count() == 2
            assert (await client.post(base+'/apply', json=body, headers=headers)).json()['already_applied']
            staged = (await client.get(base+'/stages/'+preview['id'], headers=headers)).json()
            assert staged['state'] == 'applied' and staged['result']['added'] == 1
            assert (await client.get(base+'/stages/'+preview['id']+'?limit=201', headers=headers)).status_code == 422
            assert (await client.post(base+'/export', json={'limit': 1, 'sql': 'untrusted'}, headers=headers)).status_code == 422
            first = (await client.post(base+'/export', json={'limit': 1}, headers=headers)).json()
            assert len(first['catalogue']['entries']) == 1 and first['next_cursor'] is not None
            policy_url = '/api/admin/gallery-policies/17'
            assert (await client.get(policy_url)).status_code == 401
            old = (await client.get(policy_url, headers=headers)).json()
            value = {'revision': old['revision'], 'policy': {**old['policy'], 'enabled': False}}
            saved = await client.put(policy_url, json=value, headers=headers)
            assert saved.status_code == 200 and saved.json()['task']['queued'] and scheduled == ['17']
            assert (await client.put(policy_url, json=value, headers=headers)).status_code == 409
            assert (await client.get('/api/admin/gallery-policies/9999', headers=headers)).status_code == 404
            fresh = (await client.post(base+'/preview', content=raw, headers=headers)).json()
            b.service.set_preferred_title(2, 'A newer local decision')
            stale = await client.post(base+'/apply', json={k: fresh[k] for k in ('id','revision')}, headers=headers)
            assert stale.status_code == 409
            assert b.service.get_entry(2)['preferred_title'] == 'A newer local decision'
