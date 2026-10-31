"""Exercise the production build coordinator with controlled SDK/compiler executables."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import zipfile

import pytest

ROOT = Path(__file__).resolve().parents[2]


def executable(path, content):
    path.write_text(content)
    path.chmod(0o755)


@pytest.fixture
def sdk(tmp_path):
    work = tmp_path / 'project'
    shutil.copytree(ROOT / 'extension', work / 'extension', ignore=shutil.ignore_patterns('.build', '.gradle-home', 'dist', 'dist-instances'))
    (work / 'VERSION').write_text('0.3.8')
    script = work / 'extension/build-in-container.sh'
    checkout = work / 'extension/.build/extensions-source'
    (checkout / '.git').mkdir(parents=True)
    (checkout / 'gradle/wrapper').mkdir(parents=True)
    (checkout / 'gradle/wrapper/gradle-wrapper.properties').write_text('distributionUrl=https://example.invalid/gradle-1-bin.zip\n')
    fakebin = tmp_path / 'bin'; fakebin.mkdir()
    executable(fakebin / 'git', """#!/usr/bin/env python3
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
work=Path('.')
if args[:1]==['-C']:
 work=Path(args[1]);args=args[2:]
if os.environ.get('KEI_GIT_EVENTS'):
 with open(os.environ['KEI_GIT_EVENTS'],'a') as stream:stream.write(json.dumps(args)+'\\n')
default='e40e1b78f46baf5ef9dc68cb980f82c66766a211'
if args[:2]==['remote','get-url']:
 print(os.environ.get('FAKE_KEI_ORIGIN','https://github.com/keiyoushi/extensions-source.git'))
elif args[:1]==['init']:
 (Path(args[1])/'.git').mkdir(parents=True,exist_ok=True)
elif args[:1]==['reset']:
 commit=args[-1]
 if commit=='origin/main':commit=os.environ.get('FAKE_KEI_LATEST',default)
 (work/'.git/fixture-head').write_text(commit)
elif args[:1]==['rev-parse']:
 head=work/'.git/fixture-head'
 print(head.read_text() if head.exists() else default)
""")
    executable(fakebin / 'jar', '''#!/usr/bin/env python3
import sys,zipfile
with zipfile.ZipFile(sys.argv[2]) as z: print('\\n'.join(z.namelist()))
''')
    executable(fakebin / 'aapt', '''#!/bin/sh
echo "package: name='eu.kanade.tachiyomi.extension.all.autosourcarr' versionName='1.4.3008'"
''')
    executable(fakebin / 'apksigner', '''#!/usr/bin/env python3
import sys,shutil
args=sys.argv[1:]
if args[0]=='sign': shutil.copyfile(args[-1],args[args.index('--out')+1])
else: print('Signer #1 certificate SHA-256 digest: ' + '0123456789abcdef'*4)
''')
    executable(fakebin / 'keytool', '#!/bin/sh\necho unexpected-new-signing-key >&2\nexit 44\n')
    executable(checkout / 'gradlew', '''#!/usr/bin/env python3
import json,os,sys,time,zipfile
from pathlib import Path
if '--version' in sys.argv: sys.exit(0)
module=Path('src/all/autosourcarr')
if any(x.endswith(':clean') for x in sys.argv):
 import shutil
 shutil.rmtree(module/'build',ignore_errors=True)
 sys.exit(0)
base=os.environ['AUTOSOURCARR_EXTENSION_BASE_URL']
auth=(module/'src/eu/kanade/tachiyomi/extension/all/autosourcarr/BuildAuth.kt').read_text()
data={'base':base,'auth':auth,'run':os.environ.get('BUILD_RUN','one')}
with open(os.environ['EVENTS'],'a') as out: out.write(json.dumps({'base':base,'run':data['run']})+'\\n')
if os.environ.get('FAIL_BASE')==base: sys.exit(42)
if os.environ.get('BLOCK_FIRST')=='1' and not Path(os.environ['RELEASE']).exists():
 Path(os.environ['STARTED']).touch()
 deadline=time.monotonic()+10
 while not Path(os.environ['RELEASE']).exists():
  if time.monotonic()>deadline: sys.exit(43)
  time.sleep(.01)
for suffix,entries in [('jar',{'AndroidManifest.xml':b'manifest','Source.class':bytes.fromhex('cafebabe')+b'fixture'}),('apk',{'AndroidManifest.xml':b'manifest','classes.dex':b'dex\\n035\\0','resources.arsc':b'fixture'})]:
 directory=module/'build/outputs'/suffix/'release'; directory.mkdir(parents=True,exist_ok=True)
 with zipfile.ZipFile(directory/('fixture.'+suffix),'w') as z:
  for name,value in entries.items(): z.writestr(name,value)
  z.writestr('fixture.json',json.dumps(data))
''')
    configs = []
    for name in ('main', 'private'):
        config = tmp_path / name; config.mkdir()
        host = 'autosourcarr' if name == 'main' else 'private-autosourcarr'
        token = ('a' if name == 'main' else 'b') * 64
        (config / 'autosourcarr.env').write_text(f'AUTOSOURCARR_INTERNAL_URL=http://{host}:8282\nAUTOSOURCARR_PUBLIC_URL=https://{name}.example.invalid\nAUTOSOURCARR_READER_API_TOKEN={token}\n')
        sign = config / '.extension-signing'; sign.mkdir()
        (sign / 'signing.env').write_text('AUTOSOURCARR_EXTENSION_SIGNING_PASSWORD=saved-password\n')
        (sign / 'autosourcarr-extension.jks').write_text(f'{name}-retained-key')
        configs.append(config)
    env = dict(os.environ, AUTOSOURCARR_BUILD_ROOT=str(work), PATH=f'{fakebin}:{os.environ["PATH"]}', EVENTS=str(tmp_path / 'events.jsonl'),
               AUTOSOURCARR_CONFIG_DIR=str(configs[0]), AUTOSOURCARR_PRIVATE_CONFIG_DIR=str(configs[1]),
               GRADLE_BUILD_RETRIES='1', GRADLE_BOOTSTRAP_RETRIES='1',
               AUTOSOURCARR_PUBLISH_UID=str(os.getuid()), AUTOSOURCARR_PUBLISH_GID=str(os.getgid()))
    return work, configs, env


def run_build(sdk, **overrides):
    work, _, env = sdk
    return subprocess.run(['bash', str(work / 'extension/build-in-container.sh')], env=dict(env, **overrides), text=True, capture_output=True, timeout=20)


def artifact_info(path):
    with zipfile.ZipFile(path) as z: return json.loads(z.read('fixture.json'))


def test_paired_builder_uses_each_config_and_preserves_signing_and_downloads(sdk):
    work, configs, env = sdk
    result = run_build(sdk, AUTOSOURCARR_INTERNAL_URL='http://wrong:8282', AUTOSOURCARR_PUBLIC_URL='https://wrong.example.invalid', AUTOSOURCARR_READER_API_TOKEN='c'*64)
    assert result.returncode == 0, result.stdout + result.stderr
    events = [json.loads(line)['base'] for line in Path(env['EVENTS']).read_text().splitlines()]
    assert events == ['http://autosourcarr:8282', 'https://main.example.invalid', 'http://private-autosourcarr:8282', 'https://private.example.invalid']
    for index, instance in enumerate(('autosourcarr', 'private-autosourcarr')):
        out = work / 'extension/dist-instances' / instance
        jar = next(out.glob('*.jar')); apk = next(out.glob('*.apk'))
        token = ('a' if index == 0 else 'b')*64
        assert token not in artifact_info(jar)['auth']
        assert token in artifact_info(apk)['auth']
        assert '/api/v1' in artifact_info(jar)['auth']
        assert '/api/reader/v1' in artifact_info(apk)['auth']
        assert token not in result.stdout + result.stderr
        assert apk.stat().st_mode & 0o777 == 0o600
        assert out.stat().st_mode & 0o777 == 0o700
        config = configs[index]
        manifest = json.loads((config / 'extension-downloads/current-build.json').read_text())
        published = config / 'extension-downloads' / f"0.3.8-{manifest['apk']['sha256']}.apk"
        assert published.read_bytes() == apk.read_bytes()
        assert (config / '.extension-signing/autosourcarr-extension.jks').read_text() == f'{config.name}-retained-key'
    assert not (work / 'extension/dist').exists()


def test_failed_private_build_keeps_previous_private_outputs(sdk):
    work, configs, env = sdk
    assert run_build(sdk).returncode == 0
    out = work / 'extension/dist-instances/private-autosourcarr'
    saved = {p.name: p.read_bytes() for p in out.iterdir()}
    pointer = (configs[1] / 'extension-downloads/current-build.json').read_bytes()
    result = run_build(sdk, FAIL_BASE='https://private.example.invalid')
    assert result.returncode == 4, result.stderr
    assert {p.name: p.read_bytes() for p in out.iterdir()} == saved
    assert (configs[1] / 'extension-downloads/current-build.json').read_bytes() == pointer
    assert not list((work / 'extension').glob('.dist-stage.*'))


@pytest.mark.parametrize('missing', ['private', 'explicit-single', 'private-key'])
def test_missing_explicit_configuration_fails_before_compiler_or_fallback(sdk, missing):
    work, configs, env = sdk
    legacy = work / 'config'; legacy.mkdir(); shutil.copy(configs[0]/'autosourcarr.env', legacy/'autosourcarr.env')
    if missing == 'private':
        result = run_build(sdk, AUTOSOURCARR_PRIVATE_CONFIG_DIR=str(work/'missing'))
    elif missing == 'private-key':
        (configs[1]/'autosourcarr.env').write_text('AUTOSOURCARR_PUBLIC_URL=https://private.example.invalid\n')
        result = run_build(sdk)
    else:
        result = run_build(sdk, AUTOSOURCARR_CONFIG_DIR=str(work/'missing'), AUTOSOURCARR_PRIVATE_CONFIG_DIR='')
    assert result.returncode == 21
    assert not Path(env['EVENTS']).exists()


def test_same_config_cannot_silently_build_both_instances(sdk):
    _, configs, env = sdk
    result = run_build(sdk, AUTOSOURCARR_PRIVATE_CONFIG_DIR=str(configs[0]))
    assert result.returncode == 22
    assert not Path(env['EVENTS']).exists()


def test_single_builder_retains_original_output_location(sdk):
    work, _, _ = sdk
    result = run_build(sdk, AUTOSOURCARR_PRIVATE_CONFIG_DIR='')
    assert result.returncode == 0, result.stderr
    assert len(list((work/'extension/dist').glob('*.jar'))) == 1
    assert len(list((work/'extension/dist').glob('*.apk'))) == 1
    assert not (work/'extension/dist-instances').exists()


def test_concurrent_builders_serialize_whole_pair_on_shared_project(sdk, tmp_path):
    work, _, env = sdk
    command = ['bash', str(work/'extension/build-in-container.sh')]
    started, release = tmp_path/'started', tmp_path/'release'
    first = subprocess.Popen(command, env=dict(env, BUILD_RUN='first', BLOCK_FIRST='1', STARTED=str(started), RELEASE=str(release)), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    second = None
    try:
        deadline = time.monotonic()+5
        while not started.exists() and time.monotonic()<deadline: time.sleep(.01)
        assert started.exists()
        second = subprocess.Popen(command, env=dict(env, BUILD_RUN='second'), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert 'Waiting for' in second.stdout.readline()
        release.touch()
        first_out = first.communicate(timeout=20); second_out = second.communicate(timeout=20)
        assert first.returncode == 0, first_out
        assert second.returncode == 0, second_out
        runs = [json.loads(line)['run'] for line in Path(env['EVENTS']).read_text().splitlines()]
        assert runs == ['first']*4 + ['second']*4
    finally:
        for process in (first, second):
            if process and process.poll() is None: process.kill(); process.communicate()


def test_host_wrapper_passes_two_mounts_to_one_container(sdk, tmp_path):
    work, configs, env = sdk
    fakebin = Path(env['PATH'].split(':')[0])
    args_file = tmp_path/'docker-args.json'
    executable(fakebin/'docker', '#!/usr/bin/env python3\nimport json,os,sys\nopen(os.environ["DOCKER_ARGS"],"w").write(json.dumps(sys.argv[1:]))\n')
    result = subprocess.run(['bash', str(work/'extension/build.sh')], env=dict(env,
        AUTOSOURCARR_HOST_CONFIG_DIR=str(configs[0]), AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR=str(configs[1]), DOCKER_ARGS=str(args_file)), capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    args = json.loads(args_file.read_text())
    assert args[0] == 'run'
    assert f'{configs[0]}:/autosourcarr-config' in args
    assert f'{configs[1]}:/autosourcarr-private-config' in args
    assert 'AUTOSOURCARR_PRIVATE_CONFIG_DIR=/autosourcarr-private-config' in args
    assert args[-2:] == ['bash', '/workspace/extension/build-in-container.sh']


def test_host_wrapper_explicit_missing_config_cannot_fall_back_to_project(sdk, tmp_path):
    work, configs, env = sdk
    legacy = work/'config'; legacy.mkdir(); shutil.copy(configs[0]/'autosourcarr.env', legacy/'autosourcarr.env')
    result = subprocess.run(['bash', str(work/'extension/build.sh')], env=dict(env,
        AUTOSOURCARR_HOST_CONFIG_DIR=str(tmp_path/'missing')), capture_output=True, text=True)
    assert result.returncode == 21
    assert 'Missing' in result.stderr


def test_publication_ownership_failure_keeps_previous_download_pointer(sdk):
    work, configs, env = sdk
    assert run_build(sdk).returncode == 0
    config = configs[0]
    pointer = config/'extension-downloads/current-build.json'
    old_pointer = pointer.read_bytes()
    old = json.loads(old_pointer)
    old_apk = config/'extension-downloads'/f"0.3.8-{old['apk']['sha256']}.apk"
    old_bytes, old_mode, old_owner = old_apk.read_bytes(), old_apk.stat().st_mode, old_apk.stat().st_uid
    fakebin = Path(env['PATH'].split(':')[0])
    real_stat, real_chown = shutil.which('stat'), shutil.which('chown')
    # Emulate root-created stage ownership requiring a handoff. This remains
    # runnable without root; the injected chown failure occurs before any publish.
    executable(fakebin/'stat', f'''#!/usr/bin/env python3
import os,sys
if '.publish.' in sys.argv[-1] and '%u:%g' in sys.argv:
 print('99999:99999')
else:
 os.execv({real_stat!r},[{real_stat!r}]+sys.argv[1:])
''')
    executable(fakebin/'chown', f'''#!/usr/bin/env python3
import os,sys
if any('.publish.' in arg for arg in sys.argv):
 print('simulated staged ownership failure',file=sys.stderr)
 sys.exit(73)
os.execv({real_chown!r},[{real_chown!r}]+sys.argv[1:])
''')
    failed = run_build(sdk, BUILD_RUN='replacement')
    assert failed.returncode == 73, failed.stdout + failed.stderr
    assert pointer.read_bytes() == old_pointer
    assert old_apk.read_bytes() == old_bytes
    assert old_apk.stat().st_mode == old_mode
    assert old_apk.stat().st_uid == old_owner
    assert not list((config/'extension-downloads').glob('.publish.*'))
    assert len(list((config/'extension-downloads').glob('*.apk'))) == 1
