"""Actual admin router composition and stale code-upload requests; fake native API."""
import asyncio
import importlib
from unittest.mock import AsyncMock

from fastapi import FastAPI
import httpx
import pytest

from test_v0345_raw_archive import PKG,MODULE,FILES,archive


@pytest.fixture
def api(tmp_path,monkeypatch):
    maintenance=importlib.import_module('app.source_maintenance');raw=importlib.import_module('app.source_raws')
    workflow=importlib.import_module('app.repair_workflow').repair_workflow
    orphans=importlib.import_module('app.source_orphans')
    monkeypatch.setattr(maintenance.settings,'data_dir',tmp_path)
    native=tmp_path/'source.jar';native.write_bytes(b'Actual installed binary')
    extension={'pkgName':PKG,'versionName':'1.4.23','isInstalled':True,'source':{'nodes':[{'id':'123','name':'Example','lang':'en','baseUrl':'https://old.example/'}]}}
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(return_value=[extension]))
    monkeypatch.setattr(maintenance,'installed_path',lambda _:native)
    monkeypatch.setattr(orphans.source_orphans,'annotate_extensions',lambda values:values)
    monkeypatch.setattr(raw.source_raws,'catalog',raw.SourceRawCatalog(tmp_path))
    monkeypatch.setattr(raw.source_raws,'schedule_intent',lambda _:None)
    monkeypatch.setattr(workflow,'history',importlib.import_module('app.repair_history').RepairHistory(tmp_path/'history'))
    app=FastAPI();app.include_router(maintenance.maintenance_router(lambda:None,asyncio.Lock()))
    return app,extension,raw.source_raws.catalog


@pytest.mark.asyncio
async def test_single_admin_prefix_binds_project_upload_to_current_native_version_and_request_revision(api):
    app,extension,catalog=api
    paths=list(app.openapi()['paths'])
    assert paths.count('/api/admin/source-maintenance/raws/{pkg}/project')==1
    assert not any('/source-maintenance/api/' in path for path in paths)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        status=(await client.get('/api/admin/source-maintenance/raws/'+PKG)).json()
        assert status['state']=='needs-input'
        headers={'X-Raw-Revision':str(status['revision']),'X-Source-Version':'1.4.23','X-Source-Module':MODULE}
        extension['versionName']='1.4.24'
        stale=await client.post('/api/admin/source-maintenance/raws/'+PKG+'/project',content=archive(FILES),headers=headers)
        assert stale.status_code==409 and catalog.status(PKG)['state']=='needs-input'
        extension['versionName']='1.4.23'
        good=await client.post('/api/admin/source-maintenance/raws/'+PKG+'/project',content=archive(FILES),headers=headers)
        assert good.status_code==200 and good.json()['visibility']=='private'
        duplicate=await client.post('/api/admin/source-maintenance/raws/'+PKG+'/project',content=archive(FILES),headers=headers)
        assert duplicate.status_code==409
        code=await client.get('/api/admin/source-maintenance/raws/'+PKG+'/code')
        assert code.status_code==200 and code.headers['content-type']=='application/zip'
