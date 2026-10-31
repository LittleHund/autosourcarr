"""Initial identity ownership with real temporary files and a fake key generator.

Windows fixtures replace unavailable flock/directory-fsync seams and sync files
through Windows writable handles. These do not certify Linux locking/durability
or actual Java cryptographic generation.
"""
from contextlib import contextmanager
import importlib.util
import os
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock

import pytest

PROJECT = Path(__file__).resolve().parents[2]


@pytest.fixture
def signing(monkeypatch):
    spec = importlib.util.spec_from_file_location('fixture_signing_state', PROJECT / 'extension/signing_state.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if os.name == 'nt':
        @contextmanager
        def fixture_lock(config):
            yield
        monkeypatch.setattr(module, '_exclusive_lock', fixture_lock)
        monkeypatch.setattr(module, '_sync_directory', lambda path: None)
        def windows_file_sync(path):
            with path.open('r+b') as source:
                os.fsync(source.fileno())
        monkeypatch.setattr(module, '_sync_regular', windows_file_sync)
    return module


def pair(folder, password='SYNTHETIC_EXISTING_PASSWORD'):
    folder.mkdir()
    (folder / 'signing.env').write_text('AUTOSOURCARR_EXTENSION_SIGNING_PASSWORD=' + password + '\n')
    (folder / 'autosourcarr-extension.jks').write_bytes(b'SYNTHETIC_RETAINED_KEYSTORE')


def contents(folder):
    return {path.name: path.read_bytes() for path in folder.iterdir()}


def test_first_generation_commits_only_a_complete_pair_and_reuses_it(signing, tmp_path, monkeypatch):
    destination = tmp_path / '.extension-signing'
    passwords = []
    def generate(key, password):
        assert not destination.exists()
        assert (key.parent / 'signing.env').is_file()
        passwords.append(password)
        key.write_bytes(b'SYNTHETIC_GENERATED_KEYSTORE')
    monkeypatch.setattr(signing, '_generate_keystore', generate)
    password = signing.prepare(tmp_path)
    assert passwords == [password]
    assert (destination / 'autosourcarr-extension.jks').read_bytes() == b'SYNTHETIC_GENERATED_KEYSTORE'
    before = contents(destination)
    assert signing.prepare(tmp_path) == password
    assert contents(destination) == before and len(passwords) == 1
    assert not list(tmp_path.glob('.extension-signing-stage-*'))


def test_failed_partial_generation_keeps_canonical_state_absent(signing, tmp_path, monkeypatch):
    def fail(key, password):
        key.write_bytes(b'SYNTHETIC_PARTIAL_KEYSTORE')
        raise signing.SigningStateError('Synthetic keytool failure', 42)
    monkeypatch.setattr(signing, '_generate_keystore', fail)
    with pytest.raises(signing.SigningStateError) as caught:
        signing.prepare(tmp_path)
    assert caught.value.exit_code == 42
    assert not (tmp_path / '.extension-signing').exists()
    assert not list(tmp_path.glob('.extension-signing-stage-*'))


@pytest.mark.parametrize('state', ['env-only','key-only','zero-pair','bad-env','duplicate-password','unrecognized-files'])
def test_preexisting_ambiguous_state_is_preserved_without_regeneration(signing, tmp_path, monkeypatch, state):
    folder = tmp_path / '.extension-signing'
    folder.mkdir()
    if state in {'env-only','zero-pair','bad-env','duplicate-password'}:
        data = '' if state == 'zero-pair' else 'AUTOSOURCARR_EXTENSION_SIGNING_PASSWORD=SYNTHETIC_OLD\n'
        if state == 'bad-env': data = 'UNRELATED_SETTING=retained\n'
        if state == 'duplicate-password': data *= 2
        (folder / 'signing.env').write_text(data)
    if state in {'key-only','zero-pair','bad-env','duplicate-password'}:
        (folder / 'autosourcarr-extension.jks').write_bytes(b'' if state == 'zero-pair' else b'SYNTHETIC_KEY')
    if state == 'unrecognized-files':
        (folder / 'retained-note').write_text('manual work')
    before = contents(folder)
    generator = Mock()
    monkeypatch.setattr(signing, '_generate_keystore', generator)
    with pytest.raises(signing.SigningStateError):
        signing.prepare(tmp_path)
    assert contents(folder) == before
    generator.assert_not_called()


def test_existing_identity_and_extra_manual_files_are_preserved(signing, tmp_path, monkeypatch):
    folder = tmp_path / '.extension-signing'
    pair(folder)
    (folder / 'manual-note').write_text('retained')
    before = contents(folder)
    generator = Mock()
    monkeypatch.setattr(signing, '_generate_keystore', generator)
    assert signing.prepare(tmp_path) == 'SYNTHETIC_EXISTING_PASSWORD'
    assert contents(folder) == before
    generator.assert_not_called()


def test_uncertain_parent_sync_retains_the_committed_identity_for_retry(signing, tmp_path, monkeypatch):
    passwords = []
    def generate(key, password):
        passwords.append(password)
        key.write_bytes(b'SYNTHETIC_KEY')
    def fail_parent_sync(path):
        if path == tmp_path:
            raise OSError('Synthetic directory sync failure')
    monkeypatch.setattr(signing, '_generate_keystore', generate)
    monkeypatch.setattr(signing, '_sync_directory', fail_parent_sync)
    with pytest.raises(signing.SigningStateError):
        signing.prepare(tmp_path)
    folder = tmp_path / '.extension-signing'
    before = contents(folder)
    monkeypatch.setattr(signing, '_sync_directory', lambda path: None)
    assert signing.prepare(tmp_path) == passwords[0]
    assert contents(folder) == before and len(passwords) == 1


def test_keytool_password_is_command_local_environment_and_never_argv(signing, tmp_path, monkeypatch):
    calls = []
    monkeypatch.setenv('AUTOSOURCARR_READER_API_TOKEN', 'SYNTHETIC_UNRELATED_READER_KEY')
    monkeypatch.setattr(signing.subprocess, 'run', lambda args, **kwargs: calls.append((args, kwargs)) or SimpleNamespace(returncode=0))
    signing._generate_keystore(tmp_path / 'fixture.jks', 'SYNTHETIC_SIGNING_PASSWORD')
    args, options = calls[0]
    assert 'SYNTHETIC_SIGNING_PASSWORD' not in args
    assert '-storepass:env' in args and '-keypass:env' in args
    assert args[0] == 'keytool' and '-genkeypair' in args
    assert args[args.index('-storetype')+1] == 'JKS'
    assert args[args.index('-alias')+1] == 'autosourcarr'
    assert args[args.index('-keyalg')+1] == 'RSA' and args[args.index('-keysize')+1] == '4096'
    assert args[args.index('-validity')+1] == '36500'
    assert options['env']['AUTOSOURCARR_SIGNING_PASSWORD'] == 'SYNTHETIC_SIGNING_PASSWORD'
    assert 'AUTOSOURCARR_READER_API_TOKEN' not in options['env']


@pytest.mark.skipif(os.name != 'posix', reason='Replacing an existing empty directory atomically is a Linux/POSIX gate')
def test_existing_empty_signing_directory_can_receive_the_initial_pair(signing, tmp_path, monkeypatch):
    (tmp_path / '.extension-signing').mkdir()
    monkeypatch.setattr(signing, '_generate_keystore', lambda key, password: key.write_bytes(b'SYNTHETIC_KEY'))
    assert signing.prepare(tmp_path)
    assert (tmp_path / '.extension-signing/autosourcarr-extension.jks').read_bytes() == b'SYNTHETIC_KEY'
