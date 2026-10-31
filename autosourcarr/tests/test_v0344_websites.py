"""Public probes, corresponding-address policy and immutable input handoff."""
import ast
from contextlib import closing
import hashlib
import importlib
import io
import json
from pathlib import Path
from types import SimpleNamespace
import zipfile
import asyncio
import sys
import tempfile
from unittest.mock import AsyncMock

import httpx
import pytest

ROOT=Path(__file__).resolve().parents[2]


@pytest.fixture
def modules(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT/'repair-worker'))
    return SimpleNamespace(probe=importlib.import_module('website_probe'),policy=importlib.import_module('address_policy'),inputs=importlib.import_module('repair_inputs'))


def response(status=200,body=b'<html>Source</html>',**headers):
    return httpx.Response(status,headers={'Content-Type':'text/html',**headers},stream=httpx.ByteStream(body))


def test_root_redirect_and_advertised_api_are_checked_without_cookies(modules):
    requests=[]
    def fetch(request):
        requests.append(request)
        assert 'cookie' not in request.headers and 'authorization' not in request.headers
        assert request.headers['accept-encoding']=='identity'
        if request.url.host=='old.example':return response(301,Location='https://new.example/')
        if request.url.path=='/api':return response(body=b'{"routes":{}}',**{'Content-Type':'application/json'})
        return response(body=b'<link rel="https://api.w.org/" href="/api">',**{'Set-Cookie':'private=do-not-forward'})
    with httpx.Client(transport=httpx.MockTransport(fetch),trust_env=False) as client:
        rows=modules.probe.WebsiteProbe(client,pace=0).check([{'id':'-9223372036854775808','baseUrl':'https://old.example'}])
    assert rows[0]['state']=='working' and rows[0]['final_url']=='https://new.example/'
    assert rows[0]['apis'][0]['state']=='working'
    assert len(requests)==3 and rows[0]['source_ids']==['-9223372036854775808']


@pytest.mark.parametrize('url',['http://127.0.0.1/','https://[::1]/','https://localhost./','https://site.example/?token=secret','https://user:pass@site.example/','file:///secret','https://site.example:8766/'])
def test_private_credential_and_nonpublic_inputs_are_rejected(modules,url):
    with pytest.raises(ValueError):modules.probe.public_url(url)


def test_proxy_dns_rejects_mixed_public_and_private_answers(monkeypatch,modules):
    egress=importlib.import_module('egress')
    monkeypatch.setattr(egress.socket,'getaddrinfo',lambda *a,**k:[(2,1,6,'',('8.8.8.8',443)),(2,1,6,'',('127.0.0.1',443))])
    with pytest.raises(ValueError):egress.public_address('source.example',443)


def test_downgrade_and_advertised_api_cross_origin_stop_before_following(modules):
    calls=[]
    def fetch(request):
        calls.append(str(request.url))
        return response(302,Location='http://other.example/')
    with httpx.Client(transport=httpx.MockTransport(fetch),trust_env=False) as client:
        result=modules.probe.WebsiteProbe(client,pace=0).one('https://source.example/')
    assert result['state']=='inconclusive' and len(calls)==1
    calls.clear()
    with httpx.Client(transport=httpx.MockTransport(lambda request:(calls.append(str(request.url)) or response(302,Location='https://other.example/api'))),trust_env=False) as client:
        result=modules.probe.WebsiteProbe(client,pace=0).one('https://source.example/api',allowed_origin='https://source.example')
    assert result['state']=='inconclusive' and len(calls)==1


@pytest.mark.parametrize('status,body,state',[(429,b'', 'rate_limited'),(200,b'<div class="cf-chl-">challenge</div>','verification'),(404,b'not found','not_found'),(503,b'not ready','inconclusive')])
def test_website_outcomes_do_not_equate_uncertainty_and_outage(modules,status,body,state):
    with httpx.Client(transport=httpx.MockTransport(lambda request:response(status,body)),trust_env=False) as client:
        assert modules.probe.WebsiteProbe(client,pace=0).one('https://source.example')['state']==state


def test_encoded_response_is_not_transparently_decoded(modules):
    class Unread(httpx.SyncByteStream):
        def __iter__(self):raise AssertionError('Encoded body must not be consumed/decompressed')
    with httpx.Client(transport=httpx.MockTransport(lambda request:httpx.Response(200,headers={'Content-Encoding':'gzip','Content-Type':'text/html'},stream=Unread())),trust_env=False) as client:
        row=modules.probe.WebsiteProbe(client,pace=0).one('https://source.example')
    assert row['state']=='inconclusive' and row['reason']=='encoded-response-not-decoded'


def test_probe_request_budget_never_retries(modules):
    calls=[]
    with httpx.Client(transport=httpx.MockTransport(lambda request:(calls.append(request) or response())),trust_env=False) as client:
        probe=modules.probe.WebsiteProbe(client,pace=0);probe.requests=modules.probe.MAX_REQUESTS
        assert probe.one('https://source.example')['state']=='inconclusive'
    assert not calls


def test_regular_contact_captcha_is_not_a_website_challenge(modules):
    with httpx.Client(transport=httpx.MockTransport(lambda request:response(body=b'<html><h1>Catalog</h1><form class="contact"><div class="g-recaptcha"></div></form></html>')),trust_env=False) as client:
        assert modules.probe.WebsiteProbe(client,pace=0).one('https://source.example')['state']=='working'


def source(url):return {'id':'1','name':'Source','lang':'en','baseUrl':url}


def test_corresponding_https_redirect_can_stage_but_not_guess_a_domain(modules):
    job={'website_checks':[{'state':'working','source_ids':['1'],'requested_url':'https://old.example/','final_url':'https://new.example/','redirects':['https://new.example/']}]}
    policy=modules.policy.AddressPolicy(job,lambda rows:[])
    with pytest.raises(modules.policy.AddressNeedsUpdate):policy.check([source('https://old.example')],[source('https://old.example')])
    changes=policy.check([source('https://new.example')],[source('https://old.example')])
    assert changes[0]['basis']=='observed-https-redirect'
    with pytest.raises(modules.policy.AddressBlocked):policy.check([source('https://unrelated.example')],[source('https://old.example')])


def test_official_changed_address_requires_pinned_revision_and_working_probe(modules):
    job={'source_revision':'a'*40}
    policy=modules.policy.AddressPolicy(job,lambda rows:[{'state':'working','source_ids':['1'],'requested_url':'https://new.example/','final_url':'https://new.example/'}])
    changes=policy.check([source('https://new.example')],[source('https://old.example')],official=True)
    assert changes[0]['source_revision']=='a'*40 and changes[0]['basis']=='maintained-source-revision'
    with pytest.raises(modules.policy.AddressBlocked):modules.policy.AddressPolicy({},lambda rows:[]).check([source('https://new.example')],[source('https://old.example')],official=True)
    with pytest.raises(modules.policy.AddressBlocked):modules.policy.AddressPolicy({'source_revision':'a'*40},lambda rows:[{'state':'inconclusive','source_ids':['1']}]).check([source('https://new.example')],[source('https://old.example')],official=True)


def test_input_download_contains_only_hash_bound_packages(tmp_path,modules):
    jar,apk=b'JAR_INPUT'*30,b'APK_INPUT'*30
    (tmp_path/'baseline.jar').write_bytes(jar);(tmp_path/'source.apk').write_bytes(apk)
    (tmp_path/'auth.json').write_text('PRIVATE_ACCOUNT')
    job={'id':'a'*32,'pkg_name':'eu.kanade.tachiyomi.extension.en.example','baseline_sha256':hashlib.sha256(jar).hexdigest(),'source_apk_sha256':hashlib.sha256(apk).hexdigest(),'conversation':'PRIVATE_CONVERSATION'}
    value=modules.inputs.input_archive(tmp_path,job)
    with zipfile.ZipFile(io.BytesIO(value)) as archive:
        assert set(archive.namelist())=={'baseline.jar','source.apk','input-hashes.json'}
        assert archive.read('baseline.jar')==jar and archive.read('source.apk')==apk
        assert b'PRIVATE' not in archive.read('input-hashes.json')
    (tmp_path/'source.apk').write_bytes(b'CHANGED'*40)
    with pytest.raises(ValueError,match='changed'):modules.inputs.input_archive(tmp_path,job)


def test_website_evidence_export_is_bounded_and_masks_hosts():
    from app.repair_evidence import safe_job
    from app.support_logs import SupportPrivacy
    value=safe_job({'id':'a'*32,'state':'checking-website','website_checks':[{'state':'working','requested_url':'https://source.example/','response_body':'PRIVATE_PAGE','cookie':'PRIVATE_COOKIE','http_status':200,
                 'apis':[{'state':{},'final_url':'https://user:secret@source.example/api'}]}]},SupportPrivacy())
    encoded=json.dumps(value)
    assert 'PRIVATE_' not in encoded and 'source.example' not in encoded and 'secret' not in encoded
    assert value['website_checks'][0]['http_status']==200


def test_actual_runner_checks_website_before_rejecting_apk_proof(tmp_path,modules):
    from validate import SourceBlocked
    tree=ast.parse((ROOT/'repair-worker/runner.py').read_text(encoding='utf-8'))
    cls=next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='Runner')
    run=next(n for n in cls.body if isinstance(n,ast.FunctionDef) and n.name=='run')
    namespace={'SourceBlocked':SourceBlocked,'zipfile':zipfile,'io':io}
    exec(compile(ast.Module(body=[run],type_ignores=[]),'<actual-runner-run>','exec'),namespace)
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w') as archive:archive.writestr('Source.class',b'compiled fixture')
    (tmp_path/'baseline.jar').write_bytes(stream.getvalue())
    stages=[]
    def save(job,state,message,**values):job.update(state=state,message=message,**values);stages.append(state)
    def reject(*args):raise SourceBlocked('Exact APK mismatch retained')
    runner=SimpleNamespace(path=lambda key:tmp_path,save=save,check_websites=lambda job,rows:[{'state':'working','source_ids':['1']}],converted_baseline=reject,cancelled=set())
    job={'id':'a'*32,'installed_sources':[source('https://source.example')]}
    namespace['run'](runner,job)
    assert stages==['checking-website','checking-backup','blocked']
    assert job['website_checks'][0]['state']=='working' and 'APK mismatch' in job['message']


def test_actual_internal_probe_uses_the_existing_sandbox_code_mount(tmp_path,modules):
    from validate import SourceBlocked
    tree=ast.parse((ROOT/'repair-worker/internal_runner.py').read_text(encoding='utf-8'))
    cls=next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='InternalRunner')
    method=next(n for n in cls.body if isinstance(n,ast.FunctionDef) and n.name=='check_websites')
    namespace={'tempfile':tempfile,'Path':Path,'json':json,'SourceBlocked':SourceBlocked}
    exec(compile(ast.Module(body=[method],type_ignores=[]),'<actual-internal-probe>','exec'),namespace)
    seen=[]
    def sandbox(work,args,**options):
        assert args==['/opt/autosourcarr-venv/bin/python','/sandbox-tools/website_probe.py']
        assert options=={'sockets':'PRIVATE_EGRESS_SOCKET'}
        assert json.loads((work/'targets.json').read_text())==[{'id':'1','baseUrl':'https://source.example/'}]
        seen.append('namespace');return ['owned-command']
    owner=SimpleNamespace(path=lambda key:tmp_path,sockets='PRIVATE_EGRESS_SOCKET',
        require_isolation=lambda:seen.append('isolation'),sandbox=SimpleNamespace(command=sandbox),
        command=lambda job,args,timeout:json.dumps([{'state':'working','source_ids':['1']}]))
    result=namespace['check_websites'](owner,{'id':'a'*32},[source('https://source.example'),source('https://user:secret@source.example')])
    assert result[0]['state']=='working' and seen==['isolation','namespace']
    seen.clear();assert namespace['check_websites'](owner,{'id':'a'*32},[])==[] and seen==[]


def test_status_wire_bounds_new_evidence_but_details_retain_it(modules):
    contracts=importlib.import_module('contracts')
    job={'id':'a'*32,'created_at':1,'state':'ready','address_migrations':[{'previous_url':'https://old.example','new_url':'https://new.example'}],
         'website_checks':[{'state':'working','requested_url':'https://site.example/','final_url':'https://site.example/',
                            'response_body':'PRIVATE_BODY','redirects':['https://site.example/']*200,'source_ids':['1']*200}]*100}
    summary=contracts.status_job(job)
    assert summary['address_migration_count']==1 and 'address_migrations' not in summary
    assert len(summary['website_checks'])==8 and len(summary['website_checks'][0]['source_ids'])==8
    assert 'PRIVATE_BODY' not in json.dumps(summary) and 'redirects' not in summary['website_checks'][0]
    assert contracts.public_job(job)['address_migrations']==job['address_migrations']
    with pytest.raises(ValueError):contracts.status_job({**job,'created_at':float('nan')})


@pytest.mark.parametrize('live', ['https://old.example/', 'http://new.example/', 'https://new.example/other',
                                  'https://new.example/?key=secret', None])
def test_live_address_must_match_the_reviewed_candidate(live):
    from app.source_maintenance import verify_repaired_addresses
    migration=[{'source_id':'1','new_url':'https://new.example/'}]
    with pytest.raises(ValueError):
        verify_repaired_addresses({'source':{'nodes':[source(live)]}},migration)
    verify_repaired_addresses({'source':{'nodes':[source('https://NEW.example:443')]}},migration)


@pytest.mark.asyncio
async def test_authenticated_input_proxy_and_candidate_bound_address_review(monkeypatch):
    from fastapi import APIRouter,FastAPI,HTTPException
    from app import source_maintenance as module
    from app.config import settings
    monkeypatch.setitem(sys.modules,'app.repair_connection',SimpleNamespace(connection_router=lambda *args:APIRouter()))
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    manager=module.SourceMaintenance();started=[]
    async def install(identifier,lock,**values):started.append((identifier,values))
    monkeypatch.setattr(manager,'install',install)
    monkeypatch.setattr(module,'source_maintenance',manager)
    job={'id':'a'*32,'state':'ready','candidate_sha256':'c'*64,
         'address_migrations':[{'previous_url':'https://old.example/','new_url':'https://new.example/'}]}
    async def worker(method,path,**values):
        assert method=='GET'
        if path.endswith('/inputs'):
            assert values.get('binary') is True
            return b'PK synthetic source input archive'
        return job
    proxy=AsyncMock(side_effect=worker);monkeypatch.setattr(module,'worker',proxy)
    app=FastAPI();app.include_router(module.maintenance_router(lambda:None,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        download=await client.get('/api/admin/source-maintenance/jobs/'+'a'*32+'/inputs')
        assert download.status_code==200 and download.headers['content-type']=='application/zip'
        assert 'no-store' in download.headers['cache-control']
        path='/api/admin/source-maintenance/jobs/'+'a'*32+'/install'
        assert (await client.post(path,json={})).status_code==409
        assert (await client.post(path,json={'approve_address_changes':True,'reviewed_candidate_sha256':'d'*64})).status_code==409
        job['candidate_sha256']=None
        assert (await client.post(path,json={'approve_address_changes':True})).status_code==409
        job['candidate_sha256']='c'*64
        assert not started
        accepted=await client.post(path,json={'approve_address_changes':True,'reviewed_candidate_sha256':'c'*64})
        assert accepted.status_code==202
        await asyncio.sleep(0)
        assert started==[('a'*32,{'reviewed_candidate_sha256':'c'*64})]
    def deny():raise HTTPException(401,'Authentication required')
    guarded=FastAPI();guarded.include_router(module.maintenance_router(deny,asyncio.Lock()))
    proxy.reset_mock()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=guarded),base_url='http://test') as client:
        assert (await client.get('/api/admin/source-maintenance/jobs/'+'a'*32+'/inputs')).status_code==401
    proxy.assert_not_awaited()
