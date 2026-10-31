"""Exercise web-managed setup/account through API and real owned subprocesses.

Docker commands are replaced at the executable boundary; no real account is used.
"""
import asyncio
import fcntl
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
from fastapi import FastAPI
import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'repair-worker'))
from control import Control, device_code, DEVICE_URL
import server as worker_server
from app import repair_connection as connection
from app import source_maintenance as maintenance
from app.config import settings


@pytest.mark.parametrize('code', ['ABCD-12345', '1234-56789'])
def test_device_prompt_only_exports_official_code(code):
    text=f'\x1b[94m{DEVICE_URL}\x1b[0m\n2. Enter this one-time code (expires in 15 minutes)\n \x1b[94m{code}\x1b[0m\n access_token=NEVER-EXPORT'
    assert device_code(text)==code
    assert device_code(text.replace('auth.openai.com','evil.example'))==''
    assert device_code(text.replace('/codex/device','/codex/device?token=private'))==''
    assert device_code('access_token='+code)==''


def runner(tmp):
    for folder in ('auth','locks','jobs'): (tmp/folder).mkdir(exist_ok=True)
    return SimpleNamespace(root=tmp, namespace='testweb', host=lambda p:'/host/repair/'+str(p.relative_to(tmp)),
        base_args=lambda name,memory='2g':['docker','run','--name',name,'--network','isolated-jobs','-e','HTTPS_PROXY=http://egress:8080'])


@pytest.fixture
def fake_docker(tmp_path, monkeypatch):
    executable=tmp_path/'docker'
    executable.write_text('''#!/usr/bin/env python3
import os,sys,time,json
from pathlib import Path
root=Path(os.environ['FAKE_DOCKER_ROOT']);args=sys.argv[1:]
with (root/'commands').open('a') as f:f.write(json.dumps(args)+'\\n')
if args[:2]==['image','inspect']:
 sys.exit(0 if (root/'prepared').exists() else 1)
if args[0] in ('pull','build'):
 (root/'prepared').touch();sys.exit(0)
if args[0]=='run':
 mount=args[args.index('--mount')+1]
 relative=next(part[4:] for part in mount.split(',') if part.startswith('src=')).removeprefix('/host/repair/')
 auth=root/relative
 if 'logout' in args:
  (auth/'auth.json').unlink(missing_ok=True);sys.exit(0)
 if 'status' in args:
  print('Logged in using ChatGPT' if (auth/'auth.json').exists() else 'Not logged in');sys.exit(0 if (auth/'auth.json').exists() else 1)
 print('https://auth.openai.com/codex/device\\n2. Enter this one-time code (expires in 15 minutes)\\nABCD-12345',flush=True)
 if (root/'fail-login').exists():
  print('access_token=TOPSECRET cookie=SECRET',flush=True);sys.exit(3)
 while not (root/'finish-login').exists():time.sleep(0.02)
 (auth/'auth.json').write_text(json.dumps({'auth_mode':'chatgpt','tokens':{'id_token':'private-id','access_token':'private-access','refresh_token':'private-refresh'}}));sys.exit(0)
if args[0]=='rm':sys.exit(0)
sys.exit(1)
''')
    executable.chmod(0o755)
    monkeypatch.setenv('PATH',str(tmp_path)+os.pathsep+os.environ['PATH'])
    monkeypatch.setenv('FAKE_DOCKER_ROOT',str(tmp_path))
    return tmp_path


async def wait_code(control):
    async with asyncio.timeout(5):
        while not control.snapshot()['device_code']: await asyncio.sleep(.01)


@pytest.mark.asyncio
async def test_prepare_login_refresh_logout_and_secret_redaction(fake_docker):
    c=Control(runner(fake_docker));c.start('prepare');await c.task
    assert c.snapshot()['images_ready'] and c.snapshot()['state']=='completed'
    c.start('login');await wait_code(c)
    assert c.snapshot()['verification_url']==DEVICE_URL
    # Code survives page refresh: stored by owned worker action, not request lifetime.
    assert c.snapshot()['device_code']=='ABCD-12345'
    with pytest.raises(RuntimeError):c.start('logout')
    (fake_docker/'finish-login').touch();await c.task
    assert c.snapshot()['account']=='saved-login' and not c.snapshot()['device_code']
    c.start('login-status');await c.task
    assert c.snapshot()['account']=='saved-login'
    c.start('logout');await c.task
    assert c.snapshot()['account']=='signed-out' and not c.snapshot()['auth_saved']
    assert (fake_docker/'locks/account.lock').exists()
    commands=[json.loads(x) for x in (fake_docker/'commands').read_text().splitlines()]
    assert any(x[0]=='build' and x[-1]=='/worker/toolchain' for x in commands)
    for command in (x for x in commands if x[0]=='run'):
        assert '--log-driver' in command and 'none' in command
        assert not any('docker.sock' in x or 'live' in x for x in command)
        assert any('dst=/home/node/.codex' in x for x in command)
    assert 'private auth' not in json.dumps(c.snapshot())


@pytest.mark.asyncio
async def test_cancel_drains_login_and_releases_shared_lock(fake_docker):
    c=Control(runner(fake_docker));(fake_docker/'prepared').touch()
    c.start('login');await wait_code(c)
    with (fake_docker/'locks/account.lock').open('a') as f:
        with pytest.raises(BlockingIOError): fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)
    await c.cancel_login()
    assert not c.busy and not c.snapshot()['device_code'] and c.state['state']=='cancelled'
    commands=(fake_docker/'commands').read_text()
    assert '"rm", "-f", "asr-testweb-web-account"' in commands
    with (fake_docker/'locks/account.lock').open('a') as f: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)


@pytest.mark.asyncio
async def test_immediate_cancel_releases_lock(fake_docker):
    c=Control(runner(fake_docker));c.start('login')
    # Let a scheduled task start so cancellation has the same route ordering as HTTP.
    await asyncio.sleep(0)
    await c.cancel_login()
    with (fake_docker/'locks/account.lock').open('a') as f: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)


@pytest.mark.asyncio
async def test_login_failure_never_exports_process_output(fake_docker):
    c=Control(runner(fake_docker));(fake_docker/'prepared').touch();(fake_docker/'fail-login').touch()
    c.start('login');await c.task
    data=c.snapshot();assert data['state']=='failed' and not data['device_code']
    assert 'TOPSECRET' not in json.dumps(data) and 'SECRET' not in json.dumps(data)


@pytest.mark.asyncio
async def test_worker_rejects_account_change_during_queued_job(tmp_path, monkeypatch):
    c=Control(runner(tmp_path));monkeypatch.setattr(worker_server,'control',c)
    monkeypatch.setattr(worker_server,'runner',c.runner)
    monkeypatch.setattr(worker_server,'pending',{'queued'})
    monkeypatch.setenv('REPAIR_TOKEN','x'*64)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
        assert (await client.post('/control/login')).status_code==401
        r=await client.post('/control/logout',headers={'Authorization':'Bearer '+'x'*64})
        assert r.status_code==409 and r.headers['cache-control']=='no-store'
        worker_server.pending.clear();c.task=asyncio.create_task(asyncio.sleep(1))
        r=await client.post('/jobs',headers={'Authorization':'Bearer '+'x'*64},json={})
        assert r.status_code==409
        c.task.cancel();await asyncio.gather(c.task,return_exceptions=True)


def compose():
    return {'name':'existing', 'services':{
        'autosourcarr':{'image':'old','ports':['30060:8282'],'volumes':['/host/config:/config','/host/data:/data'],'environment':{'KEEP':'true'},'networks':{'custom':{'aliases':['first']}}},
        'private-autosourcarr':{'image':'old','ports':['30061:8282'],'volumes':['/host/private-config:/config','/host/private-data:/data']},
        'suwayomi':{'image':'reader','ports':['30058:30058']}},'networks':{'custom':{}}}


def setup_body():
    return {'compose':yaml.safe_dump(compose()),'project_path':'/host/project','data_path':'/host/repair-data','services':['autosourcarr','private-autosourcarr']}


def test_compose_preview_keeps_instances_and_shared_worker():
    first=connection.preview(setup_body());parsed=yaml.safe_load(first['compose'])
    for name in ('autosourcarr','private-autosourcarr'):
        assert parsed['services'][name]['volumes']==compose()['services'][name]['volumes']
        assert parsed['services'][name]['ports']==compose()['services'][name]['ports']
        assert 'repair-jobs' not in parsed['services'][name]['networks']
    assert parsed['services']['suwayomi']==compose()['services']['suwayomi']
    assert parsed['services']['autosourcarr']['networks']['custom']['aliases']==['first']
    worker=parsed['services']['autosourcarr-repair'];assert len(worker['environment']['REPAIR_TOKEN'])==64
    assert worker['volumes']==['/var/run/docker.sock:/var/run/docker.sock','/host/repair-data:/repairs']
    assert 'ports' not in worker and parsed['networks']['repair-jobs']['internal']
    second=connection.preview({**setup_body(),'compose':first['compose']})
    assert yaml.safe_load(second['compose'])==parsed,'repeated setup preserves token and data'


@pytest.mark.parametrize('change',[
 {'data_path':'/host/data/repairs'}, {'data_path':'/host/project/subfolder'},
 {'project_path':'/host/$BAD'}, {'services':['suwayomi']},
 {'compose':'services: {}\nservices: {}'}, {'compose':'a: &a [*a]'},
])
def test_compose_preview_rejects_conflicts(change):
    with pytest.raises(ValueError): connection.preview({**setup_body(),**change})


@pytest.mark.asyncio
async def test_legacy_connection_file_is_retained_but_web_cannot_replace_it(tmp_path,monkeypatch):
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path)
    monkeypatch.setattr(settings,'app_services_mode','external')
    monkeypatch.setattr(settings,'repair_worker_url','http://env:8766')
    monkeypatch.setattr(settings,'repair_worker_token','a'*64)
    before=json.dumps({'url':'http://legacy:8766','token':'b'*64})
    connection.connection_path().write_text(before)
    connection.restore_connection()
    assert settings.repair_worker_url=='http://legacy:8766'
    worker=AsyncMock();app=FastAPI();app.include_router(connection.connection_router(worker,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        state=(await client.get('/connection')).json();assert 'b'*64 not in json.dumps(state)
        r=await client.put('/connection',json={'revision':state['revision'],'url':'http://new:8766','token':'c'*64})
        assert r.status_code==410 and r.headers['cache-control']=='no-store'
        assert connection.connection_path().read_text()==before
        assert settings.repair_worker_url=='http://legacy:8766'
        worker.assert_not_called()
        r=await client.post('/control/login',headers={'Origin':'https://evil.example'})
        assert r.status_code==403


@pytest.mark.parametrize('url,token',[('http://a:1/path','a'*64),('http://user:pass@a','a'*64),('http://a/?token=secret','a'*64),('http://a','bad\nheader')])
def test_connection_rejects_secret_urls_and_header_injection(url,token):
    with pytest.raises(ValueError):connection.validated_connection(url,token)


@pytest.mark.asyncio
async def test_overlapping_cancel_and_shutdown_drain_owned_cleanup(fake_docker):
    c=Control(runner(fake_docker));(fake_docker/'prepared').touch()
    c.start('login');await wait_code(c)
    original=c._remove_container
    entered=asyncio.Event();release=asyncio.Event()
    async def slow_cleanup():
        entered.set();await release.wait();await original()
    c._remove_container=slow_cleanup
    first=asyncio.create_task(c.cancel_login());await entered.wait()
    second=asyncio.create_task(c.cancel_login());shutdown=asyncio.create_task(c.close())
    await asyncio.sleep(.01)
    assert not first.done() and not second.done() and not shutdown.done()
    with (fake_docker/'locks/account.lock').open('a') as f:
        with pytest.raises(BlockingIOError): fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)
    release.set();await asyncio.gather(first,second,shutdown)
    assert not c.busy
    with (fake_docker/'locks/account.lock').open('a') as f: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)


@pytest.mark.asyncio
async def test_uncertain_docker_cleanup_keeps_lock_and_blocks_new_action(fake_docker):
    c=Control(runner(fake_docker));(fake_docker/'prepared').touch()
    c.start('login');await wait_code(c)
    original=c.command
    async def unavailable(args,**kwargs):
        if args[1] in {'rm','ps'}: return 1,'daemon unavailable'
        return await original(args,**kwargs)
    c.command=unavailable
    await c.cancel_login()
    assert c.state['state']=='failed'
    with pytest.raises(RuntimeError):c.start('login')
    with (fake_docker/'locks/account.lock').open('a') as f:
        with pytest.raises(BlockingIOError): fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)
    c.command=original
    c.start('retry-cleanup');await c.task
    assert not c.snapshot()['blocked'] and c.state['state']=='completed'
    with (fake_docker/'locks/account.lock').open('a') as f:fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)


@pytest.mark.asyncio
async def test_retired_connection_does_not_consume_secrets_or_begin_a_commit(tmp_path,monkeypatch):
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path)
    monkeypatch.setattr(settings,'repair_worker_url','http://old:8766')
    monkeypatch.setattr(settings,'repair_worker_token','a'*64)
    def forbidden_commit(*args): raise AssertionError('Retired setup must not begin a write')
    monkeypatch.setattr(maintenance,'atomic',forbidden_commit)
    async def private_body():
        raise AssertionError('Retired setup must reject before consuming secrets')
        yield b''
    app=FastAPI();app.include_router(connection.connection_router(AsyncMock(),asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        response=await client.put('/connection',content=private_body())
        assert response.status_code==410
        assert settings.repair_worker_url=='http://old:8766'
        assert not connection.connection_path().exists()


@pytest.mark.asyncio
async def test_cancel_before_account_task_starts_closes_lock(fake_docker):
    c=Control(runner(fake_docker));c.start('login');await c.cancel_login();await asyncio.sleep(0)
    assert c.state['state']=='cancelled'
    with (fake_docker/'locks/account.lock').open('a') as f:fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)
