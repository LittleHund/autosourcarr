"""Shared generation publication preserves authenticated legacy downloads."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import stat

import pytest

from app import extension_download as downloads
from test_v039_builder import sdk, run_build, artifact_info


PROJECT = Path(__file__).resolve().parents[2]


def pair_publisher():
    spec = importlib.util.spec_from_file_location('pair_publisher', PROJECT/'extension/publish-pair.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def selected(config):
    return json.loads((config/'extension-downloads/current-build.json').read_bytes())


def test_generation_downloads_select_matching_artifacts(sdk):
    work, configs, _ = sdk
    result = run_build(sdk)
    assert result.returncode == 0, result.stdout + result.stderr
    for config in configs:
        manifest = selected(config)
        assert manifest['apk']['app_version'] == manifest['jar']['app_version'] == '0.3.8'
        assert downloads.apk_status(config, '0.3.8')['available']
        assert downloads.jar_status(config, '0.3.8')['available']
        for kind, reader in [('apk', downloads.apk_bytes), ('jar', downloads.jar_bytes)]:
            data = reader(config, '0.3.8')
            assert hashlib.sha256(data).hexdigest() == manifest[kind]['sha256']
        assert not list((config/'extension-downloads').glob('.publish-pair.*'))


def test_second_publisher_failure_retains_shared_pointer_and_distribution(sdk):
    work, configs, _ = sdk
    assert run_build(sdk).returncode == 0
    pointer = configs[0]/'extension-downloads/current-build.json'
    previous = pointer.read_bytes()
    distribution = work/'extension/dist-instances/autosourcarr'
    saved = {path.name: path.read_bytes() for path in distribution.iterdir()}
    # Change only the existing fixture's temporary project, never release sources.
    (work/'extension/publish-jar.py').write_text('raise SystemExit(74)\n')
    failed = run_build(sdk, BUILD_RUN='replacement')
    assert failed.returncode == 74, failed.stdout + failed.stderr
    assert pointer.read_bytes() == previous
    assert {path.name: path.read_bytes() for path in distribution.iterdir()} == saved
    assert not list((configs[0]/'extension-downloads').glob('.publish-pair.*'))
    assert not list((work/'extension').glob('.dist-stage.*'))
    assert artifact_info(next(distribution.glob('*.apk')))['run'] == 'one'


@pytest.mark.parametrize('failure_point', ['first-artifact', 'second-artifact', 'manifest'])
def test_move_failure_never_changes_current_generation(sdk, monkeypatch, failure_point):
    work, configs, env = sdk
    assert run_build(sdk).returncode == 0
    pointer = configs[0]/'extension-downloads/current-build.json'
    previous = pointer.read_bytes()
    publisher = pair_publisher()
    output = work/'extension/dist-instances/autosourcarr'
    monkeypatch.setenv('PATH', env['PATH'])
    original = publisher.os.replace
    artifact_moves = 0
    def replace(source, destination):
        nonlocal artifact_moves
        destination = Path(destination)
        if destination.suffix in {'.apk', '.jar'}:
            artifact_moves += 1
        if ((failure_point == 'first-artifact' and artifact_moves == 1)
                or (failure_point == 'second-artifact' and artifact_moves == 2)
                or (failure_point == 'manifest' and destination.name == 'current-build.json')):
            raise OSError('controlled atomic publication failure')
        return original(source, destination)
    monkeypatch.setattr(publisher.os, 'replace', replace)
    with pytest.raises(OSError, match='controlled'):
        publisher.publish(next(output.glob('*.apk')), next(output.glob('*.jar')), configs[0], '0.3.8')
    assert pointer.read_bytes() == previous
    assert downloads.apk_bytes(configs[0], '0.3.8')
    assert downloads.jar_bytes(configs[0], '0.3.8')


@pytest.mark.parametrize('content', [b'{', b'{}', b'{"format":1,"app_version":"0.3.8","apk":{}}', b'X'*8193])
def test_invalid_generation_cannot_fall_back_to_legacy_pointers(tmp_path, content):
    directory = tmp_path/'extension-downloads'; directory.mkdir()
    version = '0.3.8'
    for kind, data in [('apk', b'old apk'), ('jar', b'old jar')]:
        digest = hashlib.sha256(data).hexdigest()
        (directory/f'{version}-{digest}.{kind}').write_bytes(data)
        metadata = {'app_version': version, 'sha256': digest, 'size': len(data)}
        if kind == 'apk':
            metadata.update(package=downloads.PACKAGE, signature_verified=True,
                            extension_version=downloads.extension_version(version))
        else:
            metadata['validated'] = True
        (directory/('current.json' if kind == 'apk' else 'current-jar.json')).write_text(json.dumps(metadata))
    assert downloads.apk_status(tmp_path, version)['available']
    assert downloads.jar_status(tmp_path, version)['available']
    (directory/'current-build.json').write_bytes(content)
    assert not downloads.apk_status(tmp_path, version)['available']
    assert not downloads.jar_status(tmp_path, version)['available']


@pytest.mark.parametrize('after_commit', [False, True])
def test_directory_sync_failure_distinguishes_uncommitted_from_committed(sdk, monkeypatch, capsys, after_commit):
    work, configs, env = sdk
    assert run_build(sdk).returncode == 0
    pointer = configs[0]/'extension-downloads/current-build.json'
    previous = pointer.read_bytes()
    publisher = pair_publisher()
    output = work/'extension/dist-instances/autosourcarr'
    candidate = work/'new.jar'; candidate.write_bytes(b'controlled replacement native JAR')
    monkeypatch.setenv('PATH', env['PATH'])
    original = publisher.os.fsync
    directory_syncs = 0
    def fsync(fd):
        nonlocal directory_syncs
        if stat.S_ISDIR(publisher.os.fstat(fd).st_mode):
            directory_syncs += 1
            if directory_syncs == (2 if after_commit else 1):
                raise OSError('controlled directory sync failure')
        return original(fd)
    monkeypatch.setattr(publisher.os, 'fsync', fsync)
    if after_commit:
        result = publisher.publish(next(output.glob('*.apk')), candidate, configs[0], '0.3.8')
        assert result == {'committed': True, 'durable': False}
        assert pointer.read_bytes() != previous
        assert downloads.jar_bytes(configs[0], '0.3.8') == candidate.read_bytes()
        assert 'generation committed' in capsys.readouterr().err
    else:
        with pytest.raises(OSError, match='sync'):
            publisher.publish(next(output.glob('*.apk')), candidate, configs[0], '0.3.8')
        assert pointer.read_bytes() == previous
