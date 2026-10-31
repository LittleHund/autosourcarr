"""Run the production shell bootstrap with image paths replaced by owned fixtures.

The real host setpriv/kernel proves inherited protection and exec ownership. This
host already has NoNewPrivs=1; a transition from 0 needs an actual image/host run.
"""
import json
import os
from pathlib import Path
import subprocess
import sys

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
SETPRIV = Path('/usr/bin/setpriv')


def bootstrap(tmp_path, *, refusal=False):
    runtime = tmp_path / 'runtime-python'
    runtime.write_text(f'#!{sys.executable}\n' + '''import json, os, subprocess, sys
def flag():
    return next(line.split(':', 1)[1].strip() for line in open('/proc/self/status') if line.startswith('NoNewPrivs:'))
child = subprocess.check_output([sys.executable, '-c', "print(next(line.split(':', 1)[1].strip() for line in open('/proc/self/status') if line.startswith('NoNewPrivs:')))"]).decode().strip()
print(json.dumps({'pid': os.getpid(), 'main': flag(), 'child': child, 'args': sys.argv[1:]}), flush=True)
raise SystemExit(23)
''')
    runtime.chmod(0o755)
    engine = tmp_path / 'engine-startup'; engine.touch()
    utility = tmp_path / 'setpriv'
    utility.write_text(f'#!{sys.executable}\n' + '''import json, os, sys
with open(os.environ['FIXTURE_SECURITY_LOG'], 'w') as output:
    json.dump(sys.argv[1:], output)
''' + ("print('fixture protection could not be enabled', file=sys.stderr)\nraise SystemExit(91)\n" if refusal else "os.execv('/usr/bin/setpriv', ['/usr/bin/setpriv', *sys.argv[1:]])\n"))
    utility.chmod(0o755)
    text = (ROOT / 'autosourcarr/start-truenas.sh').read_text()
    # Only image/binary locations change; production shell control flow and flags
    # remain intact. No real app, mounted configuration, network or repair runs.
    text = text.replace('/opt/autosourcarr-venv/bin/python', str(runtime))
    text = text.replace('/home/suwayomi/startup_script.sh', str(engine))
    text = text.replace('/usr/bin/setpriv', str(utility))
    script = tmp_path / 'start-truenas.sh'; script.write_text(text)
    return script, runtime


@pytest.mark.skipif(not SETPRIV.is_file(), reason='Linux util-linux setpriv unavailable')
@pytest.mark.parametrize('outer_flag', [False, True])
def test_bootstrap_enables_protection_before_exec_and_owned_children(tmp_path, outer_flag):
    script, runtime = bootstrap(tmp_path)
    env = {**os.environ, 'SOURCE_ENGINE_MODE': 'integrated',
           'AUTOSOURCARR_PORT_INTERNAL': '8282', 'FIXTURE_SECURITY_LOG': str(tmp_path / 'security.json')}
    command = ['sh', str(script)]
    if outer_flag:
        command = [str(SETPRIV), '--no-new-privs', '--', *command]
    process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    stdout, stderr = process.communicate(timeout=10)
    assert process.returncode == 23, stderr
    flags = json.loads((tmp_path / 'security.json').read_text())
    assert flags[:3] == ['--no-new-privs', '--', str(runtime)]
    result = json.loads(stdout)
    assert result['pid'] == process.pid, 'Bootstrap must exec, retaining container signal ownership'
    assert result['main'] == result['child'] == '1'
    assert result['args'] == ['-m', 'uvicorn', 'app.main:app', '--host', '0.0.0.0',
                              '--port', '8282', '--proxy-headers']


def test_bootstrap_refuses_to_start_when_protection_cannot_be_enabled(tmp_path):
    script, _ = bootstrap(tmp_path, refusal=True)
    result = subprocess.run(['sh', str(script)], env={**os.environ, 'SOURCE_ENGINE_MODE': 'integrated',
        'FIXTURE_SECURITY_LOG': str(tmp_path / 'security.json')}, capture_output=True, text=True, timeout=10)
    assert result.returncode == 91
    assert 'could not be enabled' in result.stderr
    assert not result.stdout, 'The application must never start after failed protection'


@pytest.mark.parametrize('name', ['docker-compose.yml', 'docker-compose.truenas.yml',
    'docker-compose.two-instances.yml', 'compose-env-file.yml',
    'examples/manga-cluster-autosourcarr-services.yml'])
def test_deployment_keeps_only_host_security_in_shared_defaults(name):
    config = yaml.safe_load((ROOT / name).read_text())
    defaults = config['x-autosourcarr-security']
    assert defaults['cap_drop'] == ['ALL']
    assert len(defaults['security_opt']) == 1
    assert defaults['security_opt'][0].startswith('seccomp=')
    for key, service in config['services'].items():
        if 'autosourcarr' not in key:
            assert 'security_opt' not in service, 'Main Suwayomi is outside this security change'
            continue
        assert 'user' not in service, 'The image owns its default non-root identity'
        assert service['cap_drop'] == ['ALL'] and service['security_opt'] == defaults['security_opt']
        assert not service.get('privileged') and not service.get('cap_add')
        assert '/var/run/docker.sock' not in str(service)
    lines = (ROOT / 'autosourcarr/Dockerfile').read_text().splitlines()
    assert [line for line in lines if line.startswith('USER ')][-1] == 'USER 568:568'


def test_missing_protection_diagnostic_points_to_internal_bootstrap(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT / 'repair-worker'))
    from internal_sandbox import isolation_failure
    result = isolation_failure('namespace denied', 1, {'NoNewPrivs': '0'})
    assert not result['available'] and result['reason'] == 'container-policy'
    assert 'normal start command' in result['message']
    assert 'Apply the repair security options' not in result['message']
