"""Portable policy tests; actual Linux limits/native sign-in need their own checks."""
import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import Mock

import pytest

WORKER = Path(__file__).resolve().parents[2] / 'repair-worker'


def worker_module(monkeypatch, name):
    monkeypatch.syspath_prepend(str(WORKER))
    spec = importlib.util.spec_from_file_location('fixture_' + name + '_v0338', WORKER / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    monkeypatch.setitem(sys.modules, spec.name, module)
    spec.loader.exec_module(module)
    return module


def budget_module(monkeypatch, tasks=(524288, 524288)):
    limits = {0: (-1, -1), 1: (65536, 65536), 2: (-1, -1), 3: tasks}
    writes = []
    def set_limit(name, values):
        writes.append((name, values))
        limits[name] = values
    fake = SimpleNamespace(RLIMIT_AS=0, RLIMIT_NOFILE=1, RLIMIT_FSIZE=2,
                           RLIMIT_NPROC=3, RLIM_INFINITY=-1,
                           getrlimit=lambda name: limits[name], setrlimit=set_limit)
    monkeypatch.setitem(sys.modules, 'resource', fake)
    spec = importlib.util.spec_from_file_location('fixture_budget_v0338', WORKER / 'resource_budget.py')
    module = importlib.util.module_from_spec(spec)
    monkeypatch.setitem(sys.modules, spec.name, module)
    spec.loader.exec_module(module)
    return module, limits, writes


@pytest.mark.parametrize('inherited', [(524288, 524288), (-1, -1), (512, 1024)])
def test_trusted_account_preserves_both_inherited_uid_task_ceilings(monkeypatch, inherited):
    module, limits, writes = budget_module(monkeypatch, inherited)
    module.apply_limits(trusted_account=True)
    assert limits[3] == inherited
    assert all(name != 3 for name, values in writes)
    assert limits[0] == (8 * 1024**3,) * 2
    assert limits[1] == (1024, 1024)
    assert limits[2] == (512 * 1024**2,) * 2


def test_default_source_repair_still_lowers_the_uid_task_limit(monkeypatch):
    module, limits, writes = budget_module(monkeypatch)
    module.apply_limits()
    assert limits[3] == (1024, 1024)
    assert (3, (1024, 1024)) in writes


def test_trusted_launcher_mode_requires_its_own_option_before_the_separator(monkeypatch):
    module, limits, _ = budget_module(monkeypatch)
    launched = []
    monkeypatch.setattr(module.os, 'execvpe', lambda program, args, env: launched.append((program, args)))
    module.main(['--trusted-account', '--', 'fixture-native', 'login', '--device-auth'])
    assert launched == [('fixture-native', ['fixture-native', 'login', '--device-auth'])]
    assert limits[3] == (524288, 524288)


def test_command_text_cannot_accidentally_select_trusted_policy(monkeypatch):
    module, limits, _ = budget_module(monkeypatch)
    launched = []
    monkeypatch.setattr(module.os, 'execvpe', lambda program, args, env: launched.append((program, args)))
    module.main(['--', 'fixture-command', '--trusted-account'])
    assert launched == [('fixture-command', ['fixture-command', '--trusted-account'])]
    assert limits[3] == (1024, 1024)


def test_trusted_mode_without_separator_does_not_launch_anything(monkeypatch):
    module, _, _ = budget_module(monkeypatch)
    launched = []
    monkeypatch.setattr(module.os, 'execvpe', lambda *args: launched.append(True))
    with pytest.raises(ValueError):
        module.main(['--trusted-account', 'fixture-command'])
    assert not launched


def test_private_failure_receipt_carries_exact_safe_limits_and_legacy_frames(monkeypatch):
    process = worker_module(monkeypatch, 'account_process')
    details = {'nproc_soft': '9007199254740993', 'nproc_hard': 'unlimited',
               'cgroup_pids_current': '512', 'cgroup_pids_max': '512',
               'cgroup_scope': 'visible-v2-only', 'ancestor_limits': 'unknown'}
    frame = process.failure_receipt('exec', 11, resources=details)
    decoded = process.parse_receipt(frame)
    assert decoded == {'stage': 'exec', 'errno': 11, 'resources': details}
    failure = worker_module(monkeypatch, 'account_failures').launcher_account_failure(decoded, 70)
    assert failure.diagnostics['reason'] == 'resource-limit'
    assert failure.diagnostics['resources'] == details
    assert process.parse_receipt(process.failure_receipt('exec', 11)) == {'stage': 'exec', 'errno': 11}
    assert process.parse_receipt(process.RECEIPT) is None


@pytest.mark.parametrize('details', [
    {'nproc_soft': True}, {'nproc_soft': 12}, {'nproc_soft': '12\n'},
    {'nproc_soft': '18446744073709551616'}, {'nproc_soft': '１２'},
    {'cgroup_pids_current': 'unlimited'}, {'cgroup_scope': '/private/path'},
    {'private_password': 'DO_NOT_EXPORT'},
])
def test_untrusted_resource_fields_cannot_enter_private_receipts(monkeypatch, details):
    process = worker_module(monkeypatch, 'account_process')
    with pytest.raises(ValueError):
        process.failure_receipt('exec', 11, resources=details)


@pytest.mark.parametrize('payload', [b'[]', b'{', b'{"nproc_soft":true}',
    b'{"nproc_soft":"12","nproc_soft":"13"}', b'{"private_password":"DO_NOT_EXPORT"}',
    b'{"nproc_soft":"' + b'1' * 1024 + b'"}', b'{"nproc_soft":"12"}\nextra'])
def test_invalid_resource_frames_cannot_confirm_cleanup(monkeypatch, payload):
    process = worker_module(monkeypatch, 'account_process')
    frame = b'account-launch-failure:exec:11\naccount-resources:1:' + payload + b'\n' + process.RECEIPT
    with pytest.raises(RuntimeError, match='cleanup was not confirmed'):
        process.parse_receipt(frame)


def test_optional_probe_failure_keeps_original_errno_and_legacy_drain_receipt(monkeypatch, capsys):
    process = worker_module(monkeypatch, 'account_process')
    written = []
    monkeypatch.setattr(process.ctypes, 'CDLL', lambda *a, **k: SimpleNamespace(prctl=lambda *a: 0))
    monkeypatch.setattr(process.os, 'getppid', lambda: 123)
    monkeypatch.setattr(process.os, 'write', lambda fd, data: written.append(data) or len(data))
    monkeypatch.setattr(process.signal, 'signal', Mock())
    monkeypatch.setattr(process.subprocess, 'Popen', Mock(side_effect=OSError(11, 'private original path')))
    monkeypatch.setattr(process, 'resource_snapshot', Mock(side_effect=OSError('private probe path')))
    assert process.supervise(['private argv'], 7, 123) == 70
    frame = b''.join(written)[len(process.READY):]
    assert process.parse_receipt(frame) == {'stage': 'exec', 'errno': 11}
    assert b'private' not in frame and 'private' not in capsys.readouterr().err


def test_snapshot_reads_but_never_changes_limits_and_preserves_zero_and_large_counters(monkeypatch):
    _, _, writes = budget_module(monkeypatch)
    module = worker_module(monkeypatch, 'account_resources')
    kernel = {
        '/proc/self/cgroup': '0::/slice/own\n',
        '/proc/self/mountinfo': '36 25 0:28 /slice/own /sys/fs/cgroup ro shared:1 - cgroup2 cgroup rw\n',
        '/sys/fs/cgroup/pids.current': '0\n', '/sys/fs/cgroup/pids.max': 'max\n',
        '/sys/fs/cgroup/memory.current': '9007199254740993\n',
        '/sys/fs/cgroup/memory.max': '18446744073709551616\n',
    }
    monkeypatch.setattr(module, '_read_limited', lambda path, limit: kernel.get(path))
    result = module.snapshot()
    assert not writes
    assert result['nproc_soft'] == '524288' and result['address_space_hard_bytes'] == 'unlimited'
    assert result['cgroup_pids_current'] == '0' and result['cgroup_pids_max'] == 'unlimited'
    assert result['cgroup_memory_current_bytes'] == '9007199254740993'
    assert result['cgroup_memory_max_bytes'] == 'unknown'
    assert result['ancestor_limits'] == 'unknown' and result['cgroup_scope'] == 'visible-v2-only'
    assert not any('/' in item for item in result.values())


@pytest.mark.parametrize('member,mount_root,expected', [
    ('/', '/', '/sys/fs/cgroup'), ('/slice/own', '/', '/sys/fs/cgroup/slice/own'),
    ('/slice/own', '/slice/own', '/sys/fs/cgroup'),
    ('/slice/own', '/other', None), ('/slice/../own', '/', None),
    ('/slice/own (deleted)', '/', None), ('relative', '/', None),
])
def test_visible_cgroup_location_never_falls_back_to_an_unrelated_root(monkeypatch, member, mount_root, expected):
    module = worker_module(monkeypatch, 'account_resources')
    mounts = f'36 25 0:28 {mount_root} /sys/fs/cgroup ro unknown:1 - cgroup2 cgroup rw\n'
    assert module.cgroup_location('0::' + member + '\n', mounts) == expected


def test_cgroup_move_during_snapshot_makes_the_counters_unknown(monkeypatch):
    module = worker_module(monkeypatch, 'account_resources')
    reads = 0
    def read(path, limit):
        nonlocal reads
        if path == '/proc/self/cgroup':
            reads += 1
            return '0::/\n' if reads == 1 else '0::/other\n'
        if path == '/proc/self/mountinfo':
            return '36 25 0:28 / /sys/fs/cgroup ro - cgroup2 cgroup rw\n'
        return '512\n'
    monkeypatch.setattr(module, '_read_limited', read)
    result = module.snapshot()
    assert result['cgroup_pids_current'] == 'unknown' and result['cgroup_scope'] == 'unknown'


def test_probe_closes_an_acquired_descriptor_if_stream_setup_fails(monkeypatch):
    module = worker_module(monkeypatch, 'account_resources')
    close = Mock()
    monkeypatch.setattr(module, 'os', SimpleNamespace(O_RDONLY=0, O_NOFOLLOW=0, O_NONBLOCK=0, O_CLOEXEC=0,
        open=lambda *args: 123, fdopen=Mock(side_effect=OSError('synthetic stream setup failure')), close=close))
    assert module._read_limited('/proc/self/cgroup', 4096) is None
    close.assert_called_once_with(123)
