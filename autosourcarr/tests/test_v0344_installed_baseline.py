"""Cold-import the actual saved JAR; never call a different conversion equivalent."""
import ast
from contextlib import contextmanager
import hashlib
import importlib
import io
import json
from pathlib import Path
from types import MethodType, SimpleNamespace
from urllib.parse import urlparse
import zipfile

import httpx
import pytest

from test_v0339_baseline_manifest import CLASS, CODE, PKG, XML, package

ROOT = Path(__file__).resolve().parents[2]


@pytest.fixture
def worker(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT/'repair-worker'))
    manifest = importlib.import_module('baseline_manifest')
    contracts = importlib.import_module('contracts')
    validate = importlib.import_module('validate')
    policy = importlib.import_module('address_policy')
    probe = importlib.import_module('website_probe')
    namespace = dict(io=io, json=json, zipfile=zipfile, httpx=httpx, urlparse=urlparse,
        digest=contracts.digest, package_bytes=contracts.package_bytes,
        source_identity=contracts.source_identity, safe_text=contracts.safe_text,
        manifest_contract=manifest.manifest_contract, read_manifest=manifest.read_manifest,
        restore_manifest=manifest.restore_manifest, ConversionMismatch=manifest.ConversionMismatch,
        NS=manifest.NS, SourceBlocked=validate.SourceBlocked,
        AddressPolicy=policy.AddressPolicy, AddressBlocked=policy.AddressBlocked, public_url=probe.public_url)
    if hasattr(manifest, 'installed_envelope'):
        namespace['installed_envelope'] = manifest.installed_envelope
    tree = ast.parse((ROOT/'repair-worker/runner.py').read_text(encoding='utf-8'))
    cls = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == 'Runner')
    methods = [n for n in cls.body if isinstance(n, ast.FunctionDef) and n.name in {'run', 'inspect_artifact', 'converted_baseline'}]
    exec(compile(ast.Module(body=methods, type_ignores=[]), '<actual-runner-baseline>', 'exec'), namespace)
    return SimpleNamespace(manifest=manifest, namespace=namespace, blocked=validate.SourceBlocked)


def test_envelope_preserves_every_installed_payload_but_is_not_conversion_proof(worker):
    baseline = package([(CLASS, CODE), ('assets/kept', b'EXACT_INSTALLED_RESOURCE')])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    converted = package([(CLASS, CODE+b'DIFFERENT')])
    with pytest.raises(worker.manifest.ConversionMismatch):
        worker.manifest.restore_manifest(baseline, apk, converted)
    envelope, identity, proof = worker.manifest.installed_envelope(baseline, apk)
    with zipfile.ZipFile(io.BytesIO(envelope)) as archive:
        assert archive.read(CLASS) == CODE
        assert archive.read('assets/kept') == b'EXACT_INSTALLED_RESOURCE'
        assert set(archive.namelist()) == {CLASS, 'assets/kept', 'AndroidManifest.xml'}
    assert identity == (PKG, PKG+'.Example', '1.4')
    assert proof['installed_jar_sha256'] == hashlib.sha256(baseline).hexdigest()
    assert 'converted_jar_sha256' not in proof and 'verified' not in proof


@pytest.mark.parametrize('failure', ['', 'identity', 'website', 'website-path', 'website-scheme', 'changed-backup'])
def test_mismatched_conversion_needs_actual_installed_cold_import_before_checkout(worker, tmp_path, failure):
    baseline = package([(CLASS, CODE), ('assets/kept', b'EXACT_INSTALLED_RESOURCE')])
    converted = package([(CLASS, CODE+b'CONVERSION_DIFFERENCE'), ('assets/kept', b'EXACT_INSTALLED_RESOURCE')])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    (tmp_path/'baseline.jar').write_bytes(baseline)
    (tmp_path/'source.apk').write_bytes(apk)
    expected = [{'id': '1', 'name': 'Example', 'lang': 'en', 'baseUrl': 'https://source.example'}]
    events, imports = [], []

    class Validator:
        installed_extension = {'versionName': '1.4.23'}
        converted_jar_reader = lambda self, installed: converted
        def install(self, data, pkg, **options):
            imports.append((data, options))
            rows = [dict(expected[0])]
            if not options.get('apk'):
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    assert archive.read(CLASS) == CODE  # Never cold-test the reconverted bytes instead.
                if failure == 'identity': rows[0]['id'] = '2'
                if failure == 'website': rows[0]['baseUrl'] = 'https://unrelated.example'
                if failure == 'website-path': rows[0]['baseUrl'] = 'https://source.example/unrelated'
                if failure == 'website-scheme': rows[0]['baseUrl'] = 'http://source.example'
            return rows
        def close(self): pass
        def check(self, *args): raise ValueError('Controlled existing parser failure')

    @contextmanager
    def engine(job): yield Validator()
    def save(job, state, message, **values):
        job.update(state=state, message=message, **values); events.append(state)
    def checkout(job):
        events.append('checkout-called')
        raise worker.blocked('Controlled stop after cold import; no compilation or AI')
    owner = SimpleNamespace(path=lambda key: tmp_path, validation_engine=engine, save=save,
        wait_engine=lambda *args: None, converted_baseline_supported=True,
        check_websites=lambda *args: [{'state': 'working', 'source_ids': ['1']}],
        checkout=checkout, cancelled=set())
    for name in ('run', 'inspect_artifact', 'converted_baseline'):
        setattr(owner, name, MethodType(worker.namespace[name], owner))
    job = {'id': 'a'*32, 'pkg_name': PKG, 'installed_version': '1.4.23',
        'installed_sources': expected, 'original_apk_basis': 'recorded-installed-upload',
        'source_apk_sha256': hashlib.sha256(apk).hexdigest(),
        'baseline_sha256': hashlib.sha256(baseline).hexdigest() if failure != 'changed-backup' else 'b'*64}
    owner.run(job)
    assert job['state'] == 'blocked'
    assert (tmp_path/'baseline.jar').read_bytes() == baseline
    assert 'conversion_proof' not in job
    if not failure:
        assert 'checkout-called' in events
        assert job['conversion_comparison']['changed_class_entries'] == 1
        assert job['baseline_contract_basis'] == 'cold-import-installed-jar'
        assert job['baseline_import_proof']['verified'] is True
        assert job['baseline_import_proof']['installed_jar_sha256'] == job['baseline_sha256']
        assert len(imports) == 3  # APK conversion plus saved-JAR metadata and functional tests.
    else:
        assert 'checkout-called' not in events
        assert not job.get('baseline_import_proof', {}).get('verified')
