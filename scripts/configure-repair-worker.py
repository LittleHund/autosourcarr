#!/usr/bin/env python3
"""Opt in an existing TrueNAS Custom App to the private repair worker.

Preview by default. --apply builds images and updates the saved Custom App, so
future Save/Redeploy retains the worker. Never creates a competing Compose stack.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys

spec = importlib.util.spec_from_file_location('truenas_repair', Path(__file__).with_name('repair-truenas.py'))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)


def read_settings(path):
    values = dict(line.split('=', 1) for line in path.read_text().splitlines() if line and not line.startswith('#'))
    if not re.fullmatch('[0-9a-f]{64}', values.get('REPAIR_TOKEN', '')):
        raise ValueError('Invalid worker token; run repair-worker.sh setup')
    root = Path(values['REPAIR_ROOT'])
    if not root.is_absolute() or not (root/'auth').is_dir() or not (root/'locks').is_dir():
        raise ValueError('Repair data directory is unavailable; run setup first')
    if not re.fullmatch('[a-zA-Z0-9_-]+', values.get('REPAIR_NETWORK', '')):
        raise ValueError('Invalid private network name')
    return values


spec = importlib.util.spec_from_file_location('repair_setup', Path(__file__).resolve().parents[1]/'autosourcarr/app/repair_setup.py')
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)
transform = setup.transform


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app')
    parser.add_argument('--service', action='append', choices=('autosourcarr', 'private-autosourcarr'), help='Existing AutoSourcarr service to connect; repeat for both instances (default: autosourcarr)')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args(argv)
    project = Path(__file__).resolve().parents[1]
    values = read_settings(project/'config/repair-worker.env')
    app_name = args.app or base.discover_app()
    if not re.fullmatch('[a-z][a-z0-9-]{0,39}', app_name): raise ValueError('Invalid Custom App name')
    from truenas_api_client import Client
    with Client() as client:
        if not client.call('app.get_instance', app_name).get('custom_app'):
            raise ValueError('Only existing TrueNAS Custom Apps are supported')
        original = client.call('app.config', app_name)
        updated = transform(original, project, values, args.service)
        print('Add shared source repair controller and public-only egress proxy to '+app_name+'.')
        print('Connect application services: '+', '.join(args.service or ['autosourcarr']))
        print('Connected instances share repair job history and one serialized worker; live data stays separate.')
        print('Controller has Docker host access. Agent/build/test containers receive no socket or live-data mounts.')
        print('Existing app mounts, env files, ports, services and source data are retained. Applying redeploys the full Custom App.')
        if not args.apply:
            print('Preview only. Run with --apply when ready.')
            return
        base.run(['docker', 'compose', '-f', '-', 'config', '--quiet'], payload=json.dumps(updated))
        for tag, folder, extra in (
            ('autosourcarr:local', 'autosourcarr', []),
            ('autosourcarr-repair:0.3.25', 'repair-worker', []),
            ('autosourcarr-repair-codex:0.3.25', 'repair-worker', ['-f', str(project/'repair-worker/Dockerfile.codex')]),
        ):
            base.run(['docker', 'build', '-t', tag, *extra, str(project/folder)], timeout=1800, capture=False)
        for image in ('ghcr.io/cirruslabs/android-sdk:35', 'ghcr.io/suwayomi/suwayomi-server:v2.3.2243'):
            base.run(['docker', 'pull', image], timeout=600, capture=False)
        if client.call('app.config', app_name) != original: raise ValueError('Custom App configuration changed; preview again')
        state = client.call('app.get_instance', app_name)['state']
        if state in {'DEPLOYING', 'STOPPING'}: raise ValueError('A deployment is already in progress')
        base.secure_backup(project.parent/'truenas-recovery'/(app_name+'.before-source-repair.json'), original)
        base.wait_job(client, 'app.update', app_name, {'custom_compose_config': updated})
        if state == 'STOPPED': base.wait_job(client, 'app.start', app_name)
        print('Worker configured. Open AutoSourcarr → Sources → Source repairs → Setup and account to prepare tools and sign in.')


if __name__ == '__main__':
    try: main()
    except Exception as exc:
        print('Repair worker setup stopped: '+str(exc), file=sys.stderr)
        sys.exit(1)
