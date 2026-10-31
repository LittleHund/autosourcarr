"""Exercise actual private HTTP admission with synthetic code and no AI/native tools."""
import base64
import importlib.util
import json
from pathlib import Path
import sys
import threading
from types import ModuleType,SimpleNamespace

import httpx
import pytest

from test_v0345_reconstruction import PKG,jar
from test_v0345_raw_archive import FILES,archive


@pytest.fixture
def worker(tmp_path,monkeypatch):
    root=Path(__file__).resolve().parents[2];monkeypatch.syspath_prepend(str(root));monkeypatch.syspath_prepend(str(root/'repair-worker'))
    stub=ModuleType('runner');stub.Runner=object;monkeypatch.setitem(sys.modules,'runner',stub)
    control=ModuleType('control');control.Control=object;monkeypatch.setitem(sys.modules,'control',control)
    spec=importlib.util.spec_from_file_location('raw_test_server',root/'repair-worker/server.py');api=importlib.util.module_from_spec(spec);spec.loader.exec_module(api)
    class Runner:
        active=None;isolation={'available':True};cancelled=set()
        def __init__(self):self.executed=[];self.event=threading.Event()
        def path(self,job):
            assert len(job)==32 and all(c in '0123456789abcdef' for c in job)
            return tmp_path/job
        def save(self,job,state,message,**extra):
            job.update(state=state,message=message,updated_at=100.+len(job.get('history',[])),**extra)
            job.setdefault('history',[]).append({'state':state})
            (self.path(job['id'])/'job.json').write_text(json.dumps(job))
        def run(self,job):self.executed.append(dict(job));self.event.set()
    api.runner=Runner();api.control=SimpleNamespace(busy=False,_retained_lock=None)
    monkeypatch.setenv('REPAIR_TOKEN','synthetic-private-token-for-unit-tests-only')
    yield api
    api.executor.shutdown(wait=True);api.pending.clear()


def request(**values):
    return {'pkg_name':PKG,'module':'src/en/fixture','issue':'Catalog and chapter requests stopped working.',
        'request_id':'a'*32,'raw_first':True,'installed_version':'1.4.23','auto_install':True,
        'installed_sources':[{'id':'123','name':'Fixture','lang':'en','baseUrl':'https://fixture.example/'}],
        'baseline':base64.b64encode(jar()).decode(),**values}


def client(worker):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=worker.app),base_url='http://test',
        headers={'Authorization':'Bearer synthetic-private-token-for-unit-tests-only'})


@pytest.mark.asyncio
async def test_missing_code_waits_without_using_a_worker_slot_and_request_retry_is_idempotent(worker):
    async with client(worker) as http:
        first=await http.post('/jobs',json=request())
        assert first.status_code==202 and first.json()['state']=='awaiting-raws'
        assert not worker.pending and worker.runner.executed==[]
        same=await http.post('/jobs',json=request());assert same.json()['id']==first.json()['id']
        changed=await http.post('/jobs',json=request(issue='Different input using an old request identity.'))
        assert changed.status_code==409 and worker.runner.executed==[]


@pytest.mark.asyncio
async def test_unavailable_is_an_explicit_generation_bound_choice_and_cancelled_wait_does_not_run(worker):
    async with client(worker) as http:
        job=(await http.post('/jobs',json=request())).json()
        stale=await http.post('/jobs/'+job['id']+'/raws',json={'reconstruct':True,'expected_updated_at':-1})
        assert stale.status_code==400 and worker.runner.executed==[]
        response=await http.post('/jobs/'+job['id']+'/raws',json={'reconstruct':True,'expected_updated_at':job['updated_at']})
        assert response.status_code==202
        assert worker.runner.event.wait(2) and worker.runner.executed[0]['reconstruct'] is True
        again=await http.post('/jobs/'+job['id']+'/raws',json={'reconstruct':True,'expected_updated_at':job['updated_at']})
        assert again.status_code==409 and len(worker.runner.executed)==1


@pytest.mark.asyncio
async def test_job_bound_project_input_preserves_ecosystem_and_rejects_path_escape(worker):
    info={'package':PKG,'version':'1.4.23','module':'src/en/fixture','ecosystem':'mihon','kind':'provided','recipe':'gradle','origin':{'basis':'user-provided'}}
    files={name.replace('src/en/sample','src/en/fixture'):value for name,value in FILES.items()}
    # FILES uses the helper's module; replace whichever module it declares.
    from test_v0345_raw_archive import MODULE
    files={name.replace(MODULE,'src/en/fixture'):value for name,value in FILES.items()}
    async with client(worker) as http:
        job=(await http.post('/jobs',json=request())).json()
        bad=await http.post('/jobs/'+job['id']+'/raws',json={'expected_updated_at':job['updated_at'],'raw_meta':info,
            'raw_code':base64.b64encode(archive({'../escape.java':b'bad'})).decode()})
        assert bad.status_code==400 and worker.runner.executed==[]
        good=await http.post('/jobs/'+job['id']+'/raws',json={'expected_updated_at':job['updated_at'],'raw_meta':info,
            'raw_code':base64.b64encode(archive(files)).decode()})
        assert good.status_code==202 and worker.runner.event.wait(2)
        assert worker.runner.executed[0]['raw_meta']['ecosystem']=='mihon'
        assert (worker.runner.path(job['id'])/'baseline.jar').read_bytes()==jar()


@pytest.mark.asyncio
async def test_waiting_input_can_be_cancelled_without_starting_code_execution(worker):
    async with client(worker) as http:
        job=(await http.post('/jobs',json=request())).json()
        assert (await http.post('/jobs/'+job['id']+'/cancel')).json()['requested'] is True
        result=await http.post('/jobs/'+job['id']+'/raws',json={'reconstruct':True,'expected_updated_at':job['updated_at']})
        assert result.status_code==409 and worker.runner.executed==[]
