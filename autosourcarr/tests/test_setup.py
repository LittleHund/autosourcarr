import base64
import json
import uuid
from pathlib import Path
from types import SimpleNamespace
import pytest
from app import setup_store as store
from app.config import Settings
from app.setup_schema import BY_KEY

@pytest.fixture
def configured(tmp_path):
    settings = Settings(settings_config_dir=tmp_path, main_suwayomi_url='http://running:30058', autosourcarr_internal_url='http://private-autosourcarr:8282')
    return tmp_path, settings

def save(root, settings, changes=None):
    return store.finish(root, settings, changes or {}, store.snapshot(root, settings)['revision'], str(uuid.uuid4()))

def test_initial_snapshot_does_not_write_and_hides_secret(configured):
    root, settings = configured
    settings.autosourcarr_reader_api_token = 'secret-'*8
    data = store.snapshot(root, settings)
    assert not data['completed'] and list(root.iterdir()) == []
    assert settings.autosourcarr_reader_api_token not in json.dumps(data)
    assert next(f for f in data['fields'] if f['kind']=='secret')['configured']

def test_finish_defaults_bootstraps_and_pins_effective_values(configured):
    root, settings = configured
    result = save(root, settings)
    assert result['completed'] and result['restart_required']
    assert {p.name for p in root.iterdir()} == set(store.FILES)
    assert store.env_at(root,'autosourcarr.env')['AUTOSOURCARR_INTERNAL_URL']=='http://private-autosourcarr:8282'
    assert 'keiyoushi' in store.env_at(root, 'source-host.env')['EXTENSION_STORES']
    assert store.startup_overrides(root)['main_suwayomi_url']=='http://running:30058'
    assert (root/'autosourcarr.env').stat().st_mode & 0o777 == 0o600

def test_only_changed_values_overwrite_and_normal_editor_works(configured):
    from app.env_settings import settings_payload, update_env
    root, settings=configured
    (root/'autosourcarr.env').write_text('# retain\nADMIN_TOKEN=keep-me\nMAIN_SUWAYOMI_URL=http://running:30058\nBROWSE_PAGE_SIZE=50\n')
    (root/'source-host.env').write_text('EXTENSION_STORES=[]\nCUSTOM=retained\n')
    source=(root/'source-host.env').read_bytes()
    save(root,settings,{'MAIN_SUWAYOMI_URL':'http://new:30058','BROWSE_PAGE_SIZE':77,'CONTINUOUS_CATALOG_DISCOVERY':False})
    assert (root/'source-host.env').read_bytes()==source
    assert '# retain\nADMIN_TOKEN=keep-me' in (root/'autosourcarr.env').read_text()
    payload=settings_payload(root)
    items={item['key']:item for f in payload['files'] if f['id']=='autosourcarr' for group in f['groups'] for item in group['items']}
    assert items['BROWSE_PAGE_SIZE']['value']=='77'
    assert items['CONTINUOUS_CATALOG_DISCOVERY']['value']=='false'
    update_env(root/'autosourcarr.env',{'BROWSE_PAGE_SIZE':'31'})
    assert store.startup_overrides(root)['browse_page_size']=='31'
    assert store.startup_overrides(root)['main_suwayomi_url']=='http://new:30058'

def test_stale_revision_rejected_before_any_write(configured):
    root, settings=configured
    snapshot=store.snapshot(root,settings)
    (root/'source-host.env').write_text('EXTENSION_STORES=[]\n')
    before={p.name:p.read_bytes() for p in root.iterdir()}
    with pytest.raises(store.SetupConflict): store.finish(root,settings,{'BROWSE_PAGE_SIZE':42},snapshot['revision'],str(uuid.uuid4()))
    assert before=={p.name:p.read_bytes() for p in root.iterdir()}

def test_finish_retry_is_idempotent(configured):
    root,settings=configured
    revision=store.snapshot(root,settings)['revision'];tx=str(uuid.uuid4())
    first=store.finish(root,settings,{'BROWSE_PAGE_SIZE':42},revision,tx)
    second=store.finish(root,settings,{'BROWSE_PAGE_SIZE':99},revision,tx)
    assert second==first and store.startup_overrides(root)['browse_page_size']=='42'

def test_partial_write_rollback_survives_two_starts(configured,monkeypatch):
    root,settings=configured
    (root/'autosourcarr.env').write_text('MAIN_SUWAYOMI_URL=http://old-file:30058\n')
    real=store.atomic_write;fail=[True]
    def faulty(path,data):
        if path.name=='source-host.env' and fail[0]:
            fail[0]=False;raise OSError('injected disk failure')
        real(path,data)
    monkeypatch.setattr(store,'atomic_write',faulty)
    with pytest.raises(OSError): save(root,settings,{'MAIN_SUWAYOMI_URL':'http://new:30058'})
    for _ in range(2): assert store.startup_overrides(root)['main_suwayomi_url']=='http://running:30058'
    assert not store.state_at(root).get('completed')

def test_corrupt_journal_does_not_partially_restore(configured):
    root,settings=configured
    (root/'autosourcarr.env').write_text('UNCHANGED=1\n')
    journal={'transaction':str(uuid.uuid4()),'before':{'autosourcarr.env':base64.b64encode(b'CHANGED=1').decode(),'source-host.env':'%%%','setup-state.json':None}}
    (root/store.JOURNAL).write_text(json.dumps(journal))
    with pytest.raises(ValueError):store.recover(root)
    assert (root/'autosourcarr.env').read_text()=='UNCHANGED=1\n'

def test_large_combined_journal(configured):
    root,settings=configured
    for name in store.FILES[:2]:(root/name).write_text('#'+'x'*1000000+'\n')
    save(root,settings,{'BROWSE_PAGE_SIZE':42})
    assert not (root/store.JOURNAL).exists()
    assert store.startup_overrides(root)['browse_page_size']=='42'

@pytest.mark.parametrize('key,value',[
 ('BROWSE_PAGE_SIZE',float('nan')),('BROWSE_PAGE_SIZE',1.5),('MAX_SOURCE_CONCURRENCY',50),
 ('MAIN_SUWAYOMI_URL','http://user:pass@host'),('MAIN_SUWAYOMI_URL','file:///tmp/no'),
 ('TZ','invalid-zone'),('SOURCE_LANGUAGES','../../bad'),('SOURCE_LANGUAGES','en\nADMIN_TOKEN=oops'),
 ('AUTOSOURCARR_READER_API_TOKEN','a'*257),('AUTO_MATCH_THRESHOLD',.8),('EXTENSION_STORES',['http://insecure/index.pb']),
 ('DATA_DIR','/replace-data'),('SOURCE_ENGINE_MODE','external')])
def test_invalid_or_protected_keys_do_not_write(configured,key,value):
    root,settings=configured
    with pytest.raises(ValueError):save(root,settings,{key:value})
    assert list(root.iterdir())==[]

def test_reader_token_builder_compatibility():
    assert store.normalize(BY_KEY['AUTOSOURCARR_READER_API_TOKEN'],'A._~-'*50)== 'A._~-'*50

def test_symlink_config_refused_without_target_write(configured,tmp_path):
    root,settings=configured
    target=root/'retained';target.write_text('keep')
    (root/'autosourcarr.env').symlink_to(target)
    with pytest.raises(ValueError):store.snapshot(root,settings)
    assert target.read_text()=='keep'

def test_cleanup_is_bounded_and_preserves_unknown_files(tmp_path):
    from app.setup_cleanup import scan
    cache=tmp_path/'app/__pycache__';cache.mkdir(parents=True)
    (cache/'removed.cpython-311.pyc').write_bytes(b'old')
    (cache/'current.cpython-311.pyc').write_bytes(b'keep')
    (cache.parent/'current.py').write_text('')
    unknown=tmp_path/'plugins/__pycache__';unknown.mkdir(parents=True)
    (unknown/'private.cpython-311.pyc').write_bytes(b'keep')
    for name in ('catalog.sqlite','source.apk','backup.zip','USER-NOTES.md'):(tmp_path/name).write_text('keep')
    (tmp_path/'UPGRADE-0.2.0.md').write_text('# AutoSourcarr 0.2.0 upgrade\nGenerated instructions')
    (tmp_path/'UPGRADE-0.2.1.md').write_text('# My personal notes\n')
    (tmp_path/'link').symlink_to(unknown,target_is_directory=True)
    report=scan(tmp_path,'0.3.18')
    assert set(report['removed'])=={'app/__pycache__/removed.cpython-311.pyc','UPGRADE-0.2.0.md'}
    assert (unknown/'private.cpython-311.pyc').exists() and (tmp_path/'UPGRADE-0.2.1.md').exists()
    assert scan(tmp_path,'0.3.18',max_files=1)['limited']

def test_setup_api_guard_origin_and_no_secret_response(configured):
    from app.setup_api import router
    from fastapi import FastAPI,HTTPException,Header
    from fastapi.testclient import TestClient
    root,settings=configured
    def guard(x_admin_token: str = Header(default='')):
        if x_admin_token!='test':raise HTTPException(401)
    app=FastAPI();app.include_router(router(settings,guard,'0.3.18'))
    with TestClient(app) as client:
        assert client.get('/api/admin/setup').status_code==401
        data=client.get('/api/admin/setup',headers={'X-Admin-Token':'test'}).json()
        payload={'revision':data['revision'],'transaction':str(uuid.uuid4()),'changes':{'BROWSE_PAGE_SIZE':42}}
        assert client.post('/api/admin/setup/finish',headers={'X-Admin-Token':'test','Origin':'https://evil.invalid'},json=payload).status_code==403
        assert not (root/store.STATE).exists()
        response=client.post('/api/admin/setup/finish',headers={'X-Admin-Token':'test','Origin':'http://testserver'},json=payload)
        assert response.status_code==200 and response.headers['cache-control']=='no-store'
        assert 'fields' not in response.json()
        assert client.get('/api/admin/setup',headers={'X-Admin-Token':'test'}).json()['completed']


def test_restart_tracks_repository_changes_and_case_sensitive_secrets(configured):
    root,settings=configured
    (root/'autosourcarr.env').write_text('')
    (root/'source-host.env').write_text('EXTENSION_STORES=[]\n')
    store.startup_overrides(root)
    save(root,settings,{'EXTENSION_STORES':['https://github.com/keiyoushi/extensions/raw/repo/index.pb']})
    assert store.snapshot(root,settings)['restart_required']
    assert save(root,settings)['restart_required']
    store.startup_overrides(root)  # New process/engine start applies the canonical repositories.
    assert not store.snapshot(root,settings)['restart_required']
    settings.autosourcarr_reader_api_token='a'*32
    save(root,settings,{'AUTOSOURCARR_READER_API_TOKEN':'A'*32})
    assert store.snapshot(root,settings)['restart_required']


def test_incomplete_existing_files_are_completed_without_reset(configured):
    root,settings=configured
    (root/'autosourcarr.env').write_text('# keep this comment\n')
    (root/'source-host.env').write_text('KCEF_ENABLED=true\n')
    save(root,settings)
    assert 'MAIN_SUWAYOMI_URL' in store.env_at(root,'autosourcarr.env')
    assert (root/'autosourcarr.env').read_text().startswith('# keep this comment\n')
    assert store.env_at(root,'source-host.env')['KCEF_ENABLED']=='true'
    assert 'keiyoushi' in store.env_at(root,'source-host.env')['EXTENSION_STORES']

def test_restart_schedules_normal_shutdown_once(configured,monkeypatch):
    import asyncio
    import os
    from app.setup_api import router
    from starlette.requests import Request
    root,settings=configured
    settings.app_services_mode='integrated'
    save(root,settings)
    original=Path.is_file
    monkeypatch.setattr(Path,'is_file',lambda p: True if str(p)=='/opt/autosourcarr-image-version' else original(p))
    routes=router(settings,lambda:None,'0.3.18')
    endpoint=next(route.endpoint for route in routes.routes if route.path.endswith('/restart'))
    async def run():
        scheduled=[]
        loop=asyncio.get_running_loop();real=loop.call_later
        def later(delay,callback,*args,**kwargs):
            if callback is os.kill: scheduled.append((delay,args));return None
            return real(delay,callback,*args,**kwargs)
        monkeypatch.setattr(loop,'call_later',later)
        request=Request({'type':'http','method':'POST','scheme':'http','path':'/','headers':[(b'host',b'localhost'),(b'origin',b'http://localhost')],'server':('localhost',80)})
        assert (await endpoint(request)).status_code==200
        assert (await endpoint(request)).status_code==200
        assert len(scheduled)==1 and scheduled[0][0]>=1 and scheduled[0][1][0]==os.getpid()
    asyncio.run(run())
