import copy
from pathlib import Path
from types import SimpleNamespace

import pytest
import importlib.util
spec = importlib.util.spec_from_file_location('repair_truenas', Path(__file__).resolve().parents[2] / 'scripts' / 'repair-truenas.py')
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)


def fixture_config(tmp_path):
    return {
        'services': {
            'autosourcarr': {
                'image': 'python:3.13', 'entrypoint': ['bash'], 'command': ['/app/start-truenas.sh'],
                'ports': ['192.0.2.1:30060:8282'], 'networks': ['manga'], 'user': '568:568',
                'env_file': ['/my/real/config/autosourcarr.env'],
                'environment': ['ADMIN_TOKEN=do-not-print', 'SOURCE_ENGINE_MODE=external', 'PYTHONPATH=/deps'],
                'volumes': ['/real/data:/data', '/real/config:/config', '/other/pool:/database', '/code:/app:ro', '/cache:/deps'],
                'depends_on': {'autosourcarr-source-host': {'condition': 'service_started'}, 'translation': {'condition': 'service_healthy'}},
            },
            'autosourcarr-source-host': {'image': 'suwayomi', 'volumes': [f'{tmp_path}/source:{repair.ENGINE_DATA}']},
            'suwayomi': {'image': 'reader', 'environment': {'SECRET': 'keep'}, 'volumes': ['/reader:/data']},
            'translation': {'image': 'translate', 'networks': ['manga']},
        },
        'networks': {'manga': {'external': True}}, 'volumes': {'unused-but-retained': {}}, 'x-notes': {'key': 'retain'},
    }


def test_preserves_complete_stack_and_moves_same_source_mount(tmp_path):
    original = fixture_config(tmp_path)
    snapshot = copy.deepcopy(original)
    result = repair.transform(original, tmp_path / 'project')
    assert original == snapshot
    assert set(result['services']) == {'autosourcarr', 'suwayomi', 'translation'}
    for key in ('networks', 'volumes', 'x-notes'):
        assert result[key] == original[key]
    for name in ('suwayomi', 'translation'):
        assert result['services'][name] == original['services'][name]
    app = result['services']['autosourcarr']
    for key in ('ports', 'networks', 'user', 'env_file'):
        assert app[key] == original['services']['autosourcarr'][key]
    assert app['environment'] == {'ADMIN_TOKEN': 'do-not-print', 'SOURCE_ENGINE_MODE': 'integrated'}
    assert '/other/pool:/database' in app['volumes']
    assert app['volumes'][-1] == original['services'][repair.OLD_SOURCE]['volumes'][0]
    assert 'command' not in app and 'entrypoint' not in app
    assert app['depends_on'] == {'translation': {'condition': 'service_healthy'}}
    assert repair.transform(result, tmp_path / 'project') == result


@pytest.mark.parametrize('mutate', [
    lambda c: c['services']['suwayomi'].update(depends_on=[repair.OLD_SOURCE]),
    lambda c: c['services']['autosourcarr'].update(volumes=['/different:' + repair.ENGINE_DATA]),
    lambda c: c['services'][repair.OLD_SOURCE].update(volumes=[]),
    lambda c: c['services'][repair.OLD_SOURCE].update(volumes=['named:' + repair.ENGINE_DATA]),
    lambda c: c['services'][repair.OLD_SOURCE].update(volumes=['/real:' + repair.ENGINE_DATA + ':ro']),
    lambda c: c['services']['autosourcarr'].update(volumes=['/hide:/home']),
    lambda c: c['services']['autosourcarr'].update(links=[repair.OLD_SOURCE]),
])
def test_refuses_ambiguous_or_unsafe_transforms(tmp_path, mutate):
    original = fixture_config(tmp_path)
    mutate(original)
    with pytest.raises(repair.RepairError):
        repair.transform(original, tmp_path)


def test_long_mount_and_list_dependency_preserved(tmp_path):
    original = fixture_config(tmp_path)
    mount = {'type': 'bind', 'source': str(tmp_path / 'source'), 'target': repair.ENGINE_DATA,
             'bind': {'create_host_path': False}}
    original['services'][repair.OLD_SOURCE]['volumes'] = [mount]
    original['services']['autosourcarr']['depends_on'] = [repair.OLD_SOURCE, 'translation']
    result = repair.transform(original, tmp_path)
    assert result['services']['autosourcarr']['volumes'][-1] == mount
    assert result['services']['autosourcarr']['depends_on'] == ['translation']


def test_backup_never_overwrites_original_or_follows_symlink(tmp_path):
    path = tmp_path / 'recovery' / 'app.json'
    assert repair.secure_backup(path, {'original': 1})
    assert path.stat().st_mode & 0o777 == 0o600
    first = path.read_bytes()
    assert not repair.secure_backup(path, {'replacement': 2})
    assert path.read_bytes() == first
    linked = tmp_path / 'link.json'
    linked.symlink_to(path)
    with pytest.raises(repair.RepairError):
        repair.secure_backup(linked, {'bad': 1})
    assert path.read_bytes() == first


def test_pending_job_does_not_retry_or_read_result():
    calls = []
    client = SimpleNamespace(call=lambda *a, **kw: (calls.append((a, kw)) or SimpleNamespace(
        event=SimpleNamespace(wait=lambda seconds: False), job_id=74,
        result=lambda: pytest.fail('result called on pending job'))))
    with pytest.raises(repair.RepairError, match='74'):
        repair.wait_job(client, 'app.update', 'manga', {})
    assert len(calls) == 1


def test_concurrent_config_change_prevents_update(tmp_path, monkeypatch):
    original = fixture_config(tmp_path)
    (tmp_path / 'source').mkdir()
    (tmp_path / 'source' / 'database.db').write_text('retained')
    (tmp_path / 'autosourcarr').mkdir()
    (tmp_path / 'autosourcarr' / 'Dockerfile').write_text('FROM example')
    updated = repair.transform(original, tmp_path)
    commands = []
    monkeypatch.setattr(repair, 'run', lambda args, **kw: commands.append(args) or '')
    calls = []
    client = SimpleNamespace(call=lambda *args, **kw: calls.append(args) or {'concurrent': True})
    with pytest.raises(repair.RepairError, match='changed'):
        repair.apply(client, 'manga', original, updated, tmp_path, tmp_path / 'backup.json')
    assert [c[0] for c in calls] == ['app.config']
    assert any('build' in c for c in commands)


def test_no_container_changes_after_build_failure(tmp_path, monkeypatch):
    original = fixture_config(tmp_path)
    (tmp_path / 'source').mkdir()
    (tmp_path / 'source' / 'database.db').write_text('retained')
    (tmp_path / 'autosourcarr').mkdir()
    (tmp_path / 'autosourcarr' / 'Dockerfile').write_text('FROM example')
    def fail_build(args, **kwargs):
        if 'build' in args:
            raise repair.RepairError('build failed')
        return ''
    monkeypatch.setattr(repair, 'run', fail_build)
    client = SimpleNamespace(call=lambda *a, **kw: pytest.fail('middleware mutation after failed build'))
    with pytest.raises(repair.RepairError, match='build failed'):
        repair.apply(client, 'manga', original, repair.transform(original, tmp_path), tmp_path, tmp_path / 'backup.json')


def test_discovery_requires_one_truenas_project(monkeypatch):
    monkeypatch.setattr(repair, 'run', lambda args: 'ix-manga\nix-manga\n')
    assert repair.discover_app() == 'manga'
    monkeypatch.setattr(repair, 'run', lambda args: 'ix-manga\nix-other\n')
    with pytest.raises(repair.RepairError, match='exactly one'):
        repair.discover_app()


@pytest.mark.parametrize('state,expected_jobs', [
    ('STOPPED', ['app.update', 'app.start']),
    ('RUNNING', ['app.update']),
])
def test_apply_calls_expected_api_and_checks_runtime(tmp_path, monkeypatch, state, expected_jobs, capsys):
    original = fixture_config(tmp_path)
    (tmp_path / 'source').mkdir()
    (tmp_path / 'source' / 'database.db').write_text('retained')
    (tmp_path / 'autosourcarr').mkdir()
    (tmp_path / 'autosourcarr' / 'Dockerfile').write_text('FROM example')
    updated = repair.transform(original, tmp_path)
    commands, jobs = [], []
    def command(args, **kwargs):
        commands.append(args)
        if 'ps' in args and '--format' in args and args[-1] == '{{.ID}}':
            return 'abcd1234\n'
        return ''
    monkeypatch.setattr(repair, 'run', command)
    monkeypatch.setattr(repair, 'wait_job', lambda client, method, *args: jobs.append((method, args)))
    client = SimpleNamespace(call=lambda method, app: original if method == 'app.config' else {'state': state})
    repair.apply(client, 'manga', original, updated, tmp_path, tmp_path / 'backup.json')
    assert [j[0] for j in jobs] == expected_jobs
    assert jobs[0][1] == ('manga', {'custom_compose_config': updated})
    assert commands[-1] == ['docker', 'exec', 'abcd1234', 'test', '-f', repair.ENGINE_SCRIPT]
    assert 'readiness is not yet verified' in capsys.readouterr().out


def test_external_source_consumer_blocks_before_build(tmp_path, monkeypatch):
    original = fixture_config(tmp_path)
    (tmp_path / 'source').mkdir()
    (tmp_path / 'source' / 'database.db').write_text('retained')
    commands = []
    monkeypatch.setattr(repair, 'run', lambda args, **kw: commands.append(args) or 'abc ix-other sources\n')
    client = SimpleNamespace(call=lambda *a, **kw: pytest.fail('API mutated despite conflicting engine'))
    with pytest.raises(repair.RepairError, match='outside this migration'):
        repair.apply(client, 'manga', original, repair.transform(original, tmp_path), tmp_path, tmp_path / 'backup.json')
    assert len(commands) == 1 and 'ps' in commands[0]


def test_legacy_source_identity_survives_root_backend_migration(tmp_path):
    original = fixture_config(tmp_path)
    original['services']['autosourcarr'].pop('user')
    original['services'][repair.OLD_SOURCE]['user'] = '568:569'
    result = repair.transform(original, tmp_path)
    env = result['services']['autosourcarr']['environment']
    assert env['SOURCE_ENGINE_UID'] == '568' and env['SOURCE_ENGINE_GID'] == '569'
    assert 'user' not in result['services']['autosourcarr'], 'Backend remains root and child drops to the retained source identity'
    original['services']['autosourcarr']['environment'] = {'SOURCE_ENGINE_UID': '1000', 'SOURCE_ENGINE_GID': '1001'}
    result = repair.transform(original, tmp_path)
    assert result['services']['autosourcarr']['environment']['SOURCE_ENGINE_UID'] == '1000'
    assert result['services']['autosourcarr']['environment']['SOURCE_ENGINE_GID'] == '1001'


@pytest.mark.parametrize('user', ['suwayomi', '0:0', '${SOURCE_UID}:568'])
def test_ambiguous_source_identity_is_not_guessed(tmp_path, user):
    original = fixture_config(tmp_path)
    original['services'][repair.OLD_SOURCE]['user'] = user
    with pytest.raises(repair.RepairError, match='UID/GID'):
        repair.transform(original, tmp_path)


@pytest.mark.parametrize('target', ['/home/suwayomi/', '/home/suwayomi/startup_script.sh', '/home/suwayomi/Tachidesk-Server.jar', '/app/../home/suwayomi', '/usr/bin'])
def test_image_masking_mounts_are_rejected_before_deployment(tmp_path, target):
    original = fixture_config(tmp_path)
    original['services']['autosourcarr']['volumes'].append('/hide:' + target)
    with pytest.raises(repair.RepairError, match='hide'):
        repair.transform(original, tmp_path)


def test_explicit_retained_path_recovers_removed_source_service(tmp_path):
    original = fixture_config(tmp_path)
    original['services'].pop(repair.OLD_SOURCE)
    before = copy.deepcopy(original)
    source_data = '/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/source-host'
    result = repair.transform(original, tmp_path, source_data=source_data, source_user='568:568')
    app = result['services'][repair.SERVICE]
    mount = next(m for m in app['volumes'] if repair.mount_parts(m)[1] == repair.ENGINE_DATA)
    assert mount == {'type': 'bind', 'source': source_data, 'target': repair.ENGINE_DATA,
                     'bind': {'create_host_path': False}}
    assert app['environment']['SOURCE_ENGINE_UID'] == '568'
    assert app['environment']['SOURCE_ENGINE_GID'] == '568'
    assert '/real/data:/data' in app['volumes'] and '/real/config:/config' in app['volumes']
    assert result['services']['suwayomi'] == original['services']['suwayomi']
    assert original == before
    assert repair.transform(result, tmp_path, source_data=source_data + '/', source_user='568:568') == result


@pytest.mark.parametrize('path', ['relative/source', '/mnt/${SOURCE}', '/mnt/source:/container', '/'])
def test_explicit_source_path_must_be_host_directory(tmp_path, path):
    original = fixture_config(tmp_path)
    original['services'].pop(repair.OLD_SOURCE)
    with pytest.raises(repair.RepairError, match='source-data'):
        repair.transform(original, tmp_path, source_data=path)


def test_explicit_path_cannot_replace_existing_dataset(tmp_path):
    original = fixture_config(tmp_path)
    with pytest.raises(repair.RepairError, match='conflicts'):
        repair.transform(original, tmp_path, source_data='/mnt/different')


@pytest.mark.parametrize('exists', [False, True])
def test_recovery_refuses_missing_or_empty_directory_before_any_host_change(tmp_path, monkeypatch, exists):
    original = fixture_config(tmp_path)
    original['services'].pop(repair.OLD_SOURCE)
    source = tmp_path / 'retained'
    if exists:
        source.mkdir()
    updated = repair.transform(original, tmp_path, source_data=str(source))
    monkeypatch.setattr(repair, 'run', lambda *a, **kw: pytest.fail('Docker called before data validation'))
    client = SimpleNamespace(call=lambda *a, **kw: pytest.fail('API called before data validation'))
    with pytest.raises(repair.RepairError, match='missing or empty'):
        repair.apply(client, 'manga', original, updated, tmp_path, tmp_path / 'backup.json')
    assert source.exists() == exists


def test_cli_passes_explicit_recovery_mount_and_user(tmp_path, monkeypatch, capsys):
    import sys
    original = fixture_config(tmp_path)
    original['services'].pop(repair.OLD_SOURCE)
    class Client:
        def __enter__(self):
            return self
        def __exit__(self, *args):
            pass
        def call(self, method, app):
            assert app == 'manga'
            if method == 'app.get_instance':
                return {'custom_app': True, 'state': 'RUNNING'}
            assert method == 'app.config'
            return original
    monkeypatch.setitem(sys.modules, 'truenas_api_client', SimpleNamespace(Client=Client))
    captured = []
    monkeypatch.setattr(repair, 'apply', lambda *args: captured.append(args))
    args = ['--app', 'manga', '--source-data', str(tmp_path / 'source'), '--source-user', '568:568']
    assert repair.main(args) == 0 and not captured, 'Preview remains read-only'
    assert repair.main(args + ['--apply']) == 0
    updated = captured[0][3]
    assert updated['services'][repair.SERVICE]['environment']['SOURCE_ENGINE_UID'] == '568'
    assert any(repair.mount_parts(m)[0] == str(tmp_path / 'source') for m in updated['services'][repair.SERVICE]['volumes'])
    assert 'do-not-print' not in capsys.readouterr().out
