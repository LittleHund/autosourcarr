"""Raw downloads reuse TLS/public-address pinning without widening previews."""
import socket

import pytest

from app import source_repositories as repos


def fake_public_response(monkeypatch,body,*,encoding='identity'):
    calls=[]
    monkeypatch.setattr(repos,'_public_addresses',lambda host,port:[(socket.AF_INET,socket.SOCK_STREAM,6,'',('8.8.8.8',port))])
    class Response:
        status=200
        offset=0
        def getheader(self,key,default=None):
            return str(len(body)) if key=='Content-Length' else encoding if key=='Content-Encoding' else default
        def read1(self,size):
            value=body[self.offset:self.offset+size];self.offset+=len(value);return value
    class Connection:
        def __init__(self,host,**values):self.sock=None;assert host=='public.example'
        def request(self,method,path,headers):
            assert callable(self._create_connection)
            assert 'Cookie' not in headers and 'Authorization' not in headers
            calls.append((method,path,headers))
        def getresponse(self):return Response()
        def close(self):pass
    monkeypatch.setattr(repos.http.client,'HTTPSConnection',Connection)
    return calls


def test_raw_cap_is_explicit_while_manifest_preview_keeps_three_mebibytes(monkeypatch):
    data=b'x'*(4*1024*1024)
    calls=fake_public_response(monkeypatch,data)
    with pytest.raises(ValueError):repos.fetch_manifest('https://public.example/code.zip')
    value,final= repos.fetch_manifest('https://public.example/code.zip',max_bytes=5*1024*1024)
    assert value==data and final=='https://public.example/code.zip'
    assert len(calls)==2 and repos.MAX_MANIFEST_BYTES==3*1024*1024


def test_raw_fetch_never_transparently_expands_encoded_transport(monkeypatch):
    fake_public_response(monkeypatch,b'compressed',encoding='gzip')
    with pytest.raises(ValueError,match='compressed'):
        repos.fetch_manifest('https://public.example/code.zip',max_bytes=5*1024*1024)


@pytest.mark.parametrize('limit',[True,0,-1,129*1024*1024,'large'])
def test_download_budget_is_bounded_before_any_network_work(monkeypatch,limit):
    monkeypatch.setattr(repos,'_public_addresses',lambda *args:pytest.fail('Invalid budget must not resolve DNS'))
    with pytest.raises(ValueError):repos.fetch_manifest('https://public.example/code.zip',max_bytes=limit)
