import httpx
import pytest
from test_v0345_raw_api import api
from test_v0345_raw_archive import PKG


@pytest.mark.asyncio
async def test_explicit_discovery_checks_revision_and_queues_owned_work(api,monkeypatch):
    from app.source_raws import source_raws
    app,extension,catalog=api;queued=[]
    monkeypatch.setattr(source_raws,'discover_intent',lambda row,ext:queued.append((row,ext)))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        url='/api/admin/source-maintenance/raws/'+PKG
        status=(await client.get(url)).json()
        assert (await client.post(url+'/discover',json={'revision':True})).status_code==400
        assert (await client.post(url+'/discover',json={'revision':status['revision']+1})).status_code==409
        assert queued==[]
        response=await client.post(url+'/discover',json={'revision':status['revision']})
        assert response.status_code==202 and len(queued)==1
        assert queued[0][0]['binary_sha256']==status['binary_sha256']
        assert queued[0][1]['pkgName']==PKG
