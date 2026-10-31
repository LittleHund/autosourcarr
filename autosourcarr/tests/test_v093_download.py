"""Download behavior with synthetic bytes; these fixtures are never release APKs."""
import hashlib
import json
from pathlib import Path
import os
import subprocess

import httpx
import pytest

from app import extension_download as apk


def publish_fixture(config, version='0.2.93', content=b'synthetic APK fixture'):
    directory = config / 'extension-downloads'
    directory.mkdir(exist_ok=True)
    digest = hashlib.sha256(content).hexdigest()
    path = directory / f'{version}-{digest}.apk'
    path.write_bytes(content)
    data = dict(app_version=version, extension_version=apk.extension_version(version),
                package=apk.PACKAGE, signature_verified=True, size=len(content), sha256=digest)
    (directory / 'current.json').write_text(json.dumps(data))
    return path, data


def test_absent_and_old_apk_are_never_current(tmp_path):
    assert not apk.apk_status(str(tmp_path), '0.2.93')['available']
    publish_fixture(tmp_path, '0.2.92')
    assert not apk.apk_status(str(tmp_path), '0.2.93')['available']
    with pytest.raises(apk.ApkUnavailable, match='No APK'):
        apk.apk_bytes(str(tmp_path), '0.2.93')


def test_download_returns_the_exact_hashed_bytes_and_rejects_corruption(tmp_path):
    path, _ = publish_fixture(tmp_path)
    assert apk.apk_status(str(tmp_path), '0.2.93')['available']
    assert apk.apk_bytes(str(tmp_path), '0.2.93') == b'synthetic APK fixture'
    path.write_bytes(b'x' * path.stat().st_size)
    with pytest.raises(apk.ApkUnavailable, match='integrity'):
        apk.apk_bytes(str(tmp_path), '0.2.93')


@pytest.mark.parametrize('key,value', [('package','other.package'),('extension_version','1.4.1'),('signature_verified',False),('size',True),('size',apk.MAX_APK_BYTES+1),('sha256','../secret')])
def test_publication_metadata_is_validated(tmp_path,key,value):
    _, data = publish_fixture(tmp_path)
    data[key] = value
    (tmp_path/'extension-downloads/current.json').write_text(json.dumps(data))
    assert not apk.apk_status(str(tmp_path), '0.2.93')['available']


def test_symlink_and_nonregular_files_are_rejected(tmp_path):
    path, _ = publish_fixture(tmp_path)
    other = tmp_path/'secret';other.write_bytes(path.read_bytes())
    path.unlink();path.symlink_to(other)
    assert not apk.apk_status(str(tmp_path), '0.2.93')['available']
    path.unlink();os.mkfifo(path)
    assert not apk.apk_status(str(tmp_path), '0.2.93')['available']


@pytest.mark.asyncio
async def test_apk_routes_preserve_auth_version_and_no_store(monkeypatch,tmp_path):
    from app import main
    monkeypatch.setattr(main.settings, 'admin_token', 'fixture-secret')
    monkeypatch.setattr(main.settings, 'settings_config_dir', str(tmp_path))
    publish_fixture(tmp_path, main.APP_VERSION, b"A" * 2048)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://test') as client:
        for suffix in ['', '/download']:
            assert (await client.get('/api/admin/extension-apk'+suffix)).status_code in {401,403}
        headers={'X-Admin-Token':'fixture-secret'}
        status=await client.get('/api/admin/extension-apk',headers=headers)
        assert status.json()['available'] and status.headers['cache-control']=='no-store'
        response=await client.get('/api/admin/extension-apk/download',headers=headers,params={'version':main.APP_VERSION})
        assert response.status_code==200 and response.content==b"A" * 2048
        assert response.headers['cache-control']=='no-store'
        assert 'content-encoding' not in response.headers, 'Already compressed APK bytes bypass synchronous JSON gzip'
        assert main.APP_VERSION in response.headers['content-disposition']
        assert response.headers['content-type']=='application/vnd.android.package-archive'
        stale=await client.get('/api/admin/extension-apk/download?version=0.0.1',headers=headers)
        assert stale.status_code==409


def test_sdk_publication_preserves_previous_pointer_after_failed_validation(tmp_path):
    """Exercise the shell publisher with controlled Android-tool responses."""
    script=Path(__file__).resolve().parents[2]/'extension/publish-apk.sh'
    bindir=tmp_path/'bin';bindir.mkdir()
    (bindir/'apksigner').write_text('#!/bin/sh\nexit "${SIGN_FAIL:-0}"\n')
    (bindir/'aapt').write_text("#!/bin/sh\nprintf \"package: name='eu.kanade.tachiyomi.extension.all.autosourcarr' versionName='%s'\\n\" \"${APK_VERSION:-1.4.2093}\"\n")
    for tool in bindir.iterdir():tool.chmod(0o755)
    content=tmp_path/'fixture.apk';content.write_bytes(b'synthetic SDK output')
    config=tmp_path/'config';env={**os.environ,'PATH':str(bindir)+os.pathsep+os.environ['PATH']}
    first=subprocess.run(['bash',str(script),str(content),str(config),'0.2.93'],env=env,capture_output=True,text=True)
    assert first.returncode==0,first.stderr
    pointer=(config/'extension-downloads/current.json').read_bytes()
    assert apk.apk_bytes(str(config),'0.2.93')==content.read_bytes()
    for failure in [{'APK_VERSION':'1.4.2092'},{'SIGN_FAIL':'1'}]:
        failed=subprocess.run(['bash',str(script),str(content),str(config),'0.2.93'],env={**env,**failure},capture_output=True,text=True)
        assert failed.returncode!=0
        assert (config/'extension-downloads/current.json').read_bytes()==pointer
