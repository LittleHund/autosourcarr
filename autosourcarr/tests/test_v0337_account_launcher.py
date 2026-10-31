"""Pure launcher contracts; these do not certify a live Linux/OpenAI sign-in."""
import errno
import importlib.util
from pathlib import Path
from unittest.mock import Mock

import pytest

WORKER=Path(__file__).resolve().parents[2]/'repair-worker'


def load(name):
    spec=importlib.util.spec_from_file_location('fixture_'+name,WORKER/(name+'.py'))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    return module


def elf(root,package,relative='bin/codex'):
    path=root/package/'vendor/x86_64-unknown-linux-musl'/relative
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_bytes(b'\x7fELF\x02\x01'+b'\x00'*12+b'\x3e\x00'+b'\x00'*44)
    path.chmod(0o755)
    return path


@pytest.mark.parametrize('package',['codex','codex-linux-x64','codex/node_modules/@openai/codex-linux-x64'])
def test_native_codex_resolution_uses_bundled_elf_without_a_node_wrapper(tmp_path,package):
    module=load('codex_binary')
    native=elf(tmp_path,package)
    assert module.resolve_binary(tmp_path)==native.resolve()


def test_optional_native_package_precedes_legacy_vendor_and_non_elf_fails_closed(tmp_path):
    module=load('codex_binary')
    legacy=elf(tmp_path,'codex');optional=elf(tmp_path,'codex-linux-x64')
    assert module.resolve_binary(tmp_path)==optional.resolve()
    optional.write_text('not an executable ELF')
    with pytest.raises(RuntimeError,match='native'):
        module.resolve_binary(tmp_path)


def test_legacy_native_layout_remains_supported_without_falling_back_to_node(tmp_path):
    module=load('codex_binary')
    native=elf(tmp_path,'codex',relative='codex/codex')
    assert module.resolve_binary(tmp_path)==native.resolve()


def test_supervisor_spawn_failure_preserves_errno_without_exposing_argv_or_error_text(monkeypatch,capsys):
    module=load('account_process')
    written=[]
    monkeypatch.setattr(module.ctypes,'CDLL',lambda *a,**k:type('Libc',(),{'prctl':staticmethod(lambda *a:0)})())
    monkeypatch.setattr(module.os,'getppid',lambda:123)
    monkeypatch.setattr(module.os,'write',lambda fd,data:written.append(data) or len(data))
    monkeypatch.setattr(module.signal,'signal',Mock())
    monkeypatch.setattr(module.subprocess,'Popen',Mock(side_effect=OSError(errno.EAGAIN,'private path and credential')))
    resources={'nproc_soft':'524288','nproc_hard':'524288','ancestor_limits':'unknown'}
    monkeypatch.setattr(module,'resource_snapshot',lambda:resources)
    assert module.supervise(['private argv'],7,123)==70
    receipt=b''.join(written)
    assert receipt.startswith(module.READY)
    assert module.parse_receipt(receipt[len(module.READY):])=={'stage':'exec','errno':errno.EAGAIN,'resources':resources}
    assert b'private' not in receipt
    assert 'private' not in capsys.readouterr().err


def test_only_the_private_supervisor_receipt_can_classify_a_local_launch_failure():
    module=load('account_failures')
    failure=module.account_failure('untrusted output',70,launcher_failure={'stage':'exec','errno':errno.EAGAIN})
    assert failure.diagnostics['reason']=='resource-limit'
    assert failure.diagnostics['phase']=='starting'
    assert failure.diagnostics['os_error']==errno.EAGAIN
    assert failure.diagnostics['launcher_stage']=='exec'
    assert 'OpenAI' in str(failure)
    ordinary=module.account_failure('untrusted output errno=11',70)
    assert ordinary.diagnostics['reason']=='unclassified'
    assert 'os_error' not in ordinary.diagnostics


def test_malformed_receipts_fail_closed_and_legacy_completion_stays_compatible():
    module=load('account_process')
    assert module.parse_receipt(module.RECEIPT) is None
    for data in (b'',b'forged',b'account-launch-failure:arbitrary:11\n'+module.RECEIPT):
        with pytest.raises(RuntimeError):module.parse_receipt(data)
    for stage,number in (([],11),('exec',True),('exec',4096)):
        with pytest.raises(ValueError):module.failure_receipt(stage,number)
