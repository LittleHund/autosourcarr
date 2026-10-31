#!/usr/bin/env python3
"""Preview or apply a narrow TrueNAS Custom App source-engine migration.

Intended installed path: project/scripts/repair-truenas.py. No host mutation occurs
without --apply. Uses the TrueNAS system Python API client, never Docker sockets
inside the application. Existing databases and env files are not edited.
"""
from __future__ import annotations

import argparse
import copy
import json
import os
from pathlib import Path
import posixpath
import re
import subprocess
import sys

SERVICE = 'autosourcarr'
OLD_SOURCE = 'autosourcarr-source-host'
ENGINE_DATA = '/home/suwayomi/.local/share/Tachidesk'
ENGINE_SCRIPT = '/home/suwayomi/startup_script.sh'
IMAGE = 'autosourcarr:local'


class RepairError(RuntimeError):
    pass


def mount_parts(mount):
    if isinstance(mount, dict):
        source, target = mount.get('source'), mount.get('target')
        if not isinstance(source, str) or not isinstance(target, str):
            raise RepairError('Anonymous or malformed mount needs manual review.')
        return source, posixpath.normpath(target), bool(mount.get('read_only'))
    if isinstance(mount, str):
        parts = mount.split(':')
        if len(parts) in (2, 3):
            return parts[0], posixpath.normpath(parts[1]), len(parts) == 3 and 'ro' in parts[2].split(',')
    raise RepairError('Anonymous or malformed mount needs manual review.')


def environment_mapping(value):
    if value is None:
        return {}
    if isinstance(value, dict):
        return dict(value)
    if isinstance(value, list) and all(isinstance(x, str) for x in value):
        output = {}
        for item in value:
            name, sep, val = item.partition('=')
            if name in output:
                raise RepairError('Duplicate environment keys need manual review.')
            output[name] = val if sep else None
        return output
    raise RepairError('Unsupported environment format.')


def mentions(value, service):
    if isinstance(value, str):
        return service in value
    if isinstance(value, dict):
        return any(mentions(k, service) or mentions(v, service) for k, v in value.items())
    if isinstance(value, list):
        return any(mentions(v, service) for v in value)
    return False


def transform(config, project: Path, *, source_data: str | None = None, source_user: str | None = None):
    """Pure transform; preserve unrelated services and all top-level settings."""
    if not isinstance(config, dict) or not isinstance(config.get('services'), dict):
        raise RepairError('Expected a complete Custom App Compose mapping.')
    services = config['services']
    if not isinstance(services.get(SERVICE), dict):
        raise RepairError('No unambiguous autosourcarr service in this Custom App.')
    result = copy.deepcopy(config)
    app = result['services'][SERVICE]
    old = services.get(OLD_SOURCE)
    if old is not None and not isinstance(old, dict):
        raise RepairError('Malformed source-host service.')
    if old is not None:
        for name, other in services.items():
            if name not in (SERVICE, OLD_SOURCE) and mentions(other, OLD_SOURCE):
                raise RepairError(f'Service {name!r} still references source-host; manual review required.')
        for key, value in config.items():
            if key != 'services' and mentions(value, OLD_SOURCE):
                raise RepairError('Top-level configuration references source-host; manual review required.')
    mounts = app.get('volumes', [])
    if not isinstance(mounts, list):
        raise RepairError('Unsupported AutoSourcarr mounts.')
    source_mounts = [m for m in mounts if mount_parts(m)[1] == ENGINE_DATA]
    old_source_mounts = [m for m in (old or {}).get('volumes', []) if mount_parts(m)[1] == ENGINE_DATA]
    if len(source_mounts) > 1 or len(old_source_mounts) > 1:
        raise RepairError('Multiple source-data mounts need manual review.')
    if source_mounts and old_source_mounts and mount_parts(source_mounts[0]) != mount_parts(old_source_mounts[0]):
        raise RepairError('Existing and old source-data mounts disagree; refusing to choose.')
    source_mount = (source_mounts or old_source_mounts or [None])[0]
    if source_data is not None:
        if not source_data.startswith('/') or '$' in source_data or ':' in source_data:
            raise RepairError('--source-data must be the absolute host directory, without a container target or variables.')
        source_data = posixpath.normpath(source_data)
        if source_data == '/':
            raise RepairError('--source-data must identify the retained source directory, not the filesystem root.')
        if source_mount is not None and posixpath.normpath(mount_parts(source_mount)[0]) != source_data:
            raise RepairError('--source-data conflicts with the retained mount in saved YAML; refusing to switch datasets.')
        if source_mount is None:
            source_mount = {'type': 'bind', 'source': source_data, 'target': ENGINE_DATA,
                            'bind': {'create_host_path': False}}
    if source_mount is None:
        raise RepairError('No existing source-data mount found. Supply --source-data /absolute/path/to/the/retained/source-directory; it must already exist and contain your source data.')
    source, _, readonly = mount_parts(source_mount)
    if readonly:
        raise RepairError('Source data is read-only; this migration cannot reuse it for the engine.')
    if not source.startswith('/') or '$' in source:
        raise RepairError('Use an existing absolute source-data bind mount; named/interpolated mounts need manual review.')
    kept_mounts = []
    for mount in mounts:
        _, target, _ = mount_parts(mount)
        if target in ('/app', '/deps', ENGINE_DATA):
            continue
        if target == '/' or target in ('/home', '/home/suwayomi', '/opt', '/usr') or target.startswith(('/app/', '/home/suwayomi/', '/opt/autosourcarr-venv', '/usr/')):
            raise RepairError(f'Mount at {target} can hide the integrated image; manual review required.')
        kept_mounts.append(mount)
    app['volumes'] = kept_mounts + [copy.deepcopy(source_mount)]
    app['image'] = IMAGE
    app['pull_policy'] = 'build'
    app['build'] = {'context': str(project.resolve() / 'autosourcarr')}
    app.pop('command', None)
    app.pop('entrypoint', None)
    app['working_dir'] = '/app'
    app['stop_grace_period'] = '45s'
    env = environment_mapping(app.get('environment'))
    env['SOURCE_ENGINE_MODE'] = 'integrated'
    # TrueNAS dataset ACLs need not match the mount root's Unix owner. Keep the
    # identity of the process that already owns the source database/preferences.
    previous_user = source_user if source_user is not None else (old or {}).get('user')
    if previous_user is not None:
        match = re.fullmatch(r'([1-9][0-9]*)(?::([1-9][0-9]*))?', str(previous_user))
        if not match:
            raise RepairError('Source user must have a non-root numeric UID/GID, such as --source-user 568:568.')
        for key, value in [('SOURCE_ENGINE_UID', match[1]), ('SOURCE_ENGINE_GID', match[2] or match[1])]:
            if source_user is not None or not env.get(key) or str(env[key]) == '0':
                env[key] = value
    if '/deps' in str(env.get('PYTHONPATH', '')):
        env.pop('PYTHONPATH', None)
    app['environment'] = env
    depends = app.get('depends_on')
    if isinstance(depends, dict):
        depends.pop(OLD_SOURCE, None)
    elif isinstance(depends, list):
        app['depends_on'] = [x for x in depends if x != OLD_SOURCE]
    elif depends is not None:
        raise RepairError('Unsupported dependency format.')
    # Other Docker link modes must be reviewed explicitly, not silently rewritten.
    for key in ('links', 'volumes_from', 'network_mode', 'ipc', 'pid'):
        if mentions(app.get(key), OLD_SOURCE):
            raise RepairError(f'AutoSourcarr {key} still references source-host; manual review required.')
    result['services'].pop(OLD_SOURCE, None)
    return result


def run(args, *, payload=None, timeout=60, capture=True):
    try:
        result = subprocess.run(args, input=payload, text=True, capture_output=capture,
                                timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RepairError(f'{args[0]} failed or timed out; no success can be assumed.') from exc
    if result.returncode:
        # Do not echo config/env data from command errors.
        raise RepairError(f'{args[0]} failed (exit {result.returncode}); inspect host service/build logs.')
    return result.stdout if capture else ''


def discover_app():
    output = run(['docker', 'ps', '-a', '--filter', f'label=com.docker.compose.service={SERVICE}',
                  '--format', '{{.Label "com.docker.compose.project"}}'])
    projects = {p.strip() for p in output.splitlines() if p.strip()}
    if len(projects) != 1:
        raise RepairError('Could not identify exactly one Custom App; provide --app NAME.')
    project = projects.pop()
    if not project.startswith('ix-'):
        raise RepairError('Container is not managed by a TrueNAS Custom App; provide the correct --app NAME.')
    return project[3:]


def secure_backup(path, config):
    """Never overwrite the first recovery copy, including a symlink target."""
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    except FileExistsError:
        if path.is_symlink() or not path.is_file():
            raise RepairError('Existing backup path is not a regular file.')
        return False
    with os.fdopen(descriptor, 'w') as stream:
        json.dump(config, stream, indent=2)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    return True


def wait_job(client, method, *args, seconds=1250):
    job = client.call(method, *args, job='RETURN')
    if not job.event.wait(seconds):
        raise RepairError(f'TrueNAS job {job.job_id} is still pending. Do not retry or roll back until its outcome is known.')
    return job.result()


def apply(client, app_name, original, updated, project, backup):
    # Verify the retained directory before Docker can create a replacement bind.
    retained = next(m for m in updated['services'][SERVICE]['volumes'] if mount_parts(m)[1] == ENGINE_DATA)
    source_path = Path(mount_parts(retained)[0])
    if not source_path.is_dir() or not any(source_path.iterdir()):
        raise RepairError('Retained source directory is missing or empty; refusing to initialize replacement data.')
    consumers = run(['docker', 'ps', '--filter', f'volume={source_path}', '--format',
                     '{{.ID}} {{.Label "com.docker.compose.project"}} {{.Label "com.docker.compose.service"}}'])
    for consumer in consumers.splitlines():
        fields = consumer.split()
        if len(fields) != 3 or fields[1] != 'ix-' + app_name or fields[2] not in (SERVICE, OLD_SOURCE):
            raise RepairError('A running container outside this migration uses the source directory. Stop its source engine before applying.')
    if not (project / 'autosourcarr' / 'Dockerfile').is_file():
        raise RepairError('Project build context does not contain the integrated Dockerfile.')
    # JSON is valid YAML. Validate quietly; do not print secrets resolved by Compose.
    try:
        run(['docker', '--config', '/etc/docker', 'compose', '-p', 'ix-' + app_name,
             '-f', '-', 'config', '--quiet'], payload=json.dumps(updated))
    except RepairError as exc:
        raise RepairError('Compose validation failed before changing the Custom App. Check its retained env_file/mount paths and Docker Compose availability.') from exc
    run(['docker', '--config', '/etc/docker', 'build', '-t', IMAGE,
         str(project / 'autosourcarr')], timeout=1200, capture=False)
    secure_backup(backup, original)
    if client.call('app.config', app_name) != original:
        raise RepairError('Custom App configuration changed during preparation; rerun preview before applying.')
    state = client.call('app.get_instance', app_name)['state']
    if state in ('DEPLOYING', 'STOPPING'):
        raise RepairError('Custom App deployment is already in progress.')
    wait_job(client, 'app.update', app_name, {'custom_compose_config': updated})
    if state == 'STOPPED':
        wait_job(client, 'app.start', app_name)
    containers = run(['docker', 'ps', '--filter', f'label=com.docker.compose.project=ix-{app_name}',
                      '--filter', f'label=com.docker.compose.service={SERVICE}', '--format', '{{.ID}}']).split()
    if len(containers) != 1:
        raise RepairError('Deployment finished without exactly one running AutoSourcarr container; inspect app logs.')
    run(['docker', 'exec', containers[0], 'test', '-f', ENGINE_SCRIPT])
    print('Integrated runtime is present in the running container. Engine readiness is not yet verified; check Status.')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', help='Existing TrueNAS Custom App name (not its ix- project label).')
    parser.add_argument('--source-data', help='Existing absolute host source-data directory, if its old mount was removed from saved YAML.')
    parser.add_argument('--source-user', help='Previous numeric source process UID[:GID], e.g. 568:568; explicitly sets the integrated child identity.')
    parser.add_argument('--apply', action='store_true', help='Build, back up and update the existing Custom App; restarts its full stack.')
    args = parser.parse_args(argv)
    project = Path(__file__).resolve().parents[1]
    app_name = args.app or discover_app()
    if not re.fullmatch(r'[a-z][a-z0-9-]{0,39}', app_name):
        raise RepairError('Invalid Custom App name.')
    try:
        from truenas_api_client import Client
    except ImportError as exc:
        raise RepairError('Run with the TrueNAS host system python3; its installed API client is required.') from exc
    with Client() as client:
        instance = client.call('app.get_instance', app_name)
        if not instance.get('custom_app'):
            raise RepairError('Only existing Custom Apps are supported.')
        original = client.call('app.config', app_name)
        updated = transform(original, project, source_data=args.source_data, source_user=args.source_user)
        print(f'Custom App: {app_name}. AutoSourcarr will use {IMAGE} with automatic source builds.')
        print('Existing data/config mounts, ports, networks and unrelated service definitions are retained.')
        print('Applying restarts the full Custom App stack. The old source-host service is removed when present.')
        if not args.apply:
            print('Preview only. Rerun with --apply to perform this migration.')
            return 0
        backup = project.parent / 'truenas-recovery' / (app_name + '.before-integrated.json')
        apply(client, app_name, original, updated, project, backup)
        print(f'Original configuration backup: {backup}')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except RepairError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
    except Exception as exc:
        print(f'TrueNAS operation failed ({type(exc).__name__}). Inspect Apps job logs before retrying; configuration may already have changed.', file=sys.stderr)
        raise SystemExit(1)
