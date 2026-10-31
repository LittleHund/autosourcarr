"""Repair setup belongs to the current instance, without deployment forms."""
import asyncio
from pathlib import Path
from unittest.mock import AsyncMock

import httpx
from fastapi import FastAPI, HTTPException
import pytest

from app import config, internal_services, repair_connection, source_maintenance


@pytest.mark.parametrize('mode',['integrated','external'])
@pytest.mark.asyncio
async def test_retired_deployment_routes_never_build_or_connect(mode,monkeypatch):
    monkeypatch.setattr(config.settings,'app_services_mode',mode)
    preview=AsyncMock(); worker=AsyncMock()
    monkeypatch.setattr(repair_connection,'preview',preview)
    app=FastAPI();app.include_router(repair_connection.connection_router(worker,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        for method,path in [('POST','/setup-preview'),('PUT','/connection')]:
            response=await client.request(method,path,json={'compose':'private draft','token':'private credential'})
            assert response.status_code==410,response.text
            assert 'this AutoSourcarr instance' in response.json()['detail']
            assert 'private credential' not in response.text
        worker.assert_not_called();preview.assert_not_called()


def test_bundled_image_uses_internal_tools_despite_old_environment(tmp_path,monkeypatch):
    server=tmp_path/'repair-worker/server.py';server.parent.mkdir();server.write_text('# bundled runtime')
    marker=tmp_path/'image-version'
    monkeypatch.setattr(config,'BUNDLED_REPAIR_SERVER',server,raising=False)
    monkeypatch.setattr(config,'BUNDLED_IMAGE_VERSION',marker)
    monkeypatch.setenv('APP_SERVICES_MODE','external')
    monkeypatch.setenv('HOSTNAME','arbitrary-library-service')
    assert config.Settings().app_services_mode=='integrated'
    assert config.Settings(app_services_mode='external').app_services_mode=='integrated'
    server.unlink()
    marker.write_text('0.3.22')
    assert config.Settings().app_services_mode=='integrated'  # broken bundled tool
    marker.unlink()
    assert config.Settings().app_services_mode=='external'  # standalone development


def test_repair_page_contains_no_deployment_or_instance_form():
    page=(Path(__file__).resolve().parents[1]/'app/static/index.html').read_text()
    for obsolete in ['repairComposeForm','repairFirstSetup','repairConnectionForm','repairNormalService','repairPrivateService']:
        assert f'id="{obsolete}"' not in page
    assert 'id="configureRepairAI"' in page


def test_integrated_settings_hide_unused_worker_fields_and_preserve_legacy_values(tmp_path,monkeypatch):
    from app.env_settings import settings_payload, update_env
    monkeypatch.setattr(config.settings,'app_services_mode','integrated')
    path=tmp_path/'autosourcarr.env'
    legacy='APP_SERVICES_MODE=external\nREPAIR_WORKER_URL=http://old-worker:8766\nREPAIR_WORKER_TOKEN=old-secret\n'
    path.write_text(legacy+'TZ=UTC\n')
    payload=settings_payload(tmp_path)
    keys={item['key'] for file in payload['files'] for group in file['groups'] for item in group['items']}
    assert keys=={'TZ'}
    update_env(path,{'TZ':'Europe/Amsterdam'})
    assert path.read_text()==legacy+'TZ=Europe/Amsterdam\n'


@pytest.mark.asyncio
async def test_status_distinguishes_internal_failure_from_starting(monkeypatch):
    monkeypatch.setattr(config.settings,'app_services_mode','integrated')
    monkeypatch.setattr(config.settings,'repair_worker_url','http://127.0.0.1:8766')
    monkeypatch.setattr(source_maintenance,'worker',AsyncMock(side_effect=HTTPException(503,'Unavailable')))
    monkeypatch.setattr(internal_services.internal_services,'states',{'repair':{'state':'unavailable','message':'The bundled repair runtime is missing'}})
    app=FastAPI();app.include_router(source_maintenance.maintenance_router(lambda:None,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        value=(await client.get('/api/admin/source-maintenance')).json()
    assert value['mode']=='integrated'
    assert value['service']['state']=='unavailable'
    assert value['service']['message']=='The bundled repair runtime is missing'
    assert value['reachable'] is False


def test_isolation_panel_does_not_recommend_host_changes_before_checking():
    from test_v0315_repair_ui import DOM, STATIC
    from test_v086_ui import node
    source=(STATIC/'repair-isolation.js').read_text()
    node(DOM+source+r'''
const target=ids.repairIsolation;
renderRepairIsolation(target,{available:false,reason:'not-checked',message:'Check repair tools first'});
assert.equal(target.children.length,1);assert.equal(target.children[0].textContent,'Check repair tools first');
assert(!target.children[0].children.length,'No host setup disclosure before preflight');
renderRepairIsolation(target,{available:false,reason:'runtime-missing',message:'Missing bundled tool'});
let text=target.children[0].children.map(x=>x.textContent).join(' ');
assert(text.includes('Missing bundled tool'));assert(!text.includes('host administrator'));
renderRepairIsolation(target,{available:false,reason:'namespace-denied',message:'Namespace denied',details:'bwrap failed'});
text=target.children[0].children.map(x=>x.textContent).join(' ');
assert(text.includes('without this repair check or Compose changes')&&text.includes('cannot grant itself host permissions')&&text.includes('bwrap failed'));
renderRepairIsolation(target,{available:true});assert.equal(target.children.length,0);
''')


def test_direct_ai_setup_enters_repair_step_and_keeps_draft_cancelable():
    from test_v086_ui import node
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');
const assert=require('node:assert/strict');
(async()=>{
 const h=harness();
 h.data.fields.push({key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT']],value:'chatgpt',description:''});
 const base=h.context.api;
 h.context.api=(path,opts)=>path.endsWith('/control')?Promise.resolve({busy:false}):base(path,opts);
 await h.context.AutoSourcarrSetup.openRepairAI();await tick();
 assert.equal(h.nodes.get('setupTitle').textContent,'Repair and AI');
 assert(!h.nodes.get('setupCancel').hidden);assert(h.nodes.get('setupAccount-login'));
 h.context.AutoSourcarrSetup.cancel();await tick();
 assert(h.nodes.get('setupModal').hidden);
 assert(!h.calls.some(([p,o])=>o?.method==='POST'));
})().catch(e=>{console.error(e);process.exitCode=1;});
''')
