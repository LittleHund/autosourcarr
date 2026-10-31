"""Exercise the public paired-build controls with the controlled SDK from 0.3.9."""
import json
import os
from pathlib import Path
import subprocess

import pytest

from test_v039_builder import sdk, run_build, artifact_info, executable


def command(sdk, *arguments, **overrides):
    work, _, env = sdk
    return subprocess.run(
        ['bash', str(work / 'extension/build-in-container.sh'), *arguments],
        env=dict(env, **overrides), capture_output=True, text=True, timeout=20,
    )


@pytest.mark.parametrize('arguments,mode', [(('--both',), 'auto'), ((), 'paired')])
def test_explicit_pair_cannot_report_success_without_private_config(sdk, arguments, mode):
    work, configs, env = sdk
    result = command(sdk, *arguments, AUTOSOURCARR_PRIVATE_CONFIG_DIR='', AUTOSOURCARR_BUILD_MODE=mode)
    assert result.returncode == 21
    assert 'private APK/JAR files were not built' in result.stderr
    assert not Path(env['EVENTS']).exists()
    assert not (work / 'extension/.builder.lock').exists()
    assert not (configs[0] / 'extension-downloads').exists()


def test_configuration_check_identifies_both_outputs_without_mutating_files(sdk):
    work, configs, env = sdk
    before = {str(p.relative_to(work)): p.stat().st_mtime_ns for p in work.rglob('*') if p.is_file()}
    result = command(sdk, '--both', '--check')
    assert result.returncode == 0, result.stderr
    assert 'dist-instances/private-autosourcarr' in result.stdout
    assert 'http://private-autosourcarr:8282' in result.stdout
    assert 'No files were written' in result.stdout
    assert not Path(env['EVENTS']).exists()
    assert before == {str(p.relative_to(work)): p.stat().st_mtime_ns for p in work.rglob('*') if p.is_file()}
    assert all(not (config / 'extension-downloads').exists() for config in configs)
    assert 'a'*64 not in result.stdout + result.stderr
    assert 'b'*64 not in result.stdout + result.stderr


@pytest.mark.parametrize('value,error', [('short', 13), ('', 21)])
def test_private_token_checked_before_normal_compile(sdk, value, error):
    _, configs, env = sdk
    config = configs[1] / 'autosourcarr.env'
    config.write_text(config.read_text().replace('b'*64, value))
    result = command(sdk, '--both')
    assert result.returncode == error, result.stdout + result.stderr
    assert not Path(env['EVENTS']).exists()


def test_private_internal_url_cannot_still_point_at_normal_instance(sdk):
    _, configs, env = sdk
    config = configs[1] / 'autosourcarr.env'
    config.write_text(config.read_text().replace('http://private-autosourcarr:8282', 'http://autosourcarr:8282/'))
    result = command(sdk, '--both')
    assert result.returncode == 22
    assert 'same AUTOSOURCARR_INTERNAL_URL' in result.stderr
    assert not Path(env['EVENTS']).exists()


def test_quoted_crlf_env_values_build_correct_instance_artifacts(sdk):
    work, configs, env = sdk
    for config in configs:
        path = config / 'autosourcarr.env'
        lines = []
        for line in path.read_text().splitlines():
            key, value = line.split('=', 1)
            lines.append(f'export {key} = "{value}" # retained setting\r\n')
        path.write_bytes(''.join(lines).encode())
    result = command(sdk, '--both')
    assert result.returncode == 0, result.stdout + result.stderr
    out = work / 'extension/dist-instances/private-autosourcarr'
    jar = next(out.glob('AutoSourcarr-private-*.jar'))
    apk = next(out.glob('AutoSourcarr-private-*.apk'))
    assert artifact_info(jar)['base'] == 'http://private-autosourcarr:8282'
    assert artifact_info(apk)['base'] == 'https://private.example.invalid'
    assert 'b'*64 in artifact_info(apk)['auth']
    assert 'b'*64 not in result.stdout + result.stderr
    assert 'old extension/dist directory is not updated' in result.stdout


def test_legacy_single_mode_explicitly_explains_no_private_artifacts(sdk):
    work, _, env = sdk
    result = command(sdk, '--single')
    assert result.returncode == 0, result.stdout + result.stderr
    assert 'private APK/JAR files will NOT be built' in result.stdout
    assert not (work / 'extension/dist-instances').exists()
    assert len(Path(env['EVENTS']).read_text().splitlines()) == 2


def test_host_wrapper_passes_explicit_pair_and_check_controls(sdk, tmp_path):
    work, configs, env = sdk
    fakebin = Path(env['PATH'].split(':')[0])
    recorded = tmp_path / 'args.json'
    executable(fakebin / 'docker', '#!/usr/bin/env python3\nimport os,json,sys\nopen(os.environ["DOCKER_ARGS"],"w").write(json.dumps(sys.argv[1:]))\n')
    result = subprocess.run(
        ['bash', str(work / 'extension/build.sh'), '--both', '--check'],
        env=dict(env, AUTOSOURCARR_HOST_CONFIG_DIR=str(configs[0]),
                 AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR=str(configs[1]),
                 AUTOSOURCARR_BUILD_MODE='paired', DOCKER_ARGS=str(recorded)),
        capture_output=True, text=True,
    )
    assert result.returncode == 0, result.stderr
    args = json.loads(recorded.read_text())
    assert 'AUTOSOURCARR_BUILD_MODE=paired' in args
    assert args[-4:] == ['bash', '/workspace/extension/build-in-container.sh', '--both', '--check']


def test_private_android_url_cannot_be_docker_only(sdk):
    _, configs, env = sdk
    path = configs[1] / 'autosourcarr.env'
    path.write_text(path.read_text().replace('https://private.example.invalid', 'https://private-autosourcarr:8282'))
    result = command(sdk, '--both')
    assert result.returncode == 11
    assert 'Docker-only' in result.stderr
    assert not Path(env['EVENTS']).exists()


def test_unknown_arguments_cannot_start_a_build(sdk):
    _, _, env = sdk
    result = command(sdk, '--private-only-typo')
    assert result.returncode == 24
    assert not Path(env['EVENTS']).exists()


def test_env_is_never_executed_or_interpolated(sdk, tmp_path):
    _, configs, env = sdk
    marker = tmp_path / 'must-not-exist'
    path = configs[1] / 'autosourcarr.env'
    path.write_text(path.read_text().replace('https://private.example.invalid', f'https://$(touch {marker}).invalid'))
    result = command(sdk, '--check')
    assert result.returncode == 14
    assert not marker.exists()
    assert not Path(env['EVENTS']).exists()
    assert str(marker) not in result.stdout + result.stderr


def test_temporary_signer_sidecars_are_not_published(sdk, tmp_path):
    work, _, env = sdk
    fakebin = Path(env['PATH'].split(':')[0])
    marker = tmp_path / 'signed-sidecars'
    executable(fakebin / 'apksigner', '''#!/usr/bin/env python3
import os,shutil,sys
from pathlib import Path
args=sys.argv[1:]
if args[0]=='sign':
 output=args[args.index('--out')+1]
 shutil.copyfile(args[-1],output)
 Path(output+'.idsig').write_bytes(b'v4-signature-fixture')
 with open(os.environ['SIDECAR_EVENTS'],'a') as recorded: recorded.write(output+'\\n')
else: print('Signer #1 certificate SHA-256 digest: abcdef1234')
''')
    result = command(sdk, '--both', SIDECAR_EVENTS=str(marker))
    assert result.returncode == 0, result.stdout + result.stderr
    assert len(marker.read_text().splitlines()) == 2
    for instance, prefix in [('autosourcarr', 'AutoSourcarr'), ('private-autosourcarr', 'AutoSourcarr-private')]:
        output = work / 'extension/dist-instances' / instance
        assert {p.name for p in output.iterdir()} == {
            f'{prefix}-v0.3.8-Suwayomi.jar',
            f'{prefix}-v0.3.8-Tachiyomi-Mihon.apk',
            'README.txt',
        }
