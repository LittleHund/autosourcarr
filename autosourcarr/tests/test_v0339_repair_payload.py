"""Bounded private request parsing; preserve installed rollback bytes separately."""
import base64
import importlib
from pathlib import Path

import pytest

from test_v0339_baseline_manifest import PKG, XML, CLASS, CODE, package


@pytest.fixture
def contracts(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2] / 'repair-worker'))
    return importlib.import_module('contracts')


def request():
    return {'pkg_name': PKG, 'module': 'src/en/example', 'issue': 'Check this original source without changing its identity.',
            'baseline': base64.b64encode(package([(CLASS, CODE)])).decode(),
            'source_apk': base64.b64encode(package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])).decode(),
            'installed_version': '1.4.23',
            'original_apk_basis': 'recorded-installed-upload',
            'installed_sources': [{'id': '9223372036854775806', 'name': 'Example', 'lang': 'en', 'baseUrl': 'https://example.test'}]}


def test_private_request_decodes_and_pins_both_packages_without_exposing_their_bodies(contracts):
    body = request()
    values, baseline, apk = contracts.request_payload(body)
    assert base64.b64encode(baseline).decode() == body['baseline']
    assert base64.b64encode(apk).decode() == body['source_apk']
    assert values['installed_sources'][0]['id'] == '9223372036854775806'
    assert 'source_apk' not in values and 'baseline' not in values
    assert values['source_apk_sha256'] == contracts.digest(apk)


@pytest.mark.parametrize('field,value', [('source_apk', 'invalid!'), ('source_apk', ''), ('source_apk', []),
                                       ('installed_sources', []), ('installed_sources', [{'id': 'not-an-id'}]),
                                       ('installed_version', ''), ('installed_version', '1.4.23\n'),
                                       ('original_apk_basis', ''), ('original_apk_basis', 'guessed')])
def test_incomplete_or_invalid_converted_inputs_are_not_admitted(contracts, field, value):
    body = request(); body[field] = value
    with pytest.raises((ValueError, TypeError)):
        contracts.request_payload(body)


def test_native_jobs_and_orphan_recovery_keep_their_existing_wire_contract(contracts):
    body = request()
    for field in ('source_apk', 'installed_version', 'installed_sources'): body.pop(field)
    values, baseline, apk = contracts.request_payload(body)
    assert baseline and apk == b'' and 'source_apk_sha256' not in values
    body.pop('baseline')
    body.update(recovery=True, revision='a' * 40, expected_sources=request()['installed_sources'])
    values, baseline, apk = contracts.request_payload(body)
    assert values['recovery'] and baseline == apk == b''
