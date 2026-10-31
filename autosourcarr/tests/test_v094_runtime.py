"""Process lifecycle and authenticated bridge with controlled upstream fixtures."""
import asyncio
import os
import sys
import httpx
import pytest
from fastapi import FastAPI, Header, HTTPException
from fastapi.testclient import TestClient
from starlette.requests import Request
from starlette.websockets import WebSocketDisconnect
from app import source_engine as engine, source_webview as webview
from app.config import Settings


def test_integrated_url_overrides_previous_container_env():
    config=Settings(source_engine_mode='integrated',source_suwayomi_url='http://old:4567')
    assert config.effective_source_url=='http://127.0.0.1:4567'
    config.source_engine_mode='external'
    assert config.effective_source_url=='http://old:4567'


def test_child_env_is_isolated(tmp_path,monkeypatch):
    monkeypatch.setenv('ADMIN_TOKEN','backend-secret')
    (tmp_path/'source-host.env').write_text('EXTENSION_STORES=["https://example.test/index.pb"]\nBIND_IP=0.0.0.0\nBIND_PORT=9999\nAUTH_MODE=basic_auth\nKCEF_ENABLED=false\nLD_PRELOAD=evil\nPYTHONPATH=bad\nHOME=/bad\nTZ=Europe/Amsterdam\n')
    values=engine.child_environment(tmp_path)
    assert values['EXTENSION_STORES']=='["https://example.test/index.pb"]'
    assert values['BIND_IP']=='127.0.0.1' and values['BIND_PORT']=='4567'
    assert values['AUTH_MODE']=='none' and values['KCEF_ENABLED']=='true'
    assert values['TZ']=='Europe/Amsterdam'
    assert not {'ADMIN_TOKEN','LD_PRELOAD','PYTHONPATH'} & values.keys()
    assert values.get('HOME') == os.environ.get('HOME'), 'Retain the upstream image home, never source-env override'


def test_existing_source_owner_is_reused_without_rewriting_files(tmp_path,monkeypatch):
    if os.getuid()!=0:pytest.skip('Needs root for ownership fixture')
    data=tmp_path/'source';data.mkdir()
    original_stat=type(data).stat
    def owned_stat(path,*a,**kw):
        result=original_stat(path,*a,**kw)
        if path==data:
            fields=list(result);fields[4]=568;fields[5]=568;return os.stat_result(fields)
        return result
    monkeypatch.setattr(type(data),'stat',owned_stat)
    monkeypatch.setattr(os,'chown',lambda *a:pytest.fail('Existing source files must not be re-owned'))
    marker=data/'persistent.db';marker.write_bytes(b'unchanged');before=marker.stat()
    monkeypatch.setattr(engine.settings,'source_engine_uid',0)
    monkeypatch.setattr(engine.settings,'source_engine_gid',0)
    assert engine.child_identity(data)=={'user':568,'group':568,'extra_groups':[]}
    assert marker.read_bytes()==b'unchanged' and marker.stat().st_uid==before.st_uid


@pytest.mark.asyncio
async def test_real_child_starts_asynchronously_and_receives_graceful_stop(tmp_path,monkeypatch):
    script=tmp_path/'engine'
    script.write_text(f'#!{sys.executable}\nimport signal,time\nfrom pathlib import Path\np=Path(__file__).parent\ndef stop(*a):\n (p/"stopped").write_text("yes")\n raise SystemExit(0)\nsignal.signal(signal.SIGTERM,stop)\n(p/"started").write_text("yes")\nwhile True: time.sleep(.1)\n')
    script.chmod(0o755)
    monkeypatch.setattr(engine,'ENGINE_COMMAND',str(script));monkeypatch.setattr(engine,'ENGINE_DATA',tmp_path)
    monkeypatch.setattr(engine,'child_identity',lambda: {})
    monkeypatch.setattr(engine.settings,'source_engine_mode','integrated')
    monkeypatch.setattr(engine.settings,'settings_config_dir',tmp_path)
    from app import service
    monkeypatch.setattr(service.service,'schedule_source_reconciliation',lambda: True)
    class Probe:
        calls=0
        def __init__(self,**kw):pass
        async def __aenter__(self):return self
        async def __aexit__(self,*a):pass
        async def get(self,url):
            assert url.endswith('/api/v1/settings/about')
            Probe.calls+=1
            if Probe.calls==1:raise httpx.ConnectError('not started')
            return httpx.Response(200)
    monkeypatch.setattr(engine.httpx,'AsyncClient',Probe)
    runner=engine.SourceEngine();runner.start()
    assert runner.task is not None and runner.child is None
    async def wait_started():
        while not (tmp_path/'started').exists():await asyncio.sleep(.01)
    await asyncio.wait_for(wait_started(),3)
    child=runner.child
    await runner.stop()
    assert child.returncode==0 and (tmp_path/'stopped').read_text()=='yes'
    assert runner.task is None and runner.child is None and runner.state=='stopped'


@pytest.fixture
def bridge(monkeypatch):
    monkeypatch.setattr(webview.settings,'admin_token','secret')
    monkeypatch.setattr(webview.settings,'autosourcarr_public_url','https://app.example')
    monkeypatch.setattr(webview,'sessions',webview.WebviewSessions())
    async def guard(x_admin_token: str | None = Header(default=None)):
        if x_admin_token!='secret':raise HTTPException(401)
    app=FastAPI();app.include_router(webview.webview_router(guard))
    return TestClient(app,base_url='https://app.example')


def test_session_auth_cookie_scope_and_revocation(bridge,monkeypatch):
    assert bridge.get(webview.PATH).status_code==401
    assert bridge.post(webview.PATH+'/session').status_code==401
    assert bridge.post(webview.PATH+'/session',headers={'X-Admin-Token':'secret','Origin':'https://evil.example'}).status_code==403
    r=bridge.post(webview.PATH+'/session',headers={'X-Admin-Token':'secret','Origin':'https://app.example'})
    assert r.status_code==200 and r.json()['url']==webview.PATH
    cookie=r.headers['set-cookie']
    assert all(v in cookie for v in ['HttpOnly','Secure','SameSite=strict','Path='+webview.PATH])
    assert 'secret' not in r.text
    token=bridge.cookies.get(webview.COOKIE);assert webview.sessions.valid(token)
    monkeypatch.setattr(webview.settings,'admin_token','rotated')
    assert not webview.sessions.valid(token) and bridge.get(webview.PATH).status_code==401


def test_https_proxy_uses_explicit_public_origin(bridge):
    scope={'type':'http','scheme':'http','path':webview.PATH,'query_string':b'','server':('app.example',80),
           'headers':[(b'host',b'app.example'),(b'origin',b'https://app.example')]}
    assert webview.same_origin(Request(scope)) and webview.secure_connection(Request(scope))
    scope={**scope,'headers':[(b'host',b'evil.example'),(b'origin',b'https://app.example'),(b'x-forwarded-proto',b'https')]}
    assert not webview.same_origin(Request(scope))


def test_http_bridge_has_fixed_destination_and_no_forwarded_credentials(bridge,monkeypatch):
    bridge.post(webview.PATH+'/session',headers={'X-Admin-Token':'secret'})
    original=httpx.AsyncClient;seen=[]
    def upstream(request):
        seen.append(request)
        return httpx.Response(200,text='<html>WebView</html>',headers={'Content-Type':'text/html'})
    monkeypatch.setattr(webview.httpx,'AsyncClient',lambda **kw:original(transport=httpx.MockTransport(upstream),**kw))
    result=bridge.get(webview.PATH+'?url=https://evil.example')
    assert result.status_code==200 and 'WebView' in result.text
    assert str(seen[0].url)==webview.source_host.base_url+'/api/v1/webview'
    assert not {'x-admin-token','cookie','authorization'} & set(seen[0].headers)
    assert result.headers['cache-control']=='no-store'


def test_websocket_rejects_missing_session_and_wrong_origin(bridge):
    with pytest.raises(WebSocketDisconnect) as caught:
        with bridge.websocket_connect("wss://app.example"+webview.PATH,headers={'Origin':'https://app.example'}):pass
    assert caught.value.code==1008
    bridge.post(webview.PATH+'/session',headers={'X-Admin-Token':'secret'})
    with pytest.raises(WebSocketDisconnect) as caught:
        with bridge.websocket_connect("wss://app.example"+webview.PATH,headers={'Origin':'https://evil.example'}):pass
    assert caught.value.code==1008


def test_websocket_relays_text_binary_and_closes_remote(bridge,monkeypatch):
    bridge.post(webview.PATH+'/session',headers={'X-Admin-Token':'secret'})
    closed=[]
    class Remote:
        def __init__(self):self.q=asyncio.Queue()
        async def __aenter__(self):return self
        async def __aexit__(self,*a):closed.append(True)
        async def send(self,message):await self.q.put(message)
        def __aiter__(self):return self
        async def __anext__(self):return await self.q.get()
    def remote(url,**kw):
        assert url.endswith('/api/v1/webview') and '?' not in url
        return Remote()
    monkeypatch.setattr(webview,'connect',remote)
    with bridge.websocket_connect("wss://app.example"+webview.PATH,headers={'Origin':'https://app.example'}) as ws:
        ws.send_text('{"type":"loadUrl"}');assert 'loadUrl' in ws.receive_text()
        ws.send_bytes(b'frame');assert ws.receive_bytes()==b'frame'
    assert closed


def test_old_cached_engine_urls_move_but_source_websites_do_not(monkeypatch):
    from app.suwayomi import SuwayomiClient
    monkeypatch.setattr(engine.settings,'source_engine_mode','integrated')
    monkeypatch.setattr(engine.settings,'source_suwayomi_url','http://old:4567')
    client=object.__new__(SuwayomiClient);client.base_url='http://127.0.0.1:4567'
    assert client.runtime_asset_url('http://old:4567/api/v1/manga/10/thumbnail')=='http://127.0.0.1:4567/api/v1/manga/10/thumbnail'
    assert client.runtime_asset_url('https://source.example/api/images/1')=='https://source.example/api/images/1'
    assert client.runtime_asset_url('http://old:4567.evil.example/api/a')=='http://old:4567.evil.example/api/a'
