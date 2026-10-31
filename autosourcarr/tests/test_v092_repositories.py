from __future__ import annotations

import hashlib
import json
import socket
from concurrent.futures import ThreadPoolExecutor

import httpx
import pytest

from app import source_repositories as repos
from app.env_settings import update_env
from app.task_registry import TaskRegistry


@pytest.fixture
def env_file(tmp_path):
    path = tmp_path / 'source-host.env'
    path.write_text('# Keep my source settings\nEXTENSION_STORES=["https://existing.example/repo.json"]\nBIND_PORT=4567\n')
    path.chmod(0o640)
    return path


def test_repository_changes_preserve_other_settings_and_permissions(env_file):
    snapshot = repos.repository_snapshot(env_file)
    added = repos.change_repository(env_file, repos.KEIYOUSHI_URL, snapshot['revision'])
    assert [x['url'] for x in added['repositories']] == ['https://existing.example/repo.json', repos.KEIYOUSHI_URL]
    assert added['restart_required']
    assert '# Keep my source settings' in env_file.read_text()
    assert 'BIND_PORT=4567' in env_file.read_text()
    assert env_file.stat().st_mode & 0o777 == 0o640
    same = repos.change_repository(env_file, repos.KEIYOUSHI_URL, added['revision'])
    assert len(same['repositories']) == 2
    removed = repos.change_repository(env_file, repos.KEIYOUSHI_URL, same['revision'], remove=True)
    assert [r['url'] for r in removed['repositories']] == ['https://existing.example/repo.json']


def test_stale_repository_and_settings_edit_cannot_overwrite_new_config(env_file):
    before = repos.repository_snapshot(env_file)
    repos.change_repository(env_file, repos.KEIYOUSHI_URL, before['revision'])
    with pytest.raises(repos.RepositoryConflict):
        repos.change_repository(env_file, 'https://other.example/repo.json', before['revision'])
    with pytest.raises(ValueError, match='Settings changed'):
        update_env(env_file, {'EXTENSION_STORES':'[]'}, revision=before['revision'])
    assert repos.KEIYOUSHI_URL in env_file.read_text()


def test_simultaneous_repo_writes_have_one_winner(env_file):
    revision = repos.repository_snapshot(env_file)['revision']
    def add(url):
        try:
            repos.change_repository(env_file, url, revision)
            return True
        except repos.RepositoryConflict:
            return False
    with ThreadPoolExecutor(max_workers=2) as pool:
        result = list(pool.map(add, ['https://one.example/repo.json', 'https://two.example/repo.json']))
    assert sum(result) == 1
    assert len(repos.repository_snapshot(env_file)['repositories']) == 2


@pytest.mark.parametrize('value', ['broken', '{}', '[1]', '["https://good.example/repo.json"]\nINVALID'])
def test_malformed_repository_config_is_never_overwritten(env_file, value):
    env_file.write_text('EXTENSION_STORES='+value+'\n')
    original = env_file.read_bytes()
    with pytest.raises(ValueError):
        repos.change_repository(env_file, repos.KEIYOUSHI_URL, hashlib.sha256(original).hexdigest())
    assert env_file.read_bytes() == original


def test_missing_repo_mount_is_not_created(tmp_path):
    path = tmp_path / 'missing.env'
    with pytest.raises(FileNotFoundError):
        repos.repository_snapshot(path)
    assert not path.exists()


def test_preview_is_read_only_and_counts_only_known_extensions(monkeypatch, env_file):
    original = env_file.read_bytes()
    monkeypatch.setattr(repos, 'fetch_manifest', lambda url: (json.dumps({'meta':{'name':'Keiyoushi'},'index_v2':'index.pb'}).encode(), url))
    result = repos.preview_repository(repos.KEIYOUSHI_URL)
    assert result['name'] == 'Keiyoushi'
    assert result['extension_count'] is None
    assert env_file.read_bytes() == original
    result = repos.parse_manifest(b'{"name":"Fixture","signingKey":"key","extensionList":{"extensions":[{},{}]}}')
    assert result['extension_count'] == 2


def test_modern_protobuf_manifest():
    # name(1), signingKey(3), extensionList(101) with two extension messages(1).
    result = repos.parse_manifest(b'\x0a\x07Fixture\x1a\x03key\xaa\x06\x04\x0a\x00\x0a\x00')
    assert result == {'name':'Fixture','format':'Suwayomi repository protobuf','extension_count':2}


@pytest.mark.parametrize('body', [b'', b'<html>Error</html>', b'{}', b'[]', b'\x0a\xff', b'\x0a\x04Test', b'x'*(repos.MAX_MANIFEST_BYTES+1)])
def test_invalid_manifest_is_not_presented_as_compatible(body):
    with pytest.raises((ValueError, UnicodeError)):
        repos.parse_manifest(body)


@pytest.mark.parametrize('url', ['http://example.com/repo.json','https://u:p@example.com/repo.json','file:///etc/passwd','https://example.com:8443/repo.json','https://example.com/#fragment','https://example.com/\nX:1'])
def test_preview_rejects_unsafe_url_forms(url):
    with pytest.raises(ValueError):
        repos.normalize_repository_url(url)


@pytest.mark.parametrize('address', ['127.0.0.1','10.0.0.1','169.254.169.254','::1','fe80::1','0.0.0.0'])
def test_private_dns_and_redirect_targets_rejected_before_connection(monkeypatch, address):
    monkeypatch.setattr(repos.socket, 'getaddrinfo', lambda *a,**k:[(socket.AF_INET,socket.SOCK_STREAM,6,'',(address,443))])
    with pytest.raises(ValueError, match='public HTTPS'):
        repos.fetch_manifest('https://public-looking.example/repo.json')


def test_dns_rebinding_and_redirects_are_pinned_and_revalidated(monkeypatch):
    calls=[]
    def dns(host,port,**kw):
        calls.append(host)
        return [(socket.AF_INET,socket.SOCK_STREAM,6,'',('93.184.216.34' if host=='public.example' else '127.0.0.1',port))]
    monkeypatch.setattr(repos.socket,'getaddrinfo',dns)
    class Response:
        status=302
        def getheader(self,key,default=None): return 'https://private.example/repo.json' if key=='Location' else default
    class Connection:
        def __init__(self,*args,**kwargs): self.sock=None
        def request(self,*args,**kwargs): assert callable(self._create_connection)
        def getresponse(self): return Response()
        def close(self): pass
    monkeypatch.setattr(repos.http.client,'HTTPSConnection',Connection)
    with pytest.raises(ValueError,match='public HTTPS'):
        repos.fetch_manifest('https://public.example/repo.json')
    assert calls==['public.example','private.example']


def test_task_origin_is_explicit_and_retained_across_waiting_and_completion():
    registry=TaskRegistry()
    manual=registry.create('Identical display name',origin='manual')
    automatic=registry.create('Identical display name')
    registry.mark_waiting(manual,message='Waiting for a related change')
    registry.complete(automatic)
    records={t['id']:t for t in registry.snapshot()['items']}
    assert records[manual]['origin']=='manual'
    assert records[automatic]['origin']=='automatic'
    with pytest.raises(ValueError): registry.create('bad',origin='inferred')


@pytest.mark.asyncio
async def test_repository_routes_enforce_admin_guard(monkeypatch):
    from app import main
    monkeypatch.setattr(main.settings, 'admin_token', 'fixture-secret')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://test') as client:
        for method,path,body in [('GET','',None),('POST','/preview',{'url':repos.KEIYOUSHI_URL}),('POST','',{'url':repos.KEIYOUSHI_URL,'revision':'0'*64}),('DELETE','',{'url':repos.KEIYOUSHI_URL,'revision':'0'*64})]:
            response=await client.request(method,'/api/admin/source-repositories'+path,json=body)
            assert response.status_code in {401,403}


def test_gzipped_keiyoushi_style_manifest_is_supported():
    import gzip
    raw = b'\x0a\x07Fixture\x1a\x03key\xaa\x06\x04\x0a\x00\x0a\x00'
    assert repos.parse_manifest(gzip.compress(raw))['extension_count'] == 2


def test_gzip_expansion_is_bounded_and_truncation_rejected():
    import gzip
    with pytest.raises(ValueError, match='exceeds'):
        repos.parse_manifest(gzip.compress(b'x'*(repos.MAX_MANIFEST_BYTES+100)))
    with pytest.raises(ValueError, match='truncated'):
        repos.parse_manifest(gzip.compress(b'valid-sized contents')[:-5])


def test_validated_address_fallback_keeps_tls_hostname(monkeypatch):
    attempted=[]
    monkeypatch.setattr(repos,'_public_addresses',lambda h,p:[(socket.AF_INET,socket.SOCK_STREAM,6,'',(ip,p)) for ip in ['93.184.216.34','93.184.216.35']])
    class Sock:
        def settimeout(self,value): pass
        def connect(self,address):
            attempted.append(address[0])
            if len(attempted)==1: raise OSError('First address unavailable')
        def close(self): pass
        def shutdown(self,*a): pass
    monkeypatch.setattr(repos.socket,'socket',lambda *a:Sock())
    class Response:
        status=200
        def getheader(self,key,default=None): return default
        def read1(self,size): return b''
    class Connection:
        def __init__(self,host,**kwargs):
            assert host=='public.example'
            self.sock=None
        def request(self,*a,**kw): self.sock=self._create_connection(('public.example',443))
        def getresponse(self): return Response()
        def close(self): pass
    monkeypatch.setattr(repos.http.client,'HTTPSConnection',Connection)
    repos.fetch_manifest('https://public.example/repo.json')
    assert attempted==['93.184.216.34','93.184.216.35']


@pytest.mark.asyncio
async def test_timed_out_previews_keep_bounded_admission_until_real_workers_finish(monkeypatch):
    import asyncio
    import threading
    release=threading.Event()
    started=[threading.Event(),threading.Event()]
    lock=threading.Lock()
    jobs=[]
    def blocked(url):
        with lock:
            slot=len(jobs);jobs.append(url);started[slot].set()
        release.wait(2)
        return {'name':'done'}
    monkeypatch.setattr(repos,'preview_repository',blocked)
    first=asyncio.create_task(repos.preview_repository_async('https://one.example/repo.json',timeout=.03))
    second=asyncio.create_task(repos.preview_repository_async('https://two.example/repo.json',timeout=.03))
    try:
        for event in started:
            assert await asyncio.to_thread(event.wait,1)
        for task in (first,second):
            with pytest.raises(TimeoutError): await task
        with pytest.raises(ValueError,match='still running'):
            await repos.preview_repository_async('https://three.example/repo.json')
        assert len(jobs)==2
    finally:
        release.set()
        await asyncio.gather(first,second,return_exceptions=True)
        # Join the actual dedicated workers before the next regression.
        await asyncio.get_running_loop().run_in_executor(repos._PREVIEW_EXECUTOR,lambda:None)
        await asyncio.sleep(0)
