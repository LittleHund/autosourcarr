"""Actual app admission boundary with file-backed history/catalogue and fake RPC."""
import base64
import importlib
from unittest.mock import AsyncMock
import pytest

from test_v0345_raw_archive import PKG


@pytest.mark.asyncio
async def test_raw_first_admission_forwards_exact_retained_apk_proof_and_keeps_a_private_intent(tmp_path,monkeypatch):
    maintenance=importlib.import_module('app.source_maintenance');baseline=importlib.import_module('app.repair_baseline')
    module=importlib.import_module('app.repair_workflow');raw=importlib.import_module('app.source_raws')
    monkeypatch.setattr(maintenance.settings,'data_dir',tmp_path)
    workflow=module.RepairWorkflow();workflow.initialize()
    monkeypatch.setattr(raw.source_raws,'catalog',raw.SourceRawCatalog(tmp_path))
    monkeypatch.setattr(raw.source_raws,'schedule_intent',lambda _,**options:None)
    extension={'pkgName':PKG,'versionName':'1.4.23','isInstalled':True,
        'storeIndexUrl':'https://github.com/keiyoushi/extensions/raw/repo/index.pb',
        'source':{'nodes':[{'id':'123','name':'Fixture','lang':'en','baseUrl':'https://fixture.example/'}]}}
    proof={'baseline':base64.b64encode(b'Exact saved installed binary').decode(),'source_apk':'EXACT_RETAINED_APK_INPUT',
           'installed_version':'1.4.23','installed_sources':extension['source']['nodes'],
           'original_apk_basis':'recorded-installed-upload','raw_first':True}
    observed=[]
    def retained(value,**options):
        assert options=={'raw_first':True};return proof.copy()
    async def rpc(method,path,*,payload):
        observed.append((method,path,payload))
        assert (workflow.root/(payload['request_id']+'.json')).is_file()
        assert payload['request_id'] in workflow.inflight
        return {'id':payload['request_id'],'state':'awaiting-raws'}
    monkeypatch.setattr(baseline,'repair_input',retained);monkeypatch.setattr(maintenance,'worker',rpc)
    job=await workflow.create(extension,{'issue':'Chapter and search requests stopped working.'})
    payload=observed[0][2]
    assert payload['source_apk']==proof['source_apk'] and payload['baseline']==proof['baseline']
    assert payload['auto_install'] is True and payload['raw_first'] is True
    assert payload['reconstruction_origin']['origin']['repository']=='https://github.com/keiyoushi/extensions-source'
    assert workflow.history.snapshot(PKG)['pending']==job['id']
    assert workflow.admissions[job['id']]['state']=='accepted'
    assert not workflow.inflight


@pytest.mark.asyncio
async def test_busy_worker_defers_before_reading_or_compressing_a_waiting_source_project(monkeypatch):
    from fastapi import HTTPException
    maintenance=importlib.import_module('app.source_maintenance');module=importlib.import_module('app.repair_workflow')
    monkeypatch.setattr(maintenance,'ensure_repair_ready',AsyncMock(side_effect=HTTPException(409,'Worker busy')))
    workflow=module.RepairWorkflow()
    monkeypatch.setattr(workflow,'raw_payload',AsyncMock(side_effect=AssertionError('Busy worker must not trigger a code export/transfer')))
    with pytest.raises(HTTPException) as error:await workflow.resume({'id':'a'*32,'pkg_name':PKG,'updated_at':1})
    assert error.value.status_code==409
    workflow.raw_payload.assert_not_awaited()
