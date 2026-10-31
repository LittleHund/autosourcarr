"""Pure Compose preparation shared by web setup and optional TrueNAS CLI."""
import copy
import posixpath
from pathlib import Path

def mount_parts(mount):
    if isinstance(mount, dict):
        source, target = mount.get('source'), mount.get('target')
        if not isinstance(source, str) or not isinstance(target, str):
            raise ValueError('Anonymous or malformed mount needs manual review.')
        return source, posixpath.normpath(target), bool(mount.get('read_only'))
    if isinstance(mount, str):
        parts = mount.split(':')
        if len(parts) in (2, 3):
            return parts[0], posixpath.normpath(parts[1]), len(parts) == 3 and 'ro' in parts[2].split(',')
    raise ValueError('Anonymous or malformed mount needs manual review.')


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
                raise ValueError('Duplicate environment keys need manual review.')
            output[name] = val if sep else None
        return output
    raise ValueError('Unsupported environment format.')


def transform(original, project, values, app_services=None):
    updated = copy.deepcopy(original)
    services = updated['services']
    selected = list(dict.fromkeys(app_services or ['autosourcarr']))
    if not selected or any(name not in services or name not in {'autosourcarr', 'private-autosourcarr'} for name in selected):
        raise ValueError('Select existing AutoSourcarr service names with --service')
    root, network = values['REPAIR_ROOT'], values['REPAIR_NETWORK']
    for service in services.values():
        for mount in service.get('volumes', []):
            host, target, _ = mount_parts(mount)
            if host and target not in {'/repairs', '/home/node/.codex', '/locks'}:
                path = Path(host)
                if path.is_absolute() and (path == Path(root) or path in Path(root).parents or Path(root) in path.parents):
                    raise ValueError('Repair dataset overlaps an existing application mount')
    existing = services.get('autosourcarr-repair')
    if existing and environment_mapping(existing.get('labels', {})).get('autosourcarr.managed-repair') != 'true':
        raise ValueError('A different service already uses the repair worker name')
    for name in selected:
        app = services[name]
        if 'network_mode' in app: raise ValueError('Repair setup requires ordinary Compose networks; existing network_mode needs review')
        env = environment_mapping(app.get('environment', {}))
        env.update(REPAIR_WORKER_URL='http://autosourcarr-repair:8766', REPAIR_WORKER_TOKEN=values['REPAIR_TOKEN'])
        app['environment'] = env
        # Preserve implicit default-network membership when adding the private link.
        networks = app.get('networks') or ['default']
        if isinstance(networks, list): app['networks'] = list(dict.fromkeys(networks+['repair']))
        else: app['networks'] = {**networks, 'repair': {}}
        app['build'] = {'context': str(project/'autosourcarr')}
        app['image'], app['pull_policy'] = 'autosourcarr:local', 'build'
    services['autosourcarr-repair'] = {
        'image': 'autosourcarr-repair:0.3.25', 'build': {'context': str(project/'repair-worker')},
        'restart': 'unless-stopped', 'stop_grace_period': '90s',
        'labels': {'autosourcarr.managed-repair': 'true'},
        'environment': {'REPAIR_ROOT': root, 'REPAIR_TOKEN': values['REPAIR_TOKEN'], 'REPAIR_JOB_NETWORK': network+'-jobs'},
        'volumes': ['/var/run/docker.sock:/var/run/docker.sock', root+':/repairs'],
        'networks': ['repair', 'repair-jobs'],
        'logging': {'driver': 'json-file', 'options': {'max-size': '10m', 'max-file': '3'}},
    }
    if 'autosourcarr-repair-egress' in services and environment_mapping(services['autosourcarr-repair-egress'].get('labels', {})).get('autosourcarr.managed-repair') != 'true':
        raise ValueError('Repair egress service name already exists')
    services['autosourcarr-repair-egress'] = {
        'labels': {'autosourcarr.managed-repair': 'true'},
        'image': 'autosourcarr-repair:0.3.25', 'command': ['python', 'egress.py'],
        'restart': 'unless-stopped', 'user': '1000:1000', 'read_only': True,
        'cap_drop': ['ALL'], 'security_opt': ['no-new-privileges:true'], 'mem_limit': '256m',
        'networks': ['repair-jobs', 'repair-egress'],
    }
    definitions = updated.setdefault('networks', {})
    for name, definition in {'repair': {'name': network}, 'repair-jobs': {'name': network+'-jobs', 'internal': True}, 'repair-egress': {}}.items():
        if name in definitions and definitions[name] != definition:
            raise ValueError('Existing network conflicts with '+name)
        definitions[name] = definition
    if any('default' in services[name]['networks'] for name in selected): definitions.setdefault('default', {})
    return updated

