"""Independent failure/recovery fixtures for the optional private source worker."""
import asyncio
import base64
import importlib.util
import io
import json
from pathlib import Path
import socket
import sys
import time
from unittest.mock import AsyncMock
import zipfile

import httpx
from PIL import Image
import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'repair-worker'))
import contracts
import egress
import runner as worker_runner
import validate as worker_validate
from app import source_maintenance as maintenance
from app.config import settings


def jar():
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        archive.writestr('AndroidManifest.xml', '<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="eu.kanade.tachiyomi.extension.en.test" android:versionName="1.4.20"><application><meta-data android:name="tachiyomi.extension.class" android:value=".Test"/></application></manifest>')
        archive.writestr('Source.class', b'compiled-test-fixture'*20)
    return stream.getvalue()


def png():
    output = io.BytesIO()
    Image.new('RGB', (12, 24), 'green').save(output, format='PNG')
    return output.getvalue()


def test_agent_cannot_hide_changes_by_manipulating_git(tmp_path):
    module = tmp_path/'src/en/example/src'
    module.mkdir(parents=True)
    (module/'Source.kt').write_text('before')
    (tmp_path/'build.gradle.kts').write_text('trusted')
    before = contracts.tree_manifest(tmp_path)
    (module/'Source.kt').write_text('after')
    (tmp_path/'.git').mkdir()
    (tmp_path/'.git/index').write_text('agent-controlled')
    assert contracts.approved_changes(before, contracts.tree_manifest(tmp_path), 'src/en/example') == ['src/en/example/src/Source.kt']
    (tmp_path/'build.gradle.kts').write_text('unapproved task')
    with pytest.raises(ValueError, match='outside'):
        contracts.approved_changes(before, contracts.tree_manifest(tmp_path), 'src/en/example')


def test_repair_rejects_symlinks_deletions_and_package_disguises(tmp_path):
    (tmp_path/'link.kt').symlink_to('/etc/passwd')
    with pytest.raises(ValueError, match='symbolic'):
        contracts.tree_manifest(tmp_path)
    with pytest.raises(ValueError, match='removed'):
        contracts.approved_changes({'src/en/a/src/A.kt':['a',420]}, {}, 'src/en/a')
    with pytest.raises(ValueError, match='added'):
        contracts.approved_changes({'src/en/a/src/A.kt':['a',420]},
                                  {'src/en/a/src/A.kt':['a',420], 'src/en/a/src/Extra.kt':['b',420]}, 'src/en/a')
    with pytest.raises(ValueError):
        contracts.request_contract({'pkg_name': 'evil;command', 'module': '../../root', 'issue':'x'*20})
    with pytest.raises((ValueError, zipfile.BadZipFile)):
        contracts.package_bytes(b'<html>login</html>'*100)
    assert contracts.package_bytes(jar()) == jar()


def test_loading_manifest_identity_allows_version_changes_only():
    def rewrite(old,new):
        out=io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(jar())) as src, zipfile.ZipFile(out,'w') as dst:
            for item in src.infolist():
                data=src.read(item)
                dst.writestr(item,data.replace(old,new) if item.filename=='AndroidManifest.xml' else data)
        return out.getvalue()
    identity=contracts.manifest_contract(jar())
    assert identity==('eu.kanade.tachiyomi.extension.en.test','eu.kanade.tachiyomi.extension.en.test.Test','1.4')
    assert contracts.manifest_contract(rewrite(b'1.4.20',b'1.4.21'))==identity
    assert contracts.manifest_contract(rewrite(b'.Test',b'.Different'))!=identity
    assert contracts.manifest_contract(rewrite(b'1.4.20',b'1.5.20'))!=identity


@pytest.mark.parametrize('text,secret', [
    ('{"id_token":"eyJhbGciOi.fake.signature"}', 'eyJhbGciOi.fake.signature'),
    ('https://user:pass@example.org/?token=raw-secret', 'raw-secret'),
    ('{"access_token":"test-token", "refresh_token":"secret-two"}', 'test-token'),
])
def test_worker_public_messages_redact_credentials(text, secret):
    result = contracts.safe_text(text)
    assert secret not in result and 'user:pass' not in result


@pytest.mark.parametrize('ip', ['127.0.0.1', '10.0.0.7', '172.16.1.2', '192.168.1.5', '169.254.169.254', '::1', '::ffff:192.168.0.1', '224.0.0.1'])
def test_egress_refuses_host_lan_metadata_and_multicast(monkeypatch, ip):
    monkeypatch.setattr(socket, 'getaddrinfo', lambda *a,**k:[(2,1,6,'',(ip,443))])
    with pytest.raises(ValueError): egress.public_address('malicious.example',443)


def test_egress_pins_public_dns_and_rejects_mixed_answers(monkeypatch):
    monkeypatch.setattr(socket, 'getaddrinfo', lambda *a,**k:[(2,1,6,'',('1.1.1.1',443))])
    assert egress.public_address('example.org',443) == '1.1.1.1'
    assert egress.destination('example.org:443',True) == ('example.org',443,'/')
    with pytest.raises(ValueError): egress.destination('example.org:8282',True)
    with pytest.raises(ValueError): egress.destination('http://user:pass@example.org/')
    monkeypatch.setattr(socket, 'getaddrinfo', lambda *a,**k:[(2,1,6,'',('1.1.1.1',443)),(2,1,6,'',('10.0.0.1',443))])
    with pytest.raises(ValueError): egress.public_address('mixed.example',443)


def test_compose_isolates_untrusted_jobs_from_app_and_login():
    compose = yaml.safe_load((ROOT/'docker-compose.repair.yml').read_text())
    assert compose['networks']['repair-jobs']['internal'] is True
    assert 'repair-jobs' not in compose['services']['autosourcarr']['networks']
    assert 'repair-jobs' not in compose['services']['repair-login']['networks']
    assert not compose['services']['autosourcarr-repair-egress'].get('volumes')
    assert not compose['services']['autosourcarr-repair'].get('ports')
    r = object.__new__(worker_runner.Runner);r.network='private-jobs';r.namespace='test'
    args = r.base_args('test')
    assert args[args.index('--network')+1] == 'private-jobs'
    assert '--privileged' not in args and not any('docker.sock' in x for x in args)


def validator_fixture(*, wrong_search=False, partial_error=False, broken_image=False, captcha=False):
    calls=[]
    def send(request):
        calls.append((request.method,request.url.path))
        if request.method == 'GET':
            return httpx.Response(200, content=b'<html>Challenge</html>' if broken_image else png())
        body=json.loads(request.content);query=body['query'];values=body['variables'].get('input',{})
        if captcha: return httpx.Response(200,json={'errors':[{'message':'Shape-selecting captcha detected'}]})
        if partial_error: return httpx.Response(200,json={'data':{'fetchSourceManga':{'mangas':[{'id':1}]}},'errors':[{'message':'parser failed'}]})
        if 'fetchSourceManga' in query:
            return httpx.Response(200,json={'data':{'fetchSourceManga':{'mangas':[{'id':999 if wrong_search and values['type']=='SEARCH' else 7,'title':'Example','sourceId':'22','url':'/example'}]}}})
        if 'fetchMangaAndChapters' in query:
            return httpx.Response(200,json={'data':{'fetchMangaAndChapters':{'manga':{'id':7,'title':'Example','url':'/example','description':'Public metadata','thumbnailUrl':'/thumb'},'chapters':[{'id':10,'name':'Recent','url':'/recent'},{'id':11,'name':'Old','url':'/old'}]}}})
        if 'fetchChapterPages' in query:
            index=0 if values['chapterId']==10 else 1
            return httpx.Response(200,json={'data':{'fetchChapterPages':{'pages':[f'/api/v1/manga/7/chapter/{index}/page/{n}' for n in range(5)]}}})
        raise AssertionError(query)
    return worker_validate.Validator('http://test',client=httpx.Client(base_url='http://test',transport=httpx.MockTransport(send)),pace=0),calls


def test_functional_validation_retrieves_both_chapters_and_real_images():
    validator,calls=validator_fixture()
    result=validator.check([{'id':'22','name':'Example','lang':'en','supportsLatest':True}])
    assert result[0]['works'][0]['chapter_count'] == 2
    assert [c['chapter'] for c in result[0]['works'][0]['chapters']] == ['Recent','Old']
    images=[path for method,path in calls if '/page/' in path]
    assert len(images)==6
    assert '/api/v1/manga/7/chapter/1/page/4' in images
    validator.close()


@pytest.mark.parametrize('options,error', [({'wrong_search':True},ValueError),({'partial_error':True},RuntimeError),({'broken_image':True},OSError),({'captcha':True},worker_validate.SourceBlocked)])
def test_incomplete_source_evidence_cannot_pass(options,error):
    validator,_=validator_fixture(**options)
    with pytest.raises(error):validator.check([{'id':'22','name':'Example','lang':'en'}])
    validator.close()


def test_validator_fully_decodes_images():
    image=Image.effect_noise((200,200),100).convert('RGB');out=io.BytesIO();image.save(out,format='JPEG')
    data=out.getvalue()[:-100]
    with Image.open(io.BytesIO(data)) as partial: partial.verify()
    with pytest.raises(OSError):worker_validate.image_check(data)


def test_captcha_halts_before_checkout_build_or_codex(tmp_path,monkeypatch):
    monkeypatch.setattr(worker_runner.os,'chown',lambda *a:None)
    r=worker_runner.Runner(tmp_path,tmp_path,'jobs')
    job={'id':'a'*32,'pkg_name':'eu.kanade.tachiyomi.extension.en.test','module':'src/en/test','created_at':time.time()}
    path=r.path(job['id']);path.mkdir();(path/'baseline.jar').write_bytes(jar())
    def blocked(*a,**k):raise worker_validate.SourceBlocked('Source needs human verification')
    monkeypatch.setattr(r,'inspect_artifact',blocked)
    monkeypatch.setattr(r,'checkout',lambda *a:pytest.fail('must not ask AI to bypass CAPTCHA'))
    r.run(job)
    assert job['state']=='blocked' and not (path/'candidate.jar').exists()


@pytest.fixture
def live_files(tmp_path,monkeypatch):
    from app import source_engine
    monkeypatch.setattr(settings,'data_dir',tmp_path/'data')
    monkeypatch.setattr(source_engine,'ENGINE_DATA',tmp_path/'source')
    directory=tmp_path/'source/extensions';directory.mkdir(parents=True)
    target=directory/'test.jar';target.write_bytes(b'old-binary')
    extension={'pkgName':'eu.kanade.tachiyomi.extension.en.test','apkName':'test.apk','isInstalled':True,'source':{'nodes':[{'id':'22'}]}}
    maintenance.save_backup(extension)
    return target,extension


def test_crash_recovery_restores_binary_without_touching_database(live_files):
    target,extension=live_files
    db=target.parent.parent/'database.mv.db';db.write_bytes(b'keep-database')
    target.write_bytes(b'candidate')
    record={'state':'installing','extension':extension,'baseline_sha256':maintenance.sha(b'old-binary'),'candidate_sha256':maintenance.sha(b'candidate')}
    maintenance.atomic(maintenance.storage()/'promotion.json',json.dumps(record).encode())
    manager=maintenance.SourceMaintenance();manager.recover()
    assert target.read_bytes()==b'old-binary' and db.read_bytes()==b'keep-database'
    assert manager.operation['state']=='rolled-back'


def test_crash_recovery_will_not_overwrite_a_newer_package(live_files):
    target,extension=live_files
    target.write_bytes(b'independent-update')
    record={'state':'installing','extension':extension,'baseline_sha256':maintenance.sha(b'old-binary'),'candidate_sha256':maintenance.sha(b'candidate')}
    maintenance.atomic(maintenance.storage()/'promotion.json',json.dumps(record).encode())
    with pytest.raises(ValueError,match='changed'):maintenance.SourceMaintenance().recover()
    assert target.read_bytes()==b'independent-update'


@pytest.mark.asyncio
async def test_changed_live_source_blocks_candidate_before_engine_stop(live_files,monkeypatch):
    from app.source_engine import source_engine
    from app.source_browser import source_browser
    from app.service import service
    target,extension=live_files
    job={'state':'ready','checks':[{}],'expected_ids':['22'],'validation_time':time.time(),'pkg_name':extension['pkgName'],'candidate_sha256':maintenance.sha(b'candidate'),'baseline_sha256':'0'*64}
    async def remote(method,path,**kw):return b'candidate' if path.endswith('/artifact') else job
    monkeypatch.setattr(maintenance,'worker',remote)
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(return_value=[extension]))
    stop=AsyncMock();monkeypatch.setattr(source_browser,'_stop_engine',stop)
    monkeypatch.setattr(source_engine,'state','ready')
    monkeypatch.setattr(service,'schedule_source_reconciliation',lambda **k:None)
    manager=maintenance.SourceMaintenance();await manager.install('a'*32,asyncio.Lock())
    assert manager.operation['state']=='failed' and 'updated' in manager.operation['message']
    assert target.read_bytes()==b'old-binary';stop.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['none','identity','cancel-during-write'])
async def test_promotion_commit_and_rollback_own_the_binary(live_files,monkeypatch,failure):
    from app.source_engine import source_engine
    from app.source_browser import source_browser
    from app.service import service
    from app import suwayomi
    target,extension=live_files
    job={'state':'ready','checks':[{}],'expected_ids':['22'],'validation_time':time.time(),'pkg_name':extension['pkgName'],'candidate_sha256':maintenance.sha(b'candidate'),'baseline_sha256':maintenance.sha(b'old-binary')}
    async def remote(method,path,**kw):return b'candidate' if path.endswith('/artifact') else job
    monkeypatch.setattr(maintenance,'worker',remote)
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(return_value=[extension]))
    async def stop(*args):source_engine.state='stopped'
    monkeypatch.setattr(source_browser,'_stop_engine',stop)
    monkeypatch.setattr(source_browser,'_assert_runtime_absent',AsyncMock())
    monkeypatch.setattr(source_engine,'start',lambda:setattr(source_engine,'state','ready'))
    monkeypatch.setattr(source_engine,'state','ready')
    monkeypatch.setattr(service,'schedule_source_reconciliation',lambda **k:None)
    class Client:
        def __init__(self,*args):pass
        async def graphql(self,*args):
            other={**extension,'source':{'nodes':[{'id':'different'}]}} if failure=='identity' else extension
            return {'extensions':{'nodes':[other]}}
        async def close(self):pass
    monkeypatch.setattr(suwayomi,'SuwayomiClient',Client)
    if failure=='cancel-during-write':
        actual=maintenance.run_blocking
        async def cancel(function,*args,**kwargs):
            result=await actual(function,*args,**kwargs)
            if function is maintenance.atomic and args[0]==target and args[1]==b'candidate':
                raise asyncio.CancelledError()
            return result
        monkeypatch.setattr(maintenance,'run_blocking',cancel)
    manager=maintenance.SourceMaintenance()
    if failure=='cancel-during-write':
        with pytest.raises(asyncio.CancelledError):await manager.install('a'*32,asyncio.Lock())
    else:await manager.install('a'*32,asyncio.Lock())
    assert target.read_bytes()==(b'candidate' if failure=='none' else b'old-binary')
    assert manager.operation['state']==('installed' if failure=='none' else 'rolled-back')
    assert source_browser.busy is False


@pytest.mark.asyncio
async def test_daily_monitor_reserves_window_and_limits_repeated_source_jobs(live_files,monkeypatch):
    from app.source_engine import source_engine
    from app.suwayomi import source_host
    target,extension=live_files
    monkeypatch.setattr(settings,'repair_worker_url','http://worker.invalid')
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(settings,'settings_config_dir',target.parent.parent/'config')
    monkeypatch.setattr(source_engine,'state','ready')
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(return_value=[extension]))
    monkeypatch.setattr(source_host,'extensions',AsyncMock(return_value={'extensions':[{**extension,'isObsolete':True}]}))
    submitted=[]
    async def remote(method,path,**kwargs):
        if method=='POST':submitted.append(kwargs['payload']);return {}
        return {'active':None,'jobs':[]}
    monkeypatch.setattr(maintenance,'worker',remote)
    manager=maintenance.SourceMaintenance();manager.watch_state['enabled']=True
    await manager.watch_once();await manager.watch_once()
    assert len(submitted)==1 and manager.watch_state['next_check']>time.time()+86000
    manager.watch_state['next_check']=0
    await manager.watch_once()
    assert len(submitted)==1  # same source cannot consume an attempt again within a week


def test_truenas_transform_preserves_services_mounts_and_env(tmp_path):
    spec=importlib.util.spec_from_file_location('worker_setup',ROOT/'scripts/configure-repair-worker.py')
    setup=importlib.util.module_from_spec(spec);spec.loader.exec_module(setup)
    old={'services':{'autosourcarr':{'volumes':['/data/app:/data'],'environment':['KEEP=value'],'ports':['1234:8282']},'suwayomi':{'image':'existing'}},'networks':{}}
    values={'REPAIR_ROOT':'/separate/repair','REPAIR_NETWORK':'private-repair','REPAIR_TOKEN':'a'*64}
    result=setup.transform(old,tmp_path,values)
    assert result['services']['suwayomi']==old['services']['suwayomi']
    assert result['services']['autosourcarr']['volumes']==['/data/app:/data']
    assert result['services']['autosourcarr']['ports']==['1234:8282']
    assert result['services']['autosourcarr']['environment']['KEEP']=='value'
    assert old['services']['autosourcarr']['environment']==['KEEP=value']
    with pytest.raises(ValueError,match='overlaps'):
        setup.transform(old,tmp_path,{**values,'REPAIR_ROOT':'/data/app/repair'})


@pytest.mark.parametrize('selected', [['autosourcarr'], ['private-autosourcarr'], ['autosourcarr','private-autosourcarr']])
def test_truenas_repair_setup_connects_only_selected_instances(tmp_path, selected):
    spec=importlib.util.spec_from_file_location('worker_setup_pair',ROOT/'scripts/configure-repair-worker.py')
    setup=importlib.util.module_from_spec(spec);spec.loader.exec_module(setup)
    original={'services': {
        'autosourcarr': {'ports':['30060:8282'], 'volumes':['/data/normal:/data'], 'environment':['KEEP=normal']},
        'private-autosourcarr': {'ports':['30061:8282'], 'volumes':['/data/private:/data'], 'environment':{'KEEP':'private'}, 'networks':{'media':{'aliases':['private-source']}}},
        'private-suwayomi': {'image':'existing', 'ports':['30062:30058']},
    }, 'networks': {'media': {'external':True}}}
    values={'REPAIR_ROOT':'/separate/repair','REPAIR_NETWORK':'private-repair','REPAIR_TOKEN':'a'*64}
    result=setup.transform(original,tmp_path,values,selected)
    for name in ('autosourcarr','private-autosourcarr'):
        if name not in selected:
            assert result['services'][name] == original['services'][name]
            continue
        current=result['services'][name]
        assert current['ports']==original['services'][name]['ports']
        assert current['volumes']==original['services'][name]['volumes']
        assert current['environment']['REPAIR_WORKER_URL']=='http://autosourcarr-repair:8766'
        assert current['environment']['REPAIR_WORKER_TOKEN']==values['REPAIR_TOKEN']
        assert 'repair' in current['networks'] and 'repair-jobs' not in current['networks']
    assert result['services']['private-suwayomi']==original['services']['private-suwayomi']
    assert result['networks']['media']==original['networks']['media']
    if 'private-autosourcarr' in selected:
        assert result['services']['private-autosourcarr']['networks']['media']=={'aliases':['private-source']}


@pytest.mark.parametrize('name', ['missing-autosourcarr','suwayomi'])
def test_truenas_repair_setup_rejects_missing_or_unrelated_service(tmp_path, name):
    spec=importlib.util.spec_from_file_location('worker_setup_reject',ROOT/'scripts/configure-repair-worker.py')
    setup=importlib.util.module_from_spec(spec);spec.loader.exec_module(setup)
    values={'REPAIR_ROOT':'/separate/repair','REPAIR_NETWORK':'private-repair','REPAIR_TOKEN':'a'*64}
    with pytest.raises(ValueError,match='service names'):
        setup.transform({'services':{'autosourcarr':{},'suwayomi':{}}},tmp_path,values,[name])
