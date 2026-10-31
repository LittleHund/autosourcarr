"""Native source preference and APK install contracts, without a live JVM."""
import asyncio
import copy
import io
import json
from pathlib import Path
import zipfile

from graphql import build_schema, graphql_sync
import httpx
import pytest

from app import source_controls as controls
from app.config import settings
from app.suwayomi import SuwayomiClient, SuwayomiError

SID = '6084907896154116083'
PKG = 'eu.kanade.tachiyomi.extension.en.example'


def preferences():
    common = {'title': 'Setting', 'summary': 'Source description', 'visible': True, 'enabled': True}
    return [
        {**common, '__typename': 'SwitchPreference', 'key': 'switch', 'currentValue': False, 'default': False},
        {**common, '__typename': 'CheckBoxPreference', 'key': 'check', 'currentValue': True, 'default': True},
        {**common, '__typename': 'EditTextPreference', 'key': 'password', 'currentValue': 'private-value', 'default': ''},
        {**common, '__typename': 'ListPreference', 'key': 'language', 'currentValue': 'en', 'default': 'en', 'entries': ['English', 'Dutch'], 'entryValues': ['en', 'nl']},
        {**common, '__typename': 'MultiSelectListPreference', 'key': 'categories', 'currentValue': ['a'], 'default': [], 'entries': ['First', 'Second'], 'entryValues': ['a', 'b']},
    ]


async def pref_client(prefs=None):
    schema = build_schema((Path(__file__).parent/'fixtures/suwayomi-source-settings-v2.3.2243.graphql').read_text())
    prefs = prefs if prefs is not None else preferences()
    mutations = []
    def source(_, info, id):
        assert type(id) is str and id == SID
        return {'id': id, 'name': 'Example', 'isConfigurable': True, 'preferences': prefs}
    def mutate(_, info, input):
        assert type(input['source']) is str and input['source'] == SID
        change = input['change']; mutations.append(copy.deepcopy(change))
        pref = prefs[change['position']]
        field = controls.TYPE_FIELDS[pref['__typename']][1]
        pref['currentValue'] = change[field]
        return {'source': source(None, None, SID), 'preferences': prefs}
    schema.type_map['Query'].fields['source'].resolve = source
    schema.type_map['Mutation'].fields['updateSourcePreference'].resolve = mutate
    def transport(request):
        operation = json.loads(request.content)
        result = graphql_sync(schema, operation['query'], variable_values=operation['variables'])
        assert not result.errors, result.errors
        return httpx.Response(200, json=result.formatted)
    client = SuwayomiClient('http://source.invalid')
    await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    return client, prefs, mutations


@pytest.mark.asyncio
async def test_all_five_native_preference_types_use_pinned_graphql_contract():
    client, prefs, mutations = await pref_client()
    try:
        snapshot = await controls.preferences_snapshot(client, SID)
        assert [p['type'] for p in snapshot['preferences']] == ['switch', 'checkbox', 'text', 'list', 'multiselect']
        assert snapshot['preferences'][2]['secret'] is True
        for index, value in enumerate([True, False, 'new-private-value', 'nl', ['a', 'b']]):
            pref = snapshot['preferences'][index]
            snapshot = await controls.update_preference(client, SID, {**pref, 'value': value, 'revision': snapshot['revision']})
            assert snapshot['preferences'][index]['value'] == value
        assert mutations == [{'position': 0, 'switchState': True}, {'position': 1, 'checkBoxState': False},
            {'position': 2, 'editTextState': 'new-private-value'}, {'position': 3, 'listState': 'nl'},
            {'position': 4, 'multiSelectState': ['a', 'b']}]
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_stale_screen_or_identity_is_rejected_before_mutation():
    client, prefs, mutations = await pref_client()
    try:
        snapshot = await controls.preferences_snapshot(client, SID)
        change = {**snapshot['preferences'][0], 'value': True, 'revision': snapshot['revision']}
        prefs.reverse()
        with pytest.raises(controls.SourcePreferencesConflict):
            await controls.update_preference(client, SID, change)
        snapshot = await controls.preferences_snapshot(client, SID)
        with pytest.raises(controls.SourcePreferencesConflict):
            await controls.update_preference(client, SID, {**change, 'revision': snapshot['revision']})
        assert not mutations
    finally:
        await client.close()


@pytest.mark.parametrize('index,value', [(0, 'true'), (1, 1), (2, None), (3, 'invalid'), (4, ['invalid'])])
@pytest.mark.asyncio
async def test_invalid_values_cannot_be_sent_to_native_engine(index, value):
    client, prefs, mutations = await pref_client()
    try:
        snapshot = await controls.preferences_snapshot(client, SID)
        with pytest.raises(ValueError):
            await controls.update_preference(client, SID, {**snapshot['preferences'][index], 'value': value, 'revision': snapshot['revision']})
        assert not mutations
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_hidden_disabled_and_unkeyed_settings_are_readonly():
    raw = preferences(); raw[0]['enabled'] = False; raw[1]['visible'] = False; raw[2]['key'] = None
    client, prefs, mutations = await pref_client(raw)
    try:
        snapshot = await controls.preferences_snapshot(client, SID)
        for pref in snapshot['preferences'][:3]:
            with pytest.raises(ValueError):
                await controls.update_preference(client, SID, {**pref, 'revision': snapshot['revision']})
        assert not mutations
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_multiselect_native_set_order_does_not_invalidate_revision():
    raw = preferences(); raw[-1]['currentValue'] = ['b', 'a']
    client, prefs, _ = await pref_client(raw)
    try:
        snapshot = await controls.preferences_snapshot(client, SID)
        prefs[-1]['currentValue'] = ['a', 'b']
        latest = await controls.preferences_snapshot(client, SID)
        assert snapshot['revision'] == latest['revision']
    finally:
        await client.close()


def apk(pkg=PKG, extra=None):
    target = io.BytesIO()
    with zipfile.ZipFile(target, 'w') as archive:
        archive.writestr('AndroidManifest.xml', f'<manifest package="{pkg}"/>')
        archive.writestr('classes.dex', b'dex\n035\0' + b'fixture'*40)
        for name, value in (extra or {}).items(): archive.writestr(name, value)
    return target.getvalue()


@pytest.mark.parametrize('filename,data', [('../source.apk', apk()), ('source.jar', apk()), ('bad.apk', b'junk'),
    ('source.apk', apk(extra={'../outside': 'bad'})), ('source.apk', apk('bad-package')),
    ('source.apk', apk('eu.kanade.tachiyomi.extension.all.autosourcarr'))])
def test_invalid_uploads_fail_before_engine_request(filename, data):
    with pytest.raises(ValueError): controls.inspect_apk(data, filename)


@pytest.mark.asyncio
async def test_upload_uses_native_multipart_and_confirms_installed_inventory(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    requests = []
    def transport(request):
        requests.append(request)
        if request.url.path == '/api/graphql':
            rows = [] if len(requests) == 1 else [{'pkgName': PKG, 'isInstalled': True, 'source': {'nodes': [{'id': SID}]}}]
            return httpx.Response(200, json={'data': {'extensions': {'nodes': rows}}})
        assert request.url.path == '/api/v1/extension/install'
        assert 'multipart/form-data; boundary=' in request.headers['content-type']
        assert ('filename="'+PKG+'.apk"').encode() in request.content
        assert b'name="file"' in request.content and b'application/vnd.android.package-archive' in request.content
        assert b'friendly name.apk' not in request.content
        return httpx.Response(200, text=PKG)
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        result = await controls.install_uploaded_apk(client, apk(), 'friendly name.apk')
        assert result['extension']['isInstalled'] is True and not result['backup_saved']
        assert len(requests) == 3
        receipt = json.loads(next(tmp_path.rglob('receipt.json')).read_text())
        assert receipt['state'] == 'installed'
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_failed_replace_retains_and_restores_old_package(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    previous = {'pkgName': PKG, 'apkName': 'old-source.apk', 'isInstalled': True, 'source': {'nodes': [{'id': SID}]}}
    backup_calls = []
    monkeypatch.setattr(controls, 'save_backup', lambda p: backup_calls.append(p) or b'old-jar')
    requests = []
    def transport(request):
        requests.append(request)
        if request.url.path == '/api/graphql':
            return httpx.Response(200, json={'data': {'extensions': {'nodes': [previous]}}})
        if len(requests) == 2:
            return httpx.Response(500, text='converter failure sensitive upstream text')
        assert b'filename="old-source.jar"' in request.content and b'old-jar' in request.content
        return httpx.Response(200, text=PKG)
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        with pytest.raises(SuwayomiError, match='previous version was restored') as exc:
            await controls.install_uploaded_apk(client, apk(), 'source.apk')
        assert 'sensitive' not in str(exc.value) and len(requests) == 4 and len(backup_calls) == 1
        assert json.loads(next(tmp_path.rglob('receipt.json')).read_text())['state'] == 'restored'
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_uncertain_install_does_not_retry_or_restore_during_native_work(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    requests = []
    def transport(request):
        requests.append(request)
        if request.url.path == '/api/graphql': return httpx.Response(200, json={'data': {'extensions': {'nodes': []}}})
        raise httpx.ReadTimeout('sensitive', request=request)
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        with pytest.raises(SuwayomiError, match='no automatic retry'):
            await controls.install_uploaded_apk(client, apk(), 'source.apk')
        assert len(requests) == 2
        assert json.loads(next(tmp_path.rglob('receipt.json')).read_text())['state'] == 'uncertain'
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_browser_cancellation_drains_install_before_lock_is_released(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    entered, finish = asyncio.Event(), asyncio.Event()
    count = 0
    async def transport(request):
        nonlocal count
        count += 1
        if request.url.path == '/api/graphql':
            rows = [] if count == 1 else [{'pkgName': PKG, 'isInstalled': True}]
            return httpx.Response(200, json={'data': {'extensions': {'nodes': rows}}})
        entered.set(); await finish.wait()
        return httpx.Response(200, text=PKG)
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    lock = asyncio.Lock()
    async def run():
        async with lock: await controls.install_uploaded_apk(client, apk(), 'source.apk')
    task = asyncio.create_task(run()); await entered.wait(); task.cancel(); await asyncio.sleep(0)
    assert lock.locked() and not task.done()
    task.cancel(); await asyncio.sleep(0); assert lock.locked()
    finish.set()
    with pytest.raises(asyncio.CancelledError): await task
    assert not lock.locked() and count == 3
    assert json.loads(next(tmp_path.rglob('receipt.json')).read_text())['state'] == 'installed'
    await client.close()


@pytest.mark.asyncio
async def test_upstream_preference_errors_do_not_expose_credentials():
    class Failed:
        async def graphql(self, *args): raise SuwayomiError('password=private-value')
    with pytest.raises(SuwayomiError) as exc:
        await controls.preferences_snapshot(Failed(), SID)
    assert 'private-value' not in str(exc.value)


@pytest.mark.asyncio
async def test_successful_upload_with_broken_inventory_does_not_restore(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(controls, 'save_backup', lambda p: b'old-jar')
    previous = {'pkgName': PKG, 'apkName': 'old.apk', 'isInstalled': True}
    requests = []
    def transport(request):
        requests.append(request)
        if len(requests) == 1:
            return httpx.Response(200, json={'data': {'extensions': {'nodes': [previous]}}})
        if request.url.path.endswith('/install'): return httpx.Response(200, text=PKG)
        return httpx.Response(200, json={'errors': [{'message': 'inventory secret-token'}]})
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        with pytest.raises(controls.SourceControlFailed, match='no rollback') as exc:
            await controls.install_uploaded_apk(client, apk(), 'source.apk')
        assert len(requests) == 3 and 'secret-token' not in str(exc.value)
        assert json.loads(next(tmp_path.rglob('receipt.json')).read_text())['state'] == 'uncertain'
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_source_setting_cancel_waits_for_native_save():
    entered, finish = asyncio.Event(), asyncio.Event()
    original, prefs, changes = await pref_client()
    snapshot = await controls.preferences_snapshot(original, SID)
    class Delayed:
        async def graphql(self, query, variables):
            if 'mutation ' in query:
                entered.set(); await finish.wait()
            return await original.graphql(query, variables)
    task = asyncio.create_task(controls.update_preference(Delayed(), SID, {
        **snapshot['preferences'][0], 'value': True, 'revision': snapshot['revision']}))
    await entered.wait(); task.cancel(); await asyncio.sleep(0)
    assert not task.done() and not changes
    finish.set()
    with pytest.raises(asyncio.CancelledError): await task
    assert changes == [{'position': 0, 'switchState': True}]
    await original.close()


def test_archive_limits_and_malformed_xml_are_rejected(monkeypatch):
    monkeypatch.setattr(controls, 'MAX_PACKAGE', 120)
    with pytest.raises(ValueError, match='32 MiB'): controls.inspect_apk(apk(), 'source.apk')
    monkeypatch.setattr(controls, 'MAX_PACKAGE', 32*1024*1024)
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w') as z:
        z.writestr('AndroidManifest.xml', '<manifest invalid'); z.writestr('classes.dex', b'fixture'*20)
    with pytest.raises(ValueError, match='readable'): controls.inspect_apk(out.getvalue(), 'source.apk')


@pytest.mark.parametrize('status', [408, 429, 502, 503, 504])
@pytest.mark.asyncio
async def test_gateway_failure_does_not_restore_while_engine_may_still_install(status, tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(controls, 'save_backup', lambda p: b'old-jar')
    previous = {'pkgName': PKG, 'apkName': 'old.apk', 'isInstalled': True}
    requests = []
    def transport(request):
        requests.append(request)
        if len(requests) == 1:
            return httpx.Response(200, json={'data': {'extensions': {'nodes': [previous]}}})
        return httpx.Response(status, text='gateway failure')
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        with pytest.raises(controls.SourceControlFailed, match='no rollback'):
            await controls.install_uploaded_apk(client, apk(), 'source.apk')
        assert len(requests) == 2
        assert json.loads(next(tmp_path.rglob('receipt.json')).read_text())['state'] == 'uncertain'
    finally:
        await client.close()
