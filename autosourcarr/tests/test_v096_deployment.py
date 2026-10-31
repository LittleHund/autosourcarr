"""Automatic build wiring and startup recovery without a Docker daemon."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]


@pytest.mark.parametrize('name', ['docker-compose.yml','docker-compose.truenas.yml','examples/manga-cluster-autosourcarr-services.yml','compose-env-file.yml'])
def test_deployment_automatically_builds_current_project(name):
    config=yaml.safe_load((ROOT/name).read_text())
    service=config['services']['autosourcarr']
    assert service['image']=='autosourcarr:local', 'No per-release Compose image edits'
    assert service['pull_policy']=='build', 'An already cached local image must not suppress an update'
    path=service['build']['context']
    if name=='docker-compose.yml':
        assert (ROOT/path).resolve()==ROOT/'autosourcarr'
    else:
        assert path=='/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/autosourcarr'
    assert service.get('command') is None and service.get('entrypoint') is None
    assert not service.get('privileged',False)
    assert not any('docker.sock' in str(v) for v in service['volumes'])
    assert 'autosourcarr-source-host' not in config['services']
    assert any(str(v).endswith(':/home/suwayomi/.local/share/Tachidesk') for v in service['volumes'])


def test_version_only_updates_do_not_invalidate_dependency_layer():
    lines=(ROOT/'autosourcarr/Dockerfile').read_text().splitlines()
    install=next(i for i,line in enumerate(lines) if line.startswith('RUN pip install'))
    before='\n'.join(lines[:install])
    assert 'COPY requirements.txt ./' in before
    assert 'COPY VERSION' not in before and 'COPY app' not in before
    assert 'COPY VERSION start-truenas.sh ./' in '\n'.join(lines[install+1:])
    assert 'CMD ["/bin/sh","/app/start-truenas.sh"]' in lines


def test_optional_builder_tags_the_image_used_by_deployment(tmp_path):
    binary=tmp_path/'docker'
    log=tmp_path/'docker.json'
    binary.write_text(f'#!{sys.executable}\nimport json,os,sys\nopen(os.environ["FIXTURE_DOCKER_LOG"],"w").write(json.dumps(sys.argv[1:]))\n')
    binary.chmod(0o755)
    env={**os.environ,'PATH':str(tmp_path)+os.pathsep+os.environ['PATH'],'FIXTURE_DOCKER_LOG':str(log)}
    result=subprocess.run(['bash',str(ROOT/'scripts/build-integrated-image.sh')],env=env,capture_output=True,text=True)
    assert result.returncode==0, result.stderr
    args=json.loads(log.read_text())
    assert args==['build','-t','autosourcarr:local','-t','autosourcarr:'+ (ROOT/'VERSION').read_text().strip(),str(ROOT/'autosourcarr')]
    assert 'does not switch a running container' in result.stdout


def test_wrong_image_ui_reaches_real_application_lifespan(tmp_path):
    """Run the real wrapper and ASGI app; source networking uses a rejecting transport."""
    app=tmp_path/'application';app.mkdir()
    shutil.copy2(ROOT/'autosourcarr/start-truenas.sh',app/'start-truenas.sh')
    shutil.copytree(ROOT/'autosourcarr/app',app/'app',ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copy2(ROOT/'autosourcarr/requirements.txt',app/'requirements.txt')
    shutil.copy2(ROOT/'autosourcarr/VERSION',app/'VERSION')
    deps=tmp_path/'fixture-deps';deps.mkdir()
    # Use the test interpreter's installed pinned dependencies; no network installs.
    (app/'app/dependency_cache.py').write_text('import os; print(os.environ["FIXTURE_BOOT_MODULE"])')
    (deps/'uvicorn.py').write_text('''import httpx,json
from fastapi.testclient import TestClient
from app import main
async def reject(self,request):
    raise httpx.ConnectError("Fixture has no source network",request=request)
httpx.AsyncHTTPTransport.handle_async_request=reject
with TestClient(main.app) as c:
    assert c.get('/api/v1/live').status_code==200
    response=c.get('/api/admin/source-engine')
    assert response.status_code==200,response.text
    status=response.json()
    assert status['state']=='setup-required',status
    assert status['mode']=='integrated' and not status['runtime_available'],status
    assert c.get('/').status_code==200
    assert c.get('/api/admin/ui-preferences').status_code==200
    print('BOOT_FIXTURE='+json.dumps(status))
''')
    env={**os.environ,'PATH':str(Path(sys.executable).parent)+os.pathsep+os.environ['PATH'],
         'AUTOSOURCARR_APP_DIR':str(app),'AUTOSOURCARR_DEPS_DIR':str(tmp_path/'cache'),
         'FIXTURE_BOOT_MODULE':str(deps),'SOURCE_ENGINE_MODE':'integrated','SOURCE_SUWAYOMI_URL':'http://removed.invalid:4567',
         'DATA_DIR':str(tmp_path/'data'),'DATABASE_DIR':str(tmp_path/'data'),'SETTINGS_CONFIG_DIR':str(tmp_path/'config'),
         'MAIN_SUWAYOMI_URL':'','TRANSLATION_API_URL':'','ADMIN_TOKEN':'','METADATA_IDENTITY_ENABLED':'false',
         'AUTOMATIC_DUPLICATE_MATCHING':'false','CONTINUOUS_CATALOG_DISCOVERY':'false','BACKGROUND_DEDUP_ON_STARTUP':'false'}
    if Path('/home/suwayomi/startup_script.sh').exists():
        pytest.skip('This fixture exercises a Python-only host')
    result=subprocess.run(['sh',str(app/'start-truenas.sh')],cwd=app,env=env,capture_output=True,text=True,timeout=25)
    assert result.returncode==0, result.stdout+'\n'+result.stderr
    assert 'BOOT_FIXTURE=' in result.stdout
    assert 'starting the UI in setup-required mode' in result.stderr
