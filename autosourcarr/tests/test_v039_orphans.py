"""Original repository archive discovery and explicit install regression cases."""
import asyncio
import io
import json
import struct
import time
import zipfile
from unittest.mock import AsyncMock

import httpx
import pytest

from app import source_orphans as module
from app.config import settings

PKG = 'eu.kanade.tachiyomi.extension.en.example'
OTHER = 'eu.kanade.tachiyomi.extension.en.other'
SHA = 'a' * 40


def varint(n):
    out = bytearray()
    while n > 127: out.append(n & 127 | 128); n >>= 7
    out.append(n)
    return bytes(out)


def f(k, v):
    if isinstance(v, int): return varint(k << 3) + varint(v)
    if isinstance(v, str): v = v.encode()
    return varint(k << 3 | 2) + varint(len(v)) + v


def index(pkg=PKG, artifact=None):
    artifact = artifact or module.REPOSITORY+'/releases/download/abc-0/example.jar'
    source = f(1, 999999999999999999) + f(2, 'Example') + f(3, 'en') + f(4, 'https://example.org')
    resources = f(501, artifact)
    extension = f(1, 'Example') + f(2, pkg) + f(3, resources) + f(6, '1.4.1') + f(8, source)
    return f(1, 'Keiyoushi') + f(101, f(1, extension))


def legacy(pkg=PKG):
    return json.dumps([{'pkg': pkg, 'name': 'Example', 'version': '1.4.1', 'lang': 'en',
        'apk': 'example.apk', 'sources': [{'id': '99', 'name': 'Example', 'lang': 'en'}]}]).encode()


def jar(pkg=PKG):
    target = io.BytesIO()
    with zipfile.ZipFile(target, 'w') as archive:
        archive.writestr('AndroidManifest.xml', '<manifest package="'+pkg+'"/>')
        archive.writestr('Example.class', b'fixture-class'*20)
    return target.getvalue()


def record():
    return module.parse_index(legacy(), SHA)[PKG]


@pytest.fixture
def manager(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    m = module.SourceOrphans()
    m.loaded_path = m.path()
    return m


def test_modern_manifest_retains_original_native_jar_digest_and_long_source_ids():
    item = module.parse_index(index(), SHA, assets={PKG: {'jar': {'name': 'example.jar', 'sha256': 'b'*64}}})[PKG]
    assert item['archive_url'].endswith('/example.jar') and item['sha256'] == 'b'*64
    assert item['sources'][0]['id'] == '999999999999999999'
    assert item['lang'] == 'en' and item['module'] == 'src/en/example'
    assert item['commit_url'] == module.REPOSITORY+'/commit/'+SHA


def test_mutable_release_without_pinned_original_digest_is_not_installable():
    assert module.parse_index(index(), SHA)[PKG]['archive_url'] == ''
    assert module.parse_index(index(), SHA, assets={PKG: {'jar': {'name': 'different.jar', 'sha256': 'b'*64}}})[PKG]['archive_url'] == ''


def test_legacy_package_is_pinned_to_original_commit():
    item = record()
    assert item['archive_url'] == module.RAW+SHA+'/apk/example.apk'


@pytest.mark.parametrize('url', [
    'https://example.org/extension.jar',
    'https://github.com/unofficial/extensions/releases/download/a/b.jar',
    'https://raw.githubusercontent.com/other/extensions/repo/apk/example.apk',
    'http://github.com/keiyoushi/extensions/releases/download/a/b.jar',
    'https://user:pass@github.com/keiyoushi/extensions/releases/download/a/b.jar',
    'https://github.com/keiyoushi/extensions/releases/download/a/b.jar?token=x',
    'https://raw.githubusercontent.com/keiyoushi/extensions/repo/apk/../../escape.apk',
])
def test_archives_never_substitute_unofficial_packages_or_paths(url):
    assert module.package_url(url, SHA) == ''


def test_upgrade_sentinel_is_not_misread_as_every_source_removed():
    sentinel = json.dumps([{'pkg':'eu.kanade.tachiyomi.extension.all.keiyoushi','name':'Outdated App'},
                           {'pkg':'eu.kanade.tachiyomi.extension.all.mihon','name':'Update to Mihon'}]).encode()
    with pytest.raises(ValueError, match='upgrade notice'): module.parse_index(sentinel, SHA)


def test_package_identity_must_match_archive_before_install():
    module.validate_package(jar(), PKG)
    with pytest.raises(ValueError, match='identity'): module.validate_package(jar(OTHER), PKG)
    with pytest.raises(zipfile.BadZipFile): module.validate_package(b'<html>login</html>'*100, PKG)


def test_binary_android_manifest_package_is_decoded():
    # Minimal Android resource XML using its UTF8 string pool and root attribute.
    values = ['manifest','package',PKG]
    strings = b''; offsets=[]
    for value in values:
        encoded=value.encode();offsets.append(len(strings));strings += bytes([len(value),len(encoded)])+encoded+b'\0'
    start=28+len(values)*4
    pool=struct.pack('<HHI',1,28,start+len(strings))+struct.pack('<IIIII',len(values),0,0x100,start,0)+b''.join(struct.pack('<I',v) for v in offsets)+strings
    attributes=struct.pack('<IIIHBBI',0xffffffff,1,2,8,0,3,2)
    extension=struct.pack('<IIHHHHHH',0xffffffff,0,20,20,1,0,0,0)+attributes
    element=struct.pack('<HHIII',0x102,16,16+len(extension),1,0xffffffff)+extension
    data=struct.pack('<HHI',3,8,8+len(pool)+len(element))+pool+element
    assert module.manifest_package(data)==PKG


@pytest.mark.asyncio
async def test_installed_and_available_do_not_duplicate_and_current_packages_disappear(manager, monkeypatch):
    manager.records={PKG:record(), OTHER:{**record(),'pkgName':OTHER,'name':'Other'}}
    manager.current={OTHER}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[{'pkgName':PKG,'isInstalled':True}]))
    await manager.refresh_inventory()
    result=await manager.snapshot()
    assert len(result['items'])==1
    assert result['items'][0]['installed'] is True
    assert result['items'][0]['state']=='installed-unverified'
    assert result['items'][0]['can_install'] is False


@pytest.mark.asyncio
async def test_inventory_failure_does_not_offer_unsafe_reinstallation(manager, monkeypatch):
    manager.records={PKG:record()}
    monkeypatch.setattr(module,'inventory',AsyncMock(side_effect=RuntimeError('offline')))
    result=await manager.snapshot()
    assert not result['inventory_known'] and not result['items'][0]['can_install']


@pytest.mark.asyncio
async def test_repair_classification_and_rollback_persist(manager, monkeypatch):
    manager.records={PKG:record()}
    await manager.record_repair({'pkgName':PKG}, 'c'*32)
    other=module.SourceOrphans();other.load()
    assert other.records[PKG]['state']=='repaired'
    await other.record_repair({'pkgName':PKG}, None)
    again=module.SourceOrphans();again.load()
    assert again.records[PKG]['state']=='installed-unverified'


@pytest.mark.asyncio
async def test_original_fetch_refuses_external_redirect():
    def respond(req): return httpx.Response(302, headers={'Location':'http://127.0.0.1/private'})
    async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as client:
        with pytest.raises(ValueError,match='Invalid original'):
            await module.fetch(client,module.RAW+SHA+'/index.pb')


@pytest.mark.asyncio
async def test_failed_discovery_preserves_good_cached_results(manager, monkeypatch):
    manager.records={PKG:record()}; manager.current={OTHER}
    monkeypatch.setattr(module,'fetch',AsyncMock(side_effect=ValueError('GitHub rate limit')))
    await manager.discover()
    assert manager.refresh['state']=='failed'
    assert PKG in manager.records and manager.current=={OTHER}


@pytest.mark.asyncio
async def test_discovery_compares_modern_current_and_keeps_newest_historical_snapshot(manager, monkeypatch):
    monkeypatch.setattr(module,'SNAPSHOT_DAYS',(0,7,30))
    commits=iter(['a'*40,'b'*40,'c'*40])
    async def response(client,url,maximum=module.MAX_INDEX,optional=False):
        if url.startswith(module.API):return json.dumps([{'sha':next(commits)}]).encode()
        if url.endswith('/release-assets.json'): return None
        if '/'+('a'*40)+'/' in url:return index(OTHER)
        return index(PKG,artifact=module.RAW+'repo/jar/old.jar')
    monkeypatch.setattr(module,'fetch',response)
    await manager.discover()
    assert manager.refresh['state']=='complete'
    assert list(manager.records)==[PKG]
    assert manager.records[PKG]['commit']=='b'*40
    assert manager.current=={OTHER}


@pytest.mark.asyncio
async def test_install_refuses_to_overwrite_existing_source(manager,monkeypatch):
    manager.records={PKG:record()}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[{'pkgName':PKG,'isInstalled':True}]))
    download=AsyncMock();monkeypatch.setattr(module,'fetch',download)
    await manager.install(PKG,asyncio.Lock())
    download.assert_not_awaited()
    assert manager.operation['state']=='failed'
    assert 'already installed' in manager.operation['message']


@pytest.mark.asyncio
async def test_install_rechecks_current_catalog_before_download(manager,monkeypatch):
    manager.records={PKG:record()};manager.operation={'pkgName':PKG}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[]))
    async def download(client,url,*args,**kwargs):
        if url.startswith(module.API):return json.dumps([{'sha':SHA}]).encode()
        if url.endswith('index.pb'):return index(PKG)
        pytest.fail('must not download archive once maintained package reappears')
    monkeypatch.setattr(module,'fetch',download)
    await manager.install(PKG,asyncio.Lock())
    assert manager.operation['state']=='failed'
    assert 'available again' in manager.operation['message']


@pytest.mark.asyncio
async def test_install_saves_original_and_confirms_live_package_identity(manager,monkeypatch):
    from app.suwayomi import source_host
    from app.service import service
    manager.records={PKG:record()};manager.operation={'pkgName':PKG}
    installed={'pkgName':PKG,'isInstalled':True,'source':{'nodes':[{'id':'99'}]}}
    monkeypatch.setattr(module,'inventory',AsyncMock(side_effect=[[],[installed]]))
    async def download(client,url,*args,**kwargs):
        if url.startswith(module.API):return json.dumps([{'sha':SHA}]).encode()
        if url.endswith('index.pb'):return index(OTHER)
        return jar()
    monkeypatch.setattr(module,'fetch',download)
    sent=AsyncMock(return_value=httpx.Response(200,request=httpx.Request('POST','http://source/api/v1/extension/install')))
    monkeypatch.setattr(source_host.client,'post',sent)
    monkeypatch.setattr(service,'schedule_source_reconciliation',lambda **k:None)
    await manager.install(PKG,asyncio.Lock())
    assert manager.operation['state']=='installed'
    assert manager.records[PKG]['state']=='installed-unverified'
    assert manager.records[PKG]['identity_matches'] is True
    assert list((settings.data_dir/'source-maintenance/archives').rglob('original.apk'))
    sent.assert_awaited_once()


@pytest.mark.asyncio
async def test_uninstalled_orphan_recovery_uses_pinned_original_identity(manager, monkeypatch):
    manager.records={PKG:{**record(),'snapshot_date':'2026-08-01T00:00:00Z'}}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[]))
    monkeypatch.setattr(manager,'repair_revision',AsyncMock(return_value='c'*40))
    remote=AsyncMock(return_value={'id':'d'*32,'state':'queued','recovery':True})
    monkeypatch.setattr(module,'worker',remote)
    result=await manager.repair(PKG,asyncio.Lock())
    assert result['recovery'] is True
    body=remote.call_args.kwargs['payload']
    assert body['revision']=='c'*40 and body['recovery'] is True
    assert body['expected_sources'][0]['id']=='99'
    assert 'baseline' not in body


@pytest.mark.asyncio
async def test_missing_original_binary_exposes_rebuild_without_repeat_install(manager,monkeypatch):
    manager.records={PKG:record()}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[]))
    async def download(client,url,*args,**kwargs):
        if url.startswith(module.API):return json.dumps([{'sha':SHA}]).encode()
        if url.endswith('index.pb'):return index(OTHER)
        raise ValueError('Original repository returned HTTP 404; no alternate package was selected')
    monkeypatch.setattr(module,'fetch',download)
    await manager.install(PKG,asyncio.Lock())
    assert manager.records[PKG]['archive_available'] is False
    monkeypatch.setattr(settings,'repair_worker_url','http://private-worker')
    result=await manager.snapshot()
    assert result['items'][0]['can_install'] is False
    assert result['items'][0]['can_repair'] is True


@pytest.mark.asyncio
async def test_recovered_candidate_does_not_overwrite_newly_installed_source(manager,monkeypatch):
    manager.records={PKG:record()}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[{'pkgName':PKG,'isInstalled':True}]))
    with pytest.raises(ValueError,match='will not be overwritten'):
        await manager.install_recovered({'id':'d'*32,'pkg_name':PKG,'expected_ids':['99']},jar())


@pytest.mark.asyncio
async def test_recovered_candidate_persists_functional_install_state(manager,monkeypatch):
    monkeypatch.setattr(manager,'check_current',AsyncMock())
    from app.suwayomi import source_host
    from app.service import service
    manager.records={PKG:record()}
    ext={'pkgName':PKG,'isInstalled':True,'source':{'nodes':[{'id':'99'}]}}
    monkeypatch.setattr(module,'inventory',AsyncMock(side_effect=[[],[ext]]))
    sent=AsyncMock(return_value=httpx.Response(200,request=httpx.Request('POST','http://source/api/v1/extension/install')))
    monkeypatch.setattr(source_host.client,'post',sent)
    monkeypatch.setattr(service,'schedule_source_reconciliation',lambda **k:None)
    await manager.install_recovered({'id':'d'*32,'pkg_name':PKG,'expected_ids':['99']},jar())
    assert manager.records[PKG]['state']=='repaired'
    assert manager.records[PKG]['download_sha256']==module.sha(jar())
    assert manager.operation['state']=='installed'


def test_recovery_request_requires_original_commit_and_exact_source_identity():
    import sys
    from pathlib import Path
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
    import contracts
    request={'pkg_name':PKG,'module':'src/en/example','issue':'Recover original source code','recovery':True,
             'revision':'a'*40,'expected_sources':[{'id':'99','name':'Example','lang':'en','baseUrl':'https://example.org'}]}
    assert contracts.request_contract(request)['expected_sources'][0]['id']=='99'
    with pytest.raises(ValueError,match='pinned'):contracts.request_contract({**request,'revision':''})
    with pytest.raises(ValueError,match='identity'):contracts.request_contract({**request,'expected_sources':[{'id':'wrong'}]})


def test_recovery_worker_validates_before_candidate_is_ready(tmp_path,monkeypatch):
    import sys
    from pathlib import Path
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
    import runner as worker_runner
    from contracts import manifest_contract
    # Use the manifest contract expected by a real native extension.
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w') as archive:
        archive.writestr('AndroidManifest.xml','<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="'+PKG+'" android:versionName="1.4.1"><application><meta-data android:name="tachiyomi.extension.class" android:value=".Example"/></application></manifest>')
        archive.writestr('Example.class',b'fixture-code'*20)
    data=stream.getvalue()
    monkeypatch.setattr(worker_runner.os,'chown',lambda *a:None)
    r=worker_runner.Runner(tmp_path,tmp_path,'private')
    job={'id':'a'*32,'pkg_name':PKG,'module':'src/en/example','revision':'b'*40,'recovery':True,
         'expected_sources':[{'id':'99','name':'Example','lang':'en','baseUrl':'https://example.org'}]}
    r.path(job['id']).mkdir()
    monkeypatch.setattr(r,'checkout',lambda job:tmp_path)
    monkeypatch.setattr(r,'build',lambda *a:data)
    def validate(*a,**kw):
        assert kw['expected']==job['expected_sources']
        return job['expected_sources'],[{'metadata':'passed','chapters':'passed','pages':'passed'}]
    monkeypatch.setattr(r,'inspect_artifact',validate)
    r.run(job)
    assert job['state']=='ready' and job['checks'] and job['expected_ids']==['99']
    assert (r.path(job['id'])/'candidate.jar').read_bytes()==data
    assert manifest_contract(data)[0]==PKG


def test_recovery_worker_stops_on_human_challenge(tmp_path,monkeypatch):
    import sys
    from pathlib import Path
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
    import runner as worker_runner
    import validate
    monkeypatch.setattr(worker_runner.os,'chown',lambda *a:None)
    r=worker_runner.Runner(tmp_path,tmp_path,'private')
    job={'id':'b'*32,'pkg_name':PKG,'module':'src/en/example','revision':'c'*40,'recovery':True,'expected_sources':[{'id':'99'}]}
    r.path(job['id']).mkdir()
    monkeypatch.setattr(r,'checkout',lambda j:tmp_path)
    monkeypatch.setattr(r,'build',lambda *a:jar())
    monkeypatch.setattr(worker_runner,'manifest_contract',lambda b:(PKG,'Example','1.4'))
    def challenge(*a,**k):raise validate.SourceBlocked('Source needs human verification')
    monkeypatch.setattr(r,'inspect_artifact',challenge)
    monkeypatch.setattr(r,'edit',lambda *a:pytest.fail('must not edit around CAPTCHA'))
    r.run(job)
    assert job['state']=='blocked'
    assert not (r.path(job['id'])/'candidate.jar').exists()


def test_generated_manifest_comment_is_valid_but_entity_declarations_are_not():
    import sys
    from pathlib import Path
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
    import contracts
    xml=('<?xml version="1.0"?><!-- Generated by the original extension plugin -->'
         '<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="'+PKG+'" android:versionName="1.4.1">'
         '<application><meta-data android:name="tachiyomi.extension.class" android:value=".Example"/></application></manifest>')
    assert module.manifest_package(xml.encode())==PKG
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w') as archive:archive.writestr('AndroidManifest.xml',xml)
    assert contracts.manifest_contract(stream.getvalue())[0]==PKG
    for declaration in ('<!DOCTYPE x [<!ENTITY key SYSTEM "file:///secret">]>', '<!ENTITY x "bad">'):
        with pytest.raises(ValueError,match='declaration'):module.manifest_package((declaration+xml).encode())


def test_original_compile_failure_gets_only_one_scoped_repair(tmp_path,monkeypatch):
    import sys
    from pathlib import Path
    sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
    import runner as worker_runner
    monkeypatch.setattr(worker_runner.os,'chown',lambda *a:None)
    r=worker_runner.Runner(tmp_path,tmp_path,'private')
    job={'id':'c'*32,'pkg_name':PKG,'module':'src/en/example','revision':'d'*40,'recovery':True,'expected_sources':[{'id':'99'}]}
    r.path(job['id']).mkdir()
    monkeypatch.setattr(r,'checkout',lambda j:tmp_path)
    calls=[]
    def build(job,tree,kind):
        calls.append(kind)
        if kind=='upstream-build':raise RuntimeError('Original Kotlin no longer compiles')
        return jar()
    monkeypatch.setattr(r,'build',build)
    edits=[]
    monkeypatch.setattr(r,'edit',lambda *a:edits.append('Kotlin-only') or tmp_path)
    monkeypatch.setattr(worker_runner,'manifest_contract',lambda data:(PKG,'Example','1.4'))
    monkeypatch.setattr(r,'inspect_artifact',lambda *a,**k:(job['expected_sources'],[{'metadata':'passed','pages':'passed'}]))
    r.run(job)
    assert edits==['Kotlin-only'] and calls==['upstream-build','repair-build']
    assert job['state']=='ready' and job['candidate_kind']=='repaired-orphan-recovery'
    assert job['baseline_sha256']=='' and not (r.path(job['id'])/'baseline.jar').exists()


def test_shipped_orphan_seed_contains_only_original_observed_provenance():
    from pathlib import Path
    seed=json.loads(Path(module.__file__).with_name('source_orphans_seed.json').read_text())
    assert seed['records'] and seed['refresh']['checked_at']
    assert all(module.PKG.fullmatch(pkg) and module.COMMIT.fullmatch(row['commit']) for pkg,row in seed['records'].items())
    assert all(row['origin']=='Keiyoushi' for row in seed['records'].values())
    assert all(row['archive_url']==module.package_url(row['archive_url'],row['commit']) for row in seed['records'].values())
    assert any(row.get('archive_available') is False for row in seed['records'].values())


@pytest.mark.asyncio
async def test_recovery_install_timeout_marks_uncertain_without_replaying(manager,monkeypatch):
    from app.suwayomi import source_host
    manager.records={PKG:record()}
    monkeypatch.setattr(manager,'check_current',AsyncMock())
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[]))
    sent=AsyncMock(side_effect=httpx.ReadTimeout('Source engine reply lost'))
    monkeypatch.setattr(source_host.client,'post',sent)
    with pytest.raises(httpx.ReadTimeout):
        await manager.install_recovered({'id':'d'*32,'pkg_name':PKG,'expected_ids':['99']},jar())
    assert manager.operation['state']=='failed'
    assert 'not confirmed' in manager.operation['message']
    assert 'no automatic replay' in manager.operation['message']
    assert json.loads(manager.path().read_text())['operation']['state']=='failed'
    sent.assert_awaited_once()


@pytest.mark.asyncio
async def test_recovery_install_defers_to_reappeared_original_package(manager,monkeypatch):
    from app.suwayomi import source_host
    manager.records={PKG:record()}
    monkeypatch.setattr(module,'inventory',AsyncMock(return_value=[]))
    monkeypatch.setattr(manager,'check_current',AsyncMock(side_effect=ValueError('Maintained package available again')))
    sent=AsyncMock();monkeypatch.setattr(source_host.client,'post',sent)
    with pytest.raises(ValueError,match='available again'):
        await manager.install_recovered({'id':'d'*32,'pkg_name':PKG,'expected_ids':['99']},jar())
    sent.assert_not_awaited()
    assert manager.operation['state']=='failed'


@pytest.mark.asyncio
async def test_cached_orphans_return_before_slow_engine_inventory(manager,monkeypatch):
    manager.records={PKG:record()}
    blocker=asyncio.Event()
    async def inventory():await blocker.wait();return []
    monkeypatch.setattr(module,'inventory',inventory)
    result=await asyncio.wait_for(manager.snapshot(),.2)
    assert len(result['items'])==1 and result['inventory_refreshing']
    assert not result['items'][0]['can_install']
    blocker.set()
    await manager.inventory_task
    result=await manager.snapshot()
    assert result['inventory_known'] and result['items'][0]['can_install']
    await manager.stop()


@pytest.mark.asyncio
async def test_orphan_routes_require_admin_before_any_inventory_or_write(manager,monkeypatch):
    from fastapi import FastAPI,HTTPException
    app=FastAPI()
    def denied():raise HTTPException(403,'Admin required')
    monkeypatch.setattr(module,'source_orphans',manager)
    snapshot=AsyncMock();monkeypatch.setattr(manager,'snapshot',snapshot)
    app.include_router(module.orphan_router(denied,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        for method,path in [('GET',''),('POST','/refresh'),('POST','/'+PKG+'/install'),('POST','/'+PKG+'/repair')]:
            response=await client.request(method,'/api/admin/source-orphans'+path)
            assert response.status_code==403
    snapshot.assert_not_awaited()
    assert manager.action is None and manager.task is None


@pytest.mark.asyncio
async def test_recovery_preparation_does_not_hold_browser_request_open(manager,monkeypatch):
    from fastapi import FastAPI
    app=FastAPI()
    monkeypatch.setattr(module,'source_orphans',manager)
    monkeypatch.setattr(settings,'repair_worker_url','http://private-worker')
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    barrier=asyncio.Event()
    async def prepare(*args):await barrier.wait()
    monkeypatch.setattr(manager,'prepare_repair',prepare)
    app.include_router(module.orphan_router(lambda:None,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        response=await asyncio.wait_for(client.post('/api/admin/source-orphans/'+PKG+'/repair'),.2)
    assert response.status_code==202 and response.json()['queued']
    assert manager.action and not manager.action.done()
    barrier.set();await manager.stop()


@pytest.mark.asyncio
async def test_recovery_receipt_cannot_rollback_an_unrelated_previous_repair(manager,monkeypatch):
    from app import source_maintenance as maintenance
    from app.source_engine import source_engine
    from app.service import service
    job={'id':'d'*32,'pkg_name':PKG,'state':'ready','checks':[{'pages':'passed'}],
         'expected_ids':['99'],'validation_time':time.time(),'candidate_sha256':module.sha(jar()),'recovery':True}
    async def remote(method,path,**kwargs):return jar() if path.endswith('/artifact') else job
    monkeypatch.setattr(maintenance,'worker',remote)
    monkeypatch.setattr(module,'source_orphans',manager)
    monkeypatch.setattr(manager,'install_recovered',AsyncMock(return_value={'pkgName':PKG,'isInstalled':True}))
    monkeypatch.setattr(source_engine,'state','ready')
    monkeypatch.setattr(service,'schedule_source_reconciliation',lambda **k:None)
    maintenance.atomic(maintenance.storage()/'promotion.json',json.dumps({'state':'installed','extension':{'pkgName':OTHER}}).encode())
    m=maintenance.SourceMaintenance();await m.install(job['id'],asyncio.Lock())
    assert m.operation['state']=='recovered'
    receipt=json.loads((maintenance.storage()/'promotion.json').read_text())
    assert receipt['state']=='recovered' and receipt['extension']['pkgName']==PKG
    restarted=maintenance.SourceMaintenance();restarted.recover()
    assert restarted.operation['state']=='recovered'


def test_slow_earlier_file_flush_cannot_overwrite_a_newer_state(manager,monkeypatch):
    from concurrent.futures import ThreadPoolExecutor
    import threading
    entered=threading.Event();release=threading.Event()
    real_atomic=module.atomic
    calls=[]
    def slow_first(path,data):
        calls.append(json.loads(data)['operation']['state'])
        if len(calls)==1:
            entered.set();assert release.wait(2)
        real_atomic(path,data)
    monkeypatch.setattr(module,'atomic',slow_first)
    manager.operation={'state':'older'}
    with ThreadPoolExecutor(2) as pool:
        first=pool.submit(manager.save)
        assert entered.wait(2)
        manager.operation={'state':'newer'}
        second=pool.submit(manager.save)
        release.set()
        first.result(2);second.result(2)
    assert json.loads(manager.path().read_text())['operation']['state']=='newer'


def test_recovery_preparation_restart_does_not_remain_running(manager):
    manager.operation={'state':'running','pkgName':PKG,'message':'Locating original source'}
    manager.save()
    restored=module.SourceOrphans()
    restored.load()
    assert restored.operation['state']=='interrupted'
    assert 'interrupted' in restored.operation['message']
