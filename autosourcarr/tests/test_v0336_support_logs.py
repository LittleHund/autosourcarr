"""Real retained logs and authenticated export must never contain fixture secrets."""
import asyncio
import json
import os
import sys
import threading
import time
from types import SimpleNamespace

import httpx
import pytest

_PRIVATE_BEGIN = b'-----BEGIN ' + b'PRIVATE KEY-----\n'
_PRIVATE_END = b'-----END ' + b'PRIVATE KEY-----\n'


def test_support_privacy_masks_known_values_and_personal_endpoints():
    from app.support_logs import SupportPrivacy
    privacy = SupportPrivacy()
    privacy.register({'READER_API_TOKEN': 'unlabelled-private-value'})
    raw = {'username': 'reader-person', 'email': 'person@example.test',
           'accessKey': 'private-access', 'context': 'compile failed at Dto.kt:44',
           'detail': 'unlabelled-private-value https://user:pass@nas.private:8282/api/v1?q=private-query '
                     '10.1.2.3 C:\\Users\\reader-person\\project\\Dto.kt person@example.test',
           'header': 'Bearer opaque-value', 'credential': 'another-private-value'}
    text = json.dumps(privacy.scrub(raw))
    for secret in ('unlabelled-private-value', 'reader-person', 'private-access', 'nas.private',
                   'user:pass', 'private-query', '10.1.2.3', 'person@example.test',
                   'opaque-value', 'another-private-value'):
        assert secret not in text
    assert 'Dto.kt:44' in text and 'compile failed' in text and '/api/v1' in text


def test_signing_arguments_and_unstructured_auth_material_are_masked():
    from app.support_logs import SupportPrivacy
    raw='keytool -storepass native-password -keypass "key pass value"; apksigner --ks-pass pass:signing-password --token=private-token; Bearer private-bearer'
    safe=SupportPrivacy().text(raw)
    for hidden in ('native-password','key pass value','signing-password','private-token','private-bearer'):
        assert hidden not in safe
    assert 'keytool' in safe and 'apksigner' in safe


@pytest.mark.parametrize('field',['sessionId','csrfToken','device_code','user_code','apiKey','access_token','proxyAuthorization','passphrase','USER_NAME'])
def test_sensitive_structured_fields_are_obfuscated(field):
    from app.support_logs import SupportPrivacy
    assert 'private-field-value' not in json.dumps(SupportPrivacy().scrub({field:'private-field-value'}))
    assert 'private-field-value' not in SupportPrivacy().text(field+'=private-field-value')
    assert 'private-field-value' not in SupportPrivacy().text('context:{"'+field+'":"private-field-value"}')


def test_oversize_configured_credential_fails_closed_without_unbounded_registration():
    from app.support_logs import SupportPrivacy
    privacy=SupportPrivacy()
    privacy.register({'api_key':'x'*20000})
    assert privacy.text('any later diagnostic')=='[diagnostic omitted: secret registry capacity]'


def test_long_dotted_process_lines_do_not_stall_sensitive_data_redaction():
    from app.support_logs import SupportPrivacy
    privacy=SupportPrivacy()
    raw='package.'*8192
    started=time.monotonic()
    assert privacy.text(raw)==raw
    assert time.monotonic()-started < 1.0


def test_account_failures_retain_only_whitelisted_observed_diagnostics():
    from app.support_logs import SupportLogStore
    store=SupportLogStore()
    result={'state':'failed','action':'login','busy':False,'user_code':'ABCD-EFGH',
            'message':'private account message','raw_output':'private-output',
            'diagnostics':{'reason':'request-denied','phase':'requesting-code','http_status':403,
                           'exit_code':1,'code_received':False,'access_token':'private-access'}}
    store.observe_control(result)
    store.observe_control(result)
    snapshot=store.snapshot()
    assert len(snapshot['entries'])==1
    assert snapshot['repair_account']['diagnostics']['http_status']==403
    text=json.dumps(snapshot)
    for hidden in ('ABCD-EFGH','private account message','private-output','private-access'):
        assert hidden not in text
    assert 'request-denied' in text
    store.observe_control({'state':{},'action':[],'diagnostics':{'reason':{},'phase':[],'http_status':'private-code'}})
    assert store.snapshot()['repair_account']['state']=='unknown'
    assert 'private-code' not in json.dumps(store.snapshot())


def test_account_launcher_facts_survive_support_export_without_raw_errors():
    from app.support_logs import SupportLogStore
    store=SupportLogStore()
    state={'state':'failed','action':'login','busy':False,
           'diagnostics':{'reason':'resource-limit','phase':'starting','exit_code':70,
                          'code_received':False,'launcher_stage':'exec','os_error':11,
                          'raw_error':'private-credential /private/path'}}
    store.observe_control(state)
    data=store.snapshot()['repair_account']['diagnostics']
    assert data['launcher_stage']=='exec' and data['os_error']==11
    assert 'private' not in json.dumps(store.snapshot())
    state['diagnostics'].update(launcher_stage=[],os_error=True)
    store.observe_control(state)
    data=store.snapshot()['repair_account']['diagnostics']
    assert 'launcher_stage' not in data and 'os_error' not in data


def test_diagnostics_report_only_a_validated_image_codex_version(tmp_path,monkeypatch):
    from app import diagnostics
    marker=tmp_path/'codex-version'
    monkeypatch.setattr(diagnostics,'_CODEX_VERSION_FILE',marker)
    try:
        for content,expected in [('codex-cli 0.157.0\n','codex-cli 0.157.0'),
                                 ('private-token /private/path','unavailable'),
                                 ('codex-cli '+('1'*130),'unavailable')]:
            marker.write_text(content)
            diagnostics.software_versions.cache_clear()
            assert diagnostics.software_versions()['bundled_codex']==expected
    finally:
        diagnostics.software_versions.cache_clear()


def test_unchanged_helper_health_polling_does_not_fill_the_log(monkeypatch):
    from app import internal_services as module
    from app.support_logs import SupportLogStore
    store=SupportLogStore()
    monkeypatch.setattr(module,'support_logs',store)
    manager=module.InternalServices()
    for _ in range(100):
        manager.state('repair','ready','Running inside this instance')
    manager.state('repair','failed','Process exited')
    assert len(store.snapshot()['entries'])==2


def test_real_log_files_rotate_and_survive_a_new_store(tmp_path):
    from app.support_logs import SupportLogStore
    store = SupportLogStore(max_file_bytes=16384, file_count=3, pending_limit=40)
    store.configure(tmp_path)
    store.privacy.register({'password': 'keep-me-private'})
    for i in range(120):
        store.record('builder', f'compile-{i} keep-me-private '+('x'*900))
        store.flush()
    files = list((tmp_path/'diagnostics').iterdir())
    assert len(files) <= 3
    assert sum(p.stat().st_size for p in files) <= 3*16384
    assert 'keep-me-private' not in ''.join(p.read_text() for p in files)
    other = SupportLogStore(max_file_bytes=16384, file_count=3)
    other.configure(tmp_path)
    snapshot = other.snapshot()
    assert 'compile-119' in json.dumps(snapshot)
    assert snapshot['retention']['bounded'] is True


@pytest.mark.asyncio
async def test_process_capture_redacts_whole_lines_across_read_boundaries_and_omits_oversize(tmp_path):
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.configure(tmp_path)
    store.privacy.register({'token':'chunk-split-secret'})
    stream = asyncio.StreamReader()
    for chunk in (b'compiler: unres', b'olved reference\nAuthorization: Bear', b'er chunk-split-secret\n',
                  _PRIVATE_BEGIN+b'unlabelled-key-material\n'+_PRIVATE_END,
                  b'x'*70000, b'password=oversize-secret\nlast useful error\n'):
        stream.feed_data(chunk)
    stream.feed_eof()
    await store.capture(stream, 'builder')
    store.flush()
    text = json.dumps(store.snapshot())
    for value in ('chunk-split-secret','unlabelled-key-material','oversize-secret'):
        assert value not in text
    assert 'unresolved reference' in text and 'last useful error' in text
    assert 'omitted' in text


@pytest.mark.asyncio
async def test_oversize_key_header_cannot_expose_following_key_lines():
    from app.support_logs import SupportLogStore
    store=SupportLogStore()
    stream=asyncio.StreamReader()
    stream.feed_data(b'x'*70000+_PRIVATE_BEGIN+b'private-key-after-long-line\n'+_PRIVATE_END+b'useful final failure\n')
    stream.feed_eof()
    await store.capture(stream,'builder')
    text=json.dumps(store.snapshot())
    assert 'private-key-after-long-line' not in text
    assert 'useful final failure' in text


def test_pending_log_storage_is_bounded_and_disk_failure_is_reported(tmp_path, monkeypatch):
    from app.support_logs import SupportLogStore
    store = SupportLogStore(pending_limit=8)
    store.configure(tmp_path)
    for i in range(100):
        store.record('builder', 'line-'+str(i))
    assert store.snapshot()['retention']['dropped_records'] >= 92
    def denied(*args, **kwargs):
        raise PermissionError('private-user/path')
    monkeypatch.setattr(store, '_write', denied)
    store.record('builder', 'still useful')
    store.flush()
    snapshot = store.snapshot()
    assert snapshot['storage_error'] == 'PermissionError'
    assert 'private-user' not in json.dumps(snapshot)


@pytest.mark.asyncio
async def test_helper_drain_retains_redacted_failure_and_original_exit_code(tmp_path, monkeypatch):
    from app import internal_services as module
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.configure(tmp_path)
    monkeypatch.setattr(module, 'support_logs', store)
    stream = asyncio.StreamReader()
    stream.feed_data(b'Dto.kt:14: unresolved reference\npassword=compiler-secret\n')
    stream.feed_eof()
    async def wait():
        return 17
    process = SimpleNamespace(stdout=stream, wait=wait)
    manager = module.InternalServices()
    manager.processes['builder'] = process
    assert await manager._drain(process) == 17
    text = json.dumps(store.snapshot())
    assert 'unresolved reference' in text and 'builder' in text
    assert 'compiler-secret' not in text


@pytest.mark.asyncio
async def test_diagnostic_export_auth_and_persisted_logs_are_local(tmp_path, monkeypatch):
    from app import main
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.configure(tmp_path)
    store.record('source-engine', 'java.net.ConnectException: refused at https://private-nas.local/api/graphql')
    monkeypatch.setattr(main, 'support_logs', store)
    monkeypatch.setattr(main.settings, 'admin_token', 'private-admin-token')
    monkeypatch.setattr(main.service, 'import_diagnostic_context', lambda _: {'items': []})
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        assert (await client.get('/api/admin/diagnostics')).status_code == 401
        response = await client.get('/api/admin/diagnostics',headers={'X-Admin-Token':'private-admin-token'})
    assert response.status_code == 200
    assert response.headers['cache-control'] == 'no-store'
    assert 'ConnectException' in response.text
    assert 'private-nas.local' not in response.text and 'private-admin-token' not in response.text
    assert response.json()['logs']['retention']['bounded'] is True


@pytest.mark.asyncio
async def test_real_child_stdout_and_stderr_are_collected_and_scrubbed(tmp_path):
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.configure(tmp_path)
    store.privacy.register({'token':'native-child-secret'})
    process = await asyncio.create_subprocess_exec(sys.executable,'-c',
        "import sys;print('compiler error at Dto.kt:8');print('native-child-secret',file=sys.stderr);sys.exit(17)",
        stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.STDOUT)
    await store.capture(process.stdout,'builder')
    assert await process.wait() == 17
    text = json.dumps(store.snapshot())
    assert 'Dto.kt:8' in text and 'native-child-secret' not in text


def test_log_hardlink_is_refused_and_other_file_is_untouched(tmp_path):
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.configure(tmp_path)
    directory = tmp_path/'diagnostics'
    directory.mkdir()
    target = tmp_path/'retained.txt'
    target.write_text('manual retained data')
    os.link(target,directory/'support.jsonl')
    store.record('app','new event')
    store.flush()
    assert target.read_text() == 'manual retained data'
    assert store.snapshot()['storage_error'] == 'OSError'


def test_export_refuses_a_log_path_replaced_between_validation_and_open(tmp_path,monkeypatch):
    from app import support_logs as module
    store=module.SupportLogStore()
    store.configure(tmp_path)
    store.record('app','retained useful event')
    store.flush()
    path=tmp_path/'diagnostics/support.jsonl'
    original_open=os.open
    replaced=False
    def swap_before_open(value,flags,*args,**kwargs):
        nonlocal replaced
        if str(value)==str(path) and not flags & os.O_WRONLY and not replaced:
            replaced=True
            os.replace(path,path.with_name('original.jsonl'))
            path.write_text(json.dumps({'id':'foreign','message':'outside-private-value'}))
        return original_open(value,flags,*args,**kwargs)
    monkeypatch.setattr(module.os,'open',swap_before_open)
    snapshot=store.snapshot()
    assert replaced and snapshot['storage_error']=='OSError'
    assert 'outside-private-value' not in json.dumps(snapshot)
    assert 'retained useful event' in json.dumps(snapshot)


def test_non_ascii_and_malformed_unicode_cannot_break_log_writer(tmp_path):
    from app.support_logs import SupportLogStore
    store = SupportLogStore(max_file_bytes=8192)
    store.configure(tmp_path)
    store.record('app','\ud800 invalid runtime text')
    store.record('app','\U0001f600'*4000)
    store.flush()
    assert not store.snapshot()['storage_error']
    assert all(p.stat().st_size <= 8192 for p in (tmp_path/'diagnostics').iterdir())


@pytest.mark.asyncio
async def test_shutdown_drains_a_log_write_already_running_in_a_thread(tmp_path,monkeypatch):
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    entered,release = threading.Event(),threading.Event()
    original = store._write
    def gated(lines):
        entered.set()
        assert release.wait(5)
        original(lines)
    monkeypatch.setattr(store,'_write',gated)
    store.start(tmp_path,{})
    try:
        assert await asyncio.to_thread(entered.wait,3)
        stopped = asyncio.create_task(store.stop())
        await asyncio.sleep(.02)
        assert not stopped.done()
        release.set()
        await stopped
        assert 'AutoSourcarr startup' in json.dumps(store.snapshot())
    finally:
        release.set()
        await store.stop()


@pytest.mark.asyncio
async def test_early_startup_failure_is_retained_for_the_next_successful_start(tmp_path,monkeypatch):
    from app import main
    from app.support_logs import SupportLogStore
    from test_v0335_lifespan import test_pre_yield_startup_failure_runs_existing_teardown
    store=SupportLogStore()
    monkeypatch.setattr(main,'support_logs',store)
    monkeypatch.setattr(main.settings,'data_dir',tmp_path)
    await test_pre_yield_startup_failure_runs_existing_teardown(monkeypatch,'configure')
    text=json.dumps(store.snapshot())
    assert 'startup fixture' in text and 'Application lifecycle failed' in text
    assert store._task is None and store._handler is None


@pytest.mark.asyncio
async def test_failed_resource_cleanup_cannot_abandon_the_log_writer(tmp_path,monkeypatch):
    from app import main,internal_services
    from app.support_logs import SupportLogStore
    from unittest.mock import Mock,AsyncMock
    store=SupportLogStore()
    monkeypatch.setattr(main,'support_logs',store)
    monkeypatch.setattr(main.settings,'data_dir',tmp_path)
    monkeypatch.setattr(main,'service',SimpleNamespace(cancel_background_tasks=AsyncMock()))
    monkeypatch.setattr(internal_services.internal_services,'configure',Mock(side_effect=RuntimeError('startup fixture')))
    monkeypatch.setattr(main,'source_maintenance',SimpleNamespace(stop=AsyncMock(side_effect=RuntimeError('cleanup fixture'))))
    # Cleanup now attempts every owner and preserves the original startup error.
    # Its separate failure must still be logged and the log writer drained.
    with pytest.raises(RuntimeError,match='startup fixture'):
        async with main.lifespan(main.app):
            pytest.fail('Startup must fail')
    assert store._task is None and store._handler is None
    text=json.dumps(store.snapshot())
    assert 'startup fixture' in text and 'cleanup fixture' in text
