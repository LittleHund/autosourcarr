import asyncio
import base64
from contextlib import asynccontextmanager
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastapi import FastAPI,Header,HTTPException
import httpx
import pytest
from test_v087_recovery import env,source,entry
from app.config import settings
from app.library_manager import LibraryManager
from app.reimagined_api import router


@pytest.fixture
def api(env,tmp_path,monkeypatch):
    service,database=env
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path/'config')
    settings.settings_config_dir.mkdir()
    monkeypatch.setattr(service,'schedule_status_aux_refresh',lambda **kwargs:None)
    source(database,'one');eid=entry(service,'one',4646,'Full Library Fixture')
    monkeypatch.setattr(service,'cached_subbed_payload',lambda:{'ok':True,'entry_ids':[eid]})
    manager=LibraryManager(database,service,None)
    async def guard(x_admin_token:str=Header('')):
        if x_admin_token!='fixture-secret':raise HTTPException(401,'Admin token required')
    paused=[];restarts=[]
    @asynccontextmanager
    async def quiesce():
        paused.append('enter')
        try:yield
        finally:paused.append('leave')
    app=FastAPI();app.include_router(router(database,service,manager,settings,guard,asyncio.Lock(),quiesce,lambda:restarts.append('requested')))
    return app,manager,eid,paused,restarts


@pytest.mark.asyncio
async def test_new_ui_routes_keep_admin_authentication(api):
    app,*_=api
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as c:
        for path in ['/api/admin/library','/api/admin/downloads','/api/admin/instance/backups','/api/admin/review-workbench',
                     '/api/admin/research-improvements/status','/api/admin/policy-simulator/options',
                     '/api/admin/metadata-fields/1','/api/admin/serving-upgrades/1']:
            assert (await c.get(path)).status_code==401
        assert (await c.post('/api/admin/instance/restore/apply',json={'id':'f'*32,'revision':'x','confirmation':'RESTORE THIS INSTANCE'})).status_code==401


@pytest.mark.asyncio
async def test_library_profile_and_missing_observations_use_real_router(api):
    app,manager,eid,*_=api
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test',headers={'X-Admin-Token':'fixture-secret'}) as c:
        result=(await c.get('/api/admin/library')).json()
        assert result['total']==1 and result['entries'][0]['id']==eid
        assert (await c.get('/api/admin/library?scope=missing_downloads')).json()['total']==0
        good=await c.put('/api/admin/profiles/'+str(eid),json={'revision':0,'profile':{'languages':['en']}})
        assert good.status_code==200 and good.json()['revision']==1
        assert (await c.put('/api/admin/profiles/'+str(eid),json={'revision':0,'profile':{}})).status_code==409
        assert (await c.get('/api/admin/review-workbench?entry_id='+str(eid))).json()['entry']['id']==eid


@pytest.mark.asyncio
async def test_library_projection_keeps_an_exact_cached_alias_visible_after_merge(api,monkeypatch):
    app,manager,eid,*_=api
    old=eid+1000
    manager.service._remember_entry_alias(old,eid,'retained test URL')
    monkeypatch.setattr(manager.service,'cached_subbed_payload',lambda:{'ok':True,'entry_ids':[old],'stats_by_entry':{str(old):{'chapter_count':12,'downloaded_chapter_count':4}}})
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test',headers={'X-Admin-Token':'fixture-secret'}) as c:
        result=(await c.get('/api/admin/library')).json()
        assert result['total']==1 and result['entries'][0]['id']==eid
        assert result['entries'][0]['library_stats']['downloaded_chapter_count']==4


@pytest.mark.asyncio
async def test_backup_preview_and_deliberate_restore_use_staging_without_live_restart(api):
    app,manager,eid,paused,restarts=api
    headers={'X-Admin-Token':'fixture-secret'}
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test',headers=headers) as c:
        password='fixture passphrase for encrypted restore'
        created=await c.post('/api/admin/instance/backups',json={'passphrase':password})
        assert created.status_code==200,created.text
        assert paused==['enter','leave']
        identifier=created.json()['id']
        download=await c.get('/api/admin/instance/backups/'+identifier+'/download')
        assert download.status_code==200 and download.headers['cache-control']=='no-store'
        preview=await c.post('/api/admin/instance/restore/preview',content=download.content,headers={'X-Backup-Passphrase':base64.b64encode(password.encode()).decode()})
        assert preview.status_code==200,preview.text
        staged=preview.json()
        rejected=await c.post('/api/admin/instance/restore/apply',json={'id':staged['id'],'revision':staged['revision'],'confirmation':'wrong'})
        assert rejected.status_code==409 and not restarts
        applied=await c.post('/api/admin/instance/restore/apply',json={'id':staged['id'],'revision':staged['revision'],'confirmation':'RESTORE THIS INSTANCE'})
        assert applied.status_code==200 and restarts==['requested']


@pytest.mark.asyncio
async def test_foreign_main_chapter_identity_cannot_become_missing_download_proof(env):
    service,database=env;source(database,'one');eid=entry(service,'one',4647)
    client=SimpleNamespace(graphql=AsyncMock(return_value={'chapters':{'nodes':[{'id':123,'mangaId':99,'url':'1','isDownloaded':False}],
                        'pageInfo':{'hasNextPage':False}}}))
    manager=LibraryManager(database,service,client)
    manager.exact_manga=AsyncMock(return_value={'id':10,'inLibrary':True})
    with pytest.raises(ValueError,match='identity'):await manager.observe(eid)
    assert manager.tracking(eid)['observation']['download_state']=='unknown'


@pytest.mark.asyncio
@pytest.mark.parametrize('method,path',[('PUT','/api/admin/profiles/1'),('POST','/api/admin/library/1/observe'),('POST','/api/admin/tasks/example/retry-safe'),
  ('PUT','/api/admin/metadata-fields/1'),('PUT','/api/admin/serving-upgrades/1'),('POST','/api/admin/serving-upgrades/1/preview'),
  ('POST','/api/admin/serving-upgrades/1/apply'),('POST','/api/admin/policy-simulator/preview'),('POST','/api/admin/policy-simulator/apply'),
  ('POST','/api/admin/engine/compatibility'),('POST','/api/admin/candidates/1/quality')])
async def test_queued_admin_operation_cannot_deadlock_backup_admission(monkeypatch,method,path):
    from app import main
    from starlette.requests import Request
    from starlette.responses import Response
    monkeypatch.setattr(main,'_catalog_reset_active',False)
    monkeypatch.setattr(main,'_catalog_http_requests',0)
    lock=asyncio.Lock();entered=asyncio.Event()
    async def waiting_handler(request):
        entered.set()
        async with lock:return Response('done')
    request=Request({'type':'http','method':method,'path':path,'headers':[]})
    async with lock:
        queued=asyncio.create_task(main.catalogue_reset_admission(request,waiting_handler))
        await entered.wait()
        async with main._exclusive_catalog_reset(timeout=.05):
            assert main._catalog_http_requests==0
    assert (await queued).status_code==200


@pytest.mark.asyncio
@pytest.mark.parametrize('busy_field',['pending_jobs','account_busy','cleanup_uncertain'])
async def test_actual_backup_pause_does_not_stop_busy_repair_or_account_work(monkeypatch,busy_field):
    from app import main,internal_services
    import importlib
    maintenance=importlib.import_module('app.source_maintenance')
    idle={'active':None,'pending_jobs':False,'account_busy':False,'cleanup_uncertain':False}
    read=AsyncMock(return_value={**idle,busy_field:True})
    monkeypatch.setattr(maintenance,'worker',read)
    owner=SimpleNamespace(cleanup_failed=set(),tasks={},processes={'repair':SimpleNamespace(returncode=None)},stop=AsyncMock())
    monkeypatch.setattr(internal_services,'internal_services',owner)
    from unittest.mock import Mock
    service=SimpleNamespace(begin_automatic_duplicate_pause=Mock(return_value='backup fixture'),end_automatic_duplicate_pause=Mock())
    monkeypatch.setattr(main,'service',service)
    monkeypatch.setattr(main,'task_registry',SimpleNamespace(snapshot=lambda:{'items':[]}))
    monkeypatch.setattr(main,'_database_admin_lock',asyncio.Lock())
    monkeypatch.setattr(main,'_catalog_http_requests',0)
    monkeypatch.setattr(main,'_catalog_reset_active',False)
    with pytest.raises(HTTPException) as result:
        async with main._instance_snapshot_pause():pytest.fail('Busy repair/account work cannot be paused for backup')
    assert result.value.status_code==409
    assert 'no tools were stopped' in result.value.detail
    owner.stop.assert_not_awaited();read.assert_awaited_once_with('GET','/status')


@pytest.mark.asyncio
async def test_saved_improvement_status_does_not_contact_either_engine(api,monkeypatch):
    app,manager,eid,*_=api
    monkeypatch.setattr(manager.service,'schedule_status_aux_refresh',lambda **kwargs:pytest.fail('Status must not schedule probes'))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test',headers={'X-Admin-Token':'fixture-secret'}) as c:
        result=await c.get('/api/admin/research-improvements/status')
        assert result.status_code==200 and result.json()['engine']['native_qualified'] is False
        saved=await c.put('/api/admin/metadata-fields/'+str(eid),json={'revision':0,'fields':{'author':{'mode':'locked','value':'Displayed author'}}})
        assert saved.status_code==200
        assert (await c.get('/api/admin/metadata-fields/'+str(eid))).json()['fields']['author']['value']=='Displayed author'
        assert (await c.put('/api/admin/metadata-fields/'+str(eid),json={'revision':0,'fields':{}})).status_code==409
