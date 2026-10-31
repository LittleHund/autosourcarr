"""Supervise trusted account commands only; this is not a source-code sandbox.

The controller supplies a fixed bundled Codex account command, a private home and
an empty environment. No repair editor, build script or source engine enters this
path. The completion receipt is written only after every owned child is reaped.
"""
from __future__ import annotations

import ctypes
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time


RECEIPT = b'account-children-drained\n'
READY = b'account-supervisor-ready\n'
MAX_RECEIPT = 1024
_RESOURCE_HELPER = None


def _resource_module():
    # -I excludes the script directory. Load this exact image-owned helper;
    # never add account HOME/CODEX_HOME or arbitrary paths to module lookup.
    global _RESOURCE_HELPER
    if _RESOURCE_HELPER is None:
        path = Path(__file__).with_name('account_resources.py')
        spec = importlib.util.spec_from_file_location('_account_resource_evidence', path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        _RESOURCE_HELPER = helper
    return _RESOURCE_HELPER


def resource_snapshot():
    try:
        return _resource_module().snapshot()
    except Exception:
        # Optional evidence must not replace the original failure/drain receipt.
        return None


def failure_receipt(stage, number, *, resources=None):
    if not isinstance(stage,str) or stage not in {'exec','parent'} or type(number) is not int or not 0 <= number <= 4095:
        raise ValueError('Invalid account launcher failure')
    frame = ('account-launch-failure:'+stage+':'+str(number)+'\n').encode('ascii')
    if resources is not None:
        checked = _resource_module().validate_resources(resources)
        frame += b'account-resources:1:' + json.dumps(checked, sort_keys=True, separators=(',', ':')).encode('ascii') + b'\n'
    frame += RECEIPT
    if len(frame) > MAX_RECEIPT:
        raise ValueError('Invalid account resource evidence')
    return frame


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate account resource field')
        result[key] = value
    return result


def parse_receipt(data):
    if type(data) is not bytes or len(data) > MAX_RECEIPT:
        raise RuntimeError('Account process cleanup was not confirmed')
    if data==RECEIPT:return None
    lines = data.split(b'\n')
    if len(lines) not in {3, 4} or lines[-2:] != [RECEIPT[:-1], b'']:
        raise RuntimeError('Account process cleanup was not confirmed')
    match = re.fullmatch(rb'account-launch-failure:(exec|parent):([0-9]{1,4})', lines[0])
    if not match or int(match[2]) > 4095:
        raise RuntimeError('Account process cleanup was not confirmed')
    result = {'stage':match[1].decode('ascii'),'errno':int(match[2])}
    if len(lines) == 4:
        prefix = b'account-resources:1:'
        try:
            if not lines[1].startswith(prefix):
                raise ValueError('Invalid resource frame')
            value = json.loads(lines[1][len(prefix):].decode('ascii'), object_pairs_hook=_unique_object)
            result['resources'] = _resource_module().validate_resources(value)
        except (ValueError, TypeError, UnicodeError, RecursionError, ImportError):
            raise RuntimeError('Account process cleanup was not confirmed') from None
    return result


def supervise(command, receipt_fd, parent_pid):
    libc = ctypes.CDLL(None, use_errno=True)
    # Linux unprivileged prctl operations; inherited no-new-privileges remains on.
    for option, value in ((38, 1), (36, 1), (1, signal.SIGTERM)):
        if libc.prctl(option, value, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), 'Account process protection failed')
    stopping = False

    def stop(*_):
        nonlocal stopping
        stopping = True

    for signum in (signal.SIGTERM, signal.SIGINT):
        signal.signal(signum, stop)
    os.write(receipt_fd, READY)
    # Cover parent loss before/while installing the parent-death guard.
    if os.getppid() != parent_pid:
        os.write(receipt_fd, failure_receipt('parent',0))
        return 70
    # Keep the receipt out of Codex and every descendant, so its stdout and exit
    # code cannot impersonate completed supervisor cleanup.
    if stopping:
        os.write(receipt_fd, RECEIPT)
        return 143
    try:
        child = subprocess.Popen(command, close_fds=True, stdin=subprocess.DEVNULL, start_new_session=True)
    except OSError as exc:
        number=exc.errno if type(exc.errno) is int and 0 <= exc.errno <= 4095 else 0
        try:
            receipt = failure_receipt('exec', number, resources=resource_snapshot())
        except Exception:
            receipt = failure_receipt('exec', number)
        os.write(receipt_fd, receipt)
        print('The bundled account executable could not start.',file=sys.stderr)
        return 70
    while not stopping:
        # Observe exit without reaping: the leader's PID stays reserved while
        # its group is signalled, including after its descendants are adopted.
        exited=os.waitid(os.P_PID,child.pid,os.WEXITED|os.WNOHANG|os.WNOWAIT)
        if exited is not None: break
        time.sleep(.02)
    # After the leader exits, orphans are adopted here rather than by the app.
    # The fixed account command owns its own process group. Never enumerate or
    # signal unrelated app processes. A child escaping that group cannot produce
    # a successful cleanup receipt; it keeps the account blocked for attention.
    try: os.killpg(child.pid,signal.SIGTERM)
    except ProcessLookupError: pass
    time.sleep(.1)
    try: os.killpg(child.pid,signal.SIGKILL)
    except ProcessLookupError: pass
    _,leader_status=os.waitpid(child.pid,0)
    child.returncode=os.waitstatus_to_exitcode(leader_status)
    started = time.monotonic()
    while True:
        try:
            pid, _ = os.waitpid(-1, os.WNOHANG)
        except ChildProcessError:
            os.write(receipt_fd, RECEIPT)
            return 143 if stopping else child.returncode
        if pid:
            continue
        elapsed = time.monotonic()-started
        if elapsed > 3:
            # No receipt: the controller retains the lock and saved credentials.
            return 75
        # No numeric group signals after reaping its identity. A detached child
        # keeps the receipt withheld and the account locked for attention.
        time.sleep(.02)


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if len(args) < 4 or args[2] != '--':
        raise ValueError('A supervised account command is required')
    descriptor, parent = int(args[0]), int(args[1])
    if descriptor < 3 or parent <= 0:
        raise ValueError('Invalid account process owner')
    return supervise(args[3:], descriptor, parent)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError):
        print('The bundled account process could not start or confirm cleanup.', file=sys.stderr)
        raise SystemExit(70)
