"""Draft internal repair sandbox; runtime operations require successful preflight.

No Docker socket or user-supplied flags. The caller owns unique work/socket dirs
and the public-only host Unix proxy; this module only exposes those capabilities.
"""
from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile

from resource_budget import diagnostics as resource_diagnostics


SYSTEM_DIRS = ('/usr', '/bin', '/sbin', '/lib', '/lib64')
SYSTEM_FILES = ('/etc/passwd', '/etc/group', '/etc/nsswitch.conf',
                '/etc/ld.so.cache', '/etc/localtime', '/etc/timezone')
SYSTEM_CONFIG_DIRS = ('/etc/ssl', '/etc/alternatives', '/etc/fonts', '/etc/java-21-openjdk')


def owned_directory(path: str | Path) -> Path:
    """Refuse symlink roots; caller chooses directories, never a repair request."""
    value = Path(path).absolute()
    if value == Path('/') or value.is_symlink() or not value.is_dir():
        raise ValueError('Sandbox directory must exist and must not be a symbolic link')
    # Do not expose a symlink parent leading to live configuration accidentally.
    if any(parent.is_symlink() for parent in value.parents):
        raise ValueError('Sandbox directory contains a symbolic-link parent')
    return value.resolve(strict=True)


def terminate_group(process: subprocess.Popen, grace: float = 5.0) -> None:
    """Wait for the owned bubblewrap monitor; PID-namespace teardown kills children.

    Caller must launch with start_new_session=True. No broad process-name kills.
    --die-with-parent is an independent guard if the controller itself disappears.
    """
    if process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=grace)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=5.0)


@dataclass(frozen=True)
class Sandbox:
    code_root: Path
    # Absolute interpreter already bundled into image; never take from job input.
    python: str = '/usr/bin/python3'
    bwrap: str = '/usr/bin/bwrap'
    # Bundle paths, not user-provided mounts. JRE may be /opt/java/openjdk; SDK
    # is /opt/android-sdk-linux. Python virtualenv and Node can be included here.
    tool_roots: tuple[str, ...] = ('/opt/java', '/opt/android-sdk-linux',
                                 '/opt/autosourcarr-venv', '/opt/node')

    def command(self, work, command: list[str], *, sockets=None, auth=None, ai_socket=None,
                engine_profile=None, engine_home='/home/suwayomi',
                env: dict[str, str] | None = None, bridge=True, sync_fd=None) -> list[str]:
        if not command or not all(isinstance(x, str) and '\x00' not in x for x in command):
            raise ValueError('Sandbox command must be an explicit argument list')
        work_path = owned_directory(work)
        code_path = owned_directory(self.code_root)
        # Explicit namespace list avoids host cgroup dependencies. All five are
        # mandatory: never use --unshare-*-try and never --share-net.
        argv = [self.bwrap, '--unshare-user', '--unshare-pid', '--unshare-net',
                '--unshare-ipc', '--unshare-uts', '--die-with-parent', '--new-session',
                '--clearenv', '--cap-drop', 'ALL']
        if sync_fd is not None:
            if not isinstance(sync_fd, int) or sync_fd < 3:
                raise ValueError('Invalid sandbox lifetime file descriptor')
            argv += ['--sync-fd', str(sync_fd)]
        for name in SYSTEM_DIRS + self.tool_roots:
            if Path(name).exists():
                argv += ['--ro-bind', name, name]
        argv += ['--dir', '/etc']
        for name in SYSTEM_CONFIG_DIRS + SYSTEM_FILES:
            if Path(name).exists():
                argv += ['--ro-bind', name, name]
        argv += ['--proc', '/proc', '--dev', '/dev', '--tmpfs', '/tmp',
                 '--tmpfs', '/run', '--tmpfs', '/home', '--dir', '/home/worker',
                 '--ro-bind', str(code_path), '/sandbox-tools',
                 '--bind', str(work_path), '/work', '--chdir', '/work']
        # A throwaway engine gets the bundled launch scripts/JAR, never the
        # source profile of the main app. Only these named bundled paths exist.
        if engine_profile is not None:
            profile = owned_directory(engine_profile)
            for name in ('startup', 'startup_script.sh', 'create_server_conf.sh'):
                source = Path(engine_home)/name
                if not source.exists():
                    raise ValueError('Bundled source-test runtime is incomplete')
                argv += ['--ro-bind', str(source), str(source)]
            argv += ['--bind', str(profile), engine_home+'/.local/share/Tachidesk']
        if auth is not None:
            argv += ['--bind', str(owned_directory(auth)), '/home/worker/.codex']
        if ai_socket is not None:
            if auth is not None: raise ValueError('Custom AI must not receive the ChatGPT account')
            import stat
            ai_path = Path(ai_socket)
            owned_directory(ai_path.parent)
            if ai_path.is_symlink() or not stat.S_ISSOCK(ai_path.stat().st_mode): raise ValueError('Invalid AI relay socket')
            argv += ['--ro-bind', str(ai_path), '/run/ai.sock']
        if sockets is not None:
            sockpath = owned_directory(sockets)
            # AF_UNIX sockaddr limits also apply on host, where long data roots
            # are common. Runtime sockets should use a short private /tmp path.
            if len(os.fsencode(str(sockpath/'engine.sock'))) >= 104:
                raise ValueError('Sandbox socket path is too long')
            argv += ['--ro-bind', str(sockpath), '/run/repair']
            if engine_profile is not None:
                gateway=owned_directory(Path(engine_profile).parent/'gateway')
                argv += ['--bind', str(gateway), '/run/gateway']
        values = {'HOME': '/home/worker', 'CODEX_HOME': '/home/worker/.codex',
                  'PATH': '/opt/autosourcarr-venv/bin:/opt/node/bin:/opt/java/openjdk/bin:/usr/local/bin:/usr/bin:/bin',
                  'TMPDIR': '/tmp', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
                  'GIT_TERMINAL_PROMPT': '0', 'PYTHONDONTWRITEBYTECODE': '1',
                  'HTTP_PROXY': 'http://127.0.0.1:8080', 'HTTPS_PROXY': 'http://127.0.0.1:8080',
                  'http_proxy': 'http://127.0.0.1:8080', 'https_proxy': 'http://127.0.0.1:8080',
                  'NO_PROXY': '127.0.0.1,localhost', 'no_proxy': '127.0.0.1,localhost'}
        values.update(env or {})  # Only trusted controller call sites supply env.
        for key, value in values.items():
            if not isinstance(value, str) or '\x00' in key+value:
                raise ValueError('Invalid sandbox environment')
            argv += ['--setenv', key, value]
        if bridge:
            if sockets is None:
                raise ValueError('Sandbox network bridge requires a private socket directory')
            argv += ['--', self.python, '-I', '/sandbox-tools/resource_budget.py', '--',
                     self.python, '-I', '/sandbox-tools/sandbox_bridge.py',
                     '--proxy-socket', '/run/repair/egress.sock']
            if engine_profile is not None:
                argv += ['--engine-socket', '/run/gateway/engine.sock']
            if ai_socket is not None:
                argv += ['--ai-socket', '/run/ai.sock']
            argv += ['--', *command]
        else:
            argv += ['--', self.python, '-I', '/sandbox-tools/resource_budget.py', '--', *command]
        return argv

    def probe(self) -> dict:
        """Exercise actual namespaces/mounts; availability is not a file check."""
        if not Path(self.bwrap).is_file():
            return {'available': False, 'reason':'runtime-missing', 'message': 'Bundled sandbox tools are missing. Rebuild the AutoSourcarr image.'}
        try:
            with tempfile.TemporaryDirectory(prefix='asr-probe-') as temporary:
                root = Path(temporary); (root/'work').mkdir()
                (root/'private-canary').write_text('not exposed')
                source = (
                    'import os,pathlib,socket,json,resource\n'
                    'assert resource.getrlimit(resource.RLIMIT_AS)[0] <= 8*1024**3\n'
                    'assert resource.getrlimit(resource.RLIMIT_NOFILE)[0] <= 1024\n'
                    'assert resource.getrlimit(resource.RLIMIT_FSIZE)[0] <= 512*1024**2\n'
                    'assert resource.getrlimit(resource.RLIMIT_NPROC)[0] <= 1024\n'
                    f'assert not pathlib.Path({str(root / "private-canary")!r}).exists()\n'
                    'assert not pathlib.Path("/config").exists()\n'
                    'assert not pathlib.Path("/data").exists()\n'
                    'assert not pathlib.Path("/var/run/docker.sock").exists()\n'
                    's=pathlib.Path("/proc/self/status").read_text()\n'
                    'values=dict(line.split(":",1) for line in s.splitlines() if ":" in line)\n'
                    'assert int(values["CapEff"].strip(),16)==0\n'
                    'assert values["NoNewPrivs"].strip()=="1"\n'
                    'assert socket.if_nameindex()==[(1,"lo")]\n'
                    'pathlib.Path("/work/probe").write_text("ok")\n'
                    'print(json.dumps({"uid":os.getuid(),"isolated":True}))\n')
                process = subprocess.Popen(self.command(root/'work', [self.python, '-I', '-c', source], bridge=False),
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                           start_new_session=True, env={'PATH': '/usr/bin:/bin'})
                try:
                    out, stderr = process.communicate(timeout=15)
                except subprocess.TimeoutExpired:
                    terminate_group(process)
                    return {'available': False, 'reason':'check-timeout', 'message': 'Repair sandbox self-check timed out; normal app functions remain available.'}
                if process.returncode != 0 or not (root/'work/probe').is_file():
                    return isolation_failure(stderr.decode(errors='replace'),process.returncode)
                result = json.loads(out)
                return {'available': result.get('isolated') is True, 'message': 'Internal repair isolation is available.', 'checks':host_checks(), 'resources':resource_diagnostics()}
        except (OSError, ValueError, subprocess.SubprocessError) as exc:
            return isolation_failure(str(exc))


def proc_mount_checks():
    """Count known Docker proc overrides; never return raw mountinfo/host paths."""
    result = {'proc_masked_paths': 'not reported', 'proc_readonly_paths': 'not reported'}
    masks = {'/proc/acpi', '/proc/asound', '/proc/interrupts', '/proc/kcore', '/proc/keys',
             '/proc/latency_stats', '/proc/scsi', '/proc/timer_list', '/proc/timer_stats', '/proc/sched_debug'}
    readonly = {'/proc/bus', '/proc/fs', '/proc/irq', '/proc/sys', '/proc/sysrq-trigger'}
    try:
        with Path('/proc/self/mountinfo').open(encoding='utf-8', errors='replace') as stream:
            text = stream.read(65537)
        if len(text) > 65536:
            return result
        masked_paths, readonly_paths, proc_seen = set(), set(), False
        for line in text.splitlines():
            fields = line.split()
            if len(fields) < 10 or '-' not in fields[6:]:
                continue
            separator = fields.index('-', 6)
            if separator + 3 >= len(fields):
                continue
            target, fs = fields[4], fields[separator + 1]
            if target == '/proc' and fs == 'proc':
                proc_seen = True
            if target in masks and fs != 'proc':
                masked_paths.add(target)
            if target in readonly and fs == 'proc' and 'ro' in fields[5].split(','):
                readonly_paths.add(target)
        return {'proc_masked_paths': str(len(masked_paths)), 'proc_readonly_paths': str(len(readonly_paths))} if proc_seen else result
    except OSError:
        return result


def host_checks():
    checks={}
    for name,path in [('apparmor','/proc/self/attr/current'),('max_user_namespaces','/proc/sys/user/max_user_namespaces'),('unprivileged_userns_clone','/proc/sys/kernel/unprivileged_userns_clone'),
                      ('apparmor_restrict_unprivileged_userns','/proc/sys/kernel/apparmor_restrict_unprivileged_userns'),
                      ('apparmor_restrict_unprivileged_userns_force','/proc/sys/kernel/apparmor_restrict_unprivileged_userns_force')]:
        try: checks[name]=Path(path).read_text()[:250].strip()
        except OSError: checks[name]='not reported'
    try:
        status=dict(line.split(':',1) for line in Path('/proc/self/status').read_text().splitlines() if ':' in line)
        checks.update({k:status.get(k,'not reported').strip() for k in ('Seccomp','NoNewPrivs')})
    except OSError: pass
    checks.update(proc_mount_checks())
    return checks


def isolation_failure(stderr, code=None, checks=None):
    from contracts import safe_text
    detail=safe_text(str(stderr),1800)
    checks=host_checks() if checks is None else checks
    low=detail.lower()
    reason='self-check'
    message='Repair sandbox self-check failed. Expand details for the exact failure. No repair code was run.'
    if checks.get('NoNewPrivs')=='0':
        reason='container-policy';message='This app process did not enable no-new-privileges. Rebuild/redeploy the current integrated image using its normal start command, then check repair tools again.'
    elif checks.get('max_user_namespaces')=='0' or checks.get('unprivileged_userns_clone')=='0':
        reason='host-userns';message='The host disables unprivileged user namespaces. A host administrator must enable them before repair can run.'
    elif 'namespace' in low and any(word in low for word in ('permitted','denied','not supported','no permissions','permission denied')):
        reason='namespace-denied';message='The host denied source-repair namespace creation. This blocks code execution with every provider. ChatGPT sign-in and API connection checks remain available without Compose changes.'
    elif 'mount' in low and ('permitted' in low or 'denied' in low):
        reason='mount-denied';message='The container blocked sandbox mounts. Check the supplied seccomp policy and active host mount restrictions.'
        if "can't mount proc " in low or 'cannot mount proc ' in low:
            message='The sandbox could not mount its private /proc. Docker masked/read-only proc paths can cause this even when the AutoSourcarr AppArmor profile is active. See Host permissions for the scoped system-path setting.'
    elif 'no such file' in low or 'execvp' in low:
        reason='runtime-missing';message='A bundled repair tool could not start. Rebuild this release image; see details for the missing runtime.'
    result={'available':False,'reason':reason,'message':message,'exit_code':code,'details':detail,'checks':checks}
    if reason in {'container-policy','host-userns','namespace-denied','mount-denied'}:
        steps=[]
        if checks.get('apparmor','').startswith('docker-default'):
            steps.append('Docker default AppArmor is active. On the host, load security/autosourcarr-internal.apparmor and select apparmor=autosourcarr-internal for this AutoSourcarr service.')
        if reason=='mount-denied' and ("can't mount proc " in low or 'cannot mount proc ' in low):
            steps.append('For this private /proc mount denial, add systempaths=unconfined to the shared AutoSourcarr security_opt block in the saved Compose, then Save/Redeploy. This removes Docker default /proc and /sys path masks; retain the non-root user, cap_drop: [ALL], and supplied seccomp/AppArmor policies. The host guide includes verification; no parent /proc fallback is used.')
        steps.extend([
            'Select security/repair-seccomp.json in the shared AutoSourcarr security block, then Save/Redeploy to recreate the containers. Seccomp=2 reports filtering, not which rules Docker applied.',
            'Run Check repair tools again. If namespaces are still denied, the host administrator must inspect the active container security options and the host denial log from that check. Positive namespace limits do not establish permission; missing settings are not reported, not disabled.',
        ])
        result.update(host_setup=True,steps=steps,api_checks_available=True,account_setup_available=True)
    return result


class SyncPipe:
    """Witness bwrap PID1 exit, including all of its namespace descendants.

    Usage::
        with SyncPipe() as lifetime:
            args = sandbox.command(..., sync_fd=lifetime.write_fd)
            child = subprocess.Popen(args, pass_fds=lifetime.pass_fds,
                                     start_new_session=True, ...)
            lifetime.spawned()  # Drop controller's copy of the write endpoint.
            try:
                ...
            finally:
                terminate_group(child)
                lifetime.wait_eof()  # Must precede artifact use/account unlock.

    In an async command, call wait_eof through asyncio.to_thread and shield/drain
    cleanup before releasing the account lock. Do not turn its error into success.
    """
    def __init__(self):
        self.read_fd, self.write_fd = os.pipe2(os.O_CLOEXEC)
        self._spawned = False

    @property
    def pass_fds(self):
        if self.write_fd is None:
            raise RuntimeError('Sandbox lifetime pipe is already handed off')
        return (self.write_fd,)

    def attach(self, args):
        """Alternative to factory sync_fd, for an already constructed argv."""
        if not args or Path(args[0]).name != 'bwrap' or '--sync-fd' in args:
            raise ValueError('Lifetime pipe requires one bubblewrap command')
        return [args[0], '--sync-fd', str(self.write_fd), *args[1:]]

    def spawned(self):
        if self.write_fd is not None:
            os.close(self.write_fd)
            self.write_fd = None
        self._spawned = True

    def wait_eof(self, timeout=10.0):
        import select
        import time
        if not self._spawned or self.read_fd is None:
            raise RuntimeError('Sandbox lifetime was not handed to a process')
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline-time.monotonic()
            if remaining <= 0 or not select.select([self.read_fd], [], [], remaining)[0]:
                raise RuntimeError('Sandbox processes could not be confirmed stopped')
            if not os.read(self.read_fd, 4096):
                return

    def close(self):
        for name in ('read_fd', 'write_fd'):
            value = getattr(self, name)
            if value is not None:
                os.close(value)
                setattr(self, name, None)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
