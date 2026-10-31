"""Real runner state machine, fake isolated engine/build; no source execution."""
from contextlib import contextmanager
import hashlib
import importlib.util
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import Mock

import pytest

from test_v0339_baseline_manifest import PKG, XML, CLASS, CODE, package

SOURCES = [{'id': '9223372036854775806', 'name': 'Example', 'lang': 'en', 'baseUrl': 'https://example.test'}]


@pytest.fixture
def runner_module(monkeypatch):
    worker = Path(__file__).resolve().parents[2] / 'repair-worker'
    monkeypatch.syspath_prepend(str(worker))
    if os.name == 'nt':
        # The tested state path must never use flock. Actual POSIX isolation is a
        # separate Linux gate, not simulated by this unused import adapter.
        monkeypatch.setitem(sys.modules, 'fcntl', SimpleNamespace(flock=lambda *a: pytest.fail('Unexpected native lock operation')))
    spec = importlib.util.spec_from_file_location('fixture_runner_v0339', worker / 'runner.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture
def prepared(runner_module, tmp_path, monkeypatch):
    runner = runner_module.Runner(tmp_path, tmp_path, '', owner=None)
    monkeypatch.setattr(runner, 'converted_baseline_supported', True)  # Controlled integrated-engine seam.
    raw = package([(CLASS, CODE), ('assets/retained.txt', b'ORIGINAL_ASSET')])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    candidate = package([('AndroidManifest.xml', XML), (CLASS, CODE + b'PATCH')])
    job = {'id': 'c' * 32, 'pkg_name': PKG, 'module': 'src/en/example', 'revision': '',
           'created_at': 1, 'baseline_sha256': hashlib.sha256(raw).hexdigest(),
           'source_apk_sha256': hashlib.sha256(apk).hexdigest(), 'installed_version': '1.4.23',
           'original_apk_basis': 'recorded-installed-upload',
           'installed_sources': SOURCES}
    directory = runner.path(job['id']); directory.mkdir()
    (directory / 'baseline.jar').write_bytes(raw)
    (directory / 'source.apk').write_bytes(apk)
    monkeypatch.setattr(runner, 'checkout', lambda _: tmp_path)
    monkeypatch.setattr(runner, 'build', Mock(return_value=candidate))
    monkeypatch.setattr(runner, 'edit', lambda *a: pytest.fail('A passing upstream fixture needs no AI edit'))
    state = {'converted': raw, 'sources': SOURCES, 'imports': [], 'engines_drained': 0}

    class Engine:
        installed_extension = {'pkgName': PKG, 'apkName': 'candidate.apk', 'versionName': '1.4.23'}
        converted_jar_reader = staticmethod(lambda _: state['converted'])

        def response(self, *a, **k):
            return None

        def install(self, data, pkg, *, apk=False):
            assert pkg == PKG
            state['imports'].append('apk' if apk else 'jar')
            if apk:
                assert data == (directory / 'source.apk').read_bytes()
                return state['sources']
            assert runner_module.manifest_contract(data) == (PKG, PKG + '.Example', '1.4')
            return SOURCES

        def check(self, sources, expected):
            assert sources == expected == SOURCES
            return [{'metadata': 'passed', 'chapters': 'passed', 'pages': 'passed'}]

        def close(self):
            pass

    @contextmanager
    def isolated(_):
        try:
            yield Engine()
        finally:
            state['engines_drained'] += 1

    monkeypatch.setattr(runner, 'validation_engine', isolated)
    return runner, job, raw, apk, state


def test_converted_backup_proceeds_only_after_exact_apk_conversion_and_keeps_rollback_hash(prepared):
    runner, job, raw, apk, state = prepared
    runner.run(job)
    assert job['state'] == 'ready', job['message']
    assert job['baseline_format'] == 'converted-apk-jar'
    assert job['baseline_sha256'] == hashlib.sha256(raw).hexdigest()
    assert job['conversion_proof']['source_apk_sha256'] == hashlib.sha256(apk).hexdigest()
    assert (runner.path(job['id']) / 'baseline.jar').read_bytes() == raw
    assert state['imports'] == ['apk', 'jar', 'jar', 'jar']
    assert state['engines_drained'] == 4
    assert (runner.path(job['id']) / 'candidate.jar').is_file()


@pytest.mark.parametrize('failure', ['changed-backup-hash', 'changed-source', 'changed-version', 'missing-apk', 'changed-apk', 'unproven-metadata'])
def test_missing_or_mismatching_original_proof_never_reaches_a_build(prepared, failure):
    runner, job, raw, apk, state = prepared
    if failure == 'changed-backup-hash': job['baseline_sha256'] = 'b'*64
    if failure == 'changed-source': state['sources'] = [{**SOURCES[0], 'id': '44'}]
    if failure == 'changed-version': job['installed_version'] = '1.4.24'
    if failure == 'missing-apk': (runner.path(job['id']) / 'source.apk').unlink()
    if failure == 'changed-apk': (runner.path(job['id']) / 'source.apk').write_bytes(apk + b'ALTERED')
    if failure == 'unproven-metadata': job.pop('original_apk_basis')
    runner.run(job)
    assert job['state'] == 'blocked', job['message']
    runner.build.assert_not_called()
    assert not (runner.path(job['id']) / 'candidate.jar').exists()
    assert (runner.path(job['id']) / 'baseline.jar').read_bytes() == raw


def test_different_conversion_requires_actual_saved_jar_import_and_retains_mismatch(prepared):
    runner, job, raw, apk, state = prepared
    state['converted'] = package([(CLASS, CODE + b'DIFFERENT_CONVERSION')])
    runner.run(job)
    assert job['state'] == 'ready', job['message']
    assert job['baseline_import_proof']['verified'] is True
    assert job['baseline_import_proof']['installed_jar_sha256'] == hashlib.sha256(raw).hexdigest()
    assert job['conversion_comparison']['changed_class_entries'] == 1
    assert job['conversion_comparison']['missing_entries'] == 1
    assert 'conversion_proof' not in job
    assert (runner.path(job['id'])/'baseline.jar').read_bytes() == raw
    assert state['imports'] == ['apk', 'jar', 'jar', 'jar'] and state['engines_drained'] == 4


def test_original_native_jar_path_does_not_require_or_convert_an_apk(prepared):
    runner, job, raw, apk, state = prepared
    native = package([('AndroidManifest.xml', XML), (CLASS, CODE)])
    (runner.path(job['id']) / 'baseline.jar').write_bytes(native)
    job['baseline_sha256'] = hashlib.sha256(native).hexdigest()
    (runner.path(job['id']) / 'source.apk').unlink()
    runner.run(job)
    assert job['state'] == 'ready', job['message']
    assert state['imports'] == ['jar', 'jar', 'jar']
    assert 'conversion_proof' not in job


def test_broken_native_manifest_is_not_treated_as_a_missing_converted_manifest(prepared):
    runner, job, raw, apk, state = prepared
    damaged = package([('AndroidManifest.xml', b'<manifest broken'), (CLASS, CODE)])
    (runner.path(job['id']) / 'baseline.jar').write_bytes(damaged)
    job['baseline_sha256'] = hashlib.sha256(damaged).hexdigest()
    runner.run(job)
    assert job['state'] == 'blocked'
    assert state['imports'] == []
    runner.build.assert_not_called()


def test_unsupported_legacy_runner_does_not_start_a_sibling_validation_container(prepared):
    runner, job, raw, apk, state = prepared
    runner.converted_baseline_supported = False
    runner.run(job)
    assert job['state'] == 'blocked'
    assert state['imports'] == [] and state['engines_drained'] == 0
    runner.build.assert_not_called()
