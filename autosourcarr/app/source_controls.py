"""Source preference and local APK controls for the pinned Suwayomi engine.

The engine owns Android preference semantics, APK conversion and source reloads.
These adapters validate user input and retain a previous package before replacement.
Call mutation helpers while holding the application's source maintenance lock.
"""
from __future__ import annotations

import asyncio
import hashlib
import io
import json
import re
import stat
import struct
import time
import xml.etree.ElementTree as ET
import zipfile

import httpx

from .async_utils import run_blocking
from .config import settings
from .source_maintenance import INVENTORY, MAX_PACKAGE, atomic, identities, save_backup, storage
from .source_orphans import manifest_package, source_orphans
from .suwayomi import SuwayomiError, SourceWorkDeferred


# v2.3.2243 SourceType.kt and SourceMutation.kt. Distinct aliases are required
# because GraphQL union fields named currentValue/default have different types.
PREFERENCE_FIELDS = """
__typename
... on SwitchPreference { key title summary visible enabled switch_value: currentValue switch_default: default }
... on CheckBoxPreference { key title summary visible enabled checkbox_value: currentValue checkbox_default: default }
... on EditTextPreference { key title summary visible enabled text_value: currentValue text_default: default dialogTitle dialogMessage }
... on ListPreference { key title summary visible enabled list_value: currentValue list_default: default entries entryValues }
... on MultiSelectListPreference { key title summary visible enabled multiselect_value: currentValue multiselect_default: default entries entryValues dialogTitle dialogMessage }
"""
TYPE_FIELDS = {
    'SwitchPreference': ('switch', 'switchState'),
    'CheckBoxPreference': ('checkbox', 'checkBoxState'),
    'EditTextPreference': ('text', 'editTextState'),
    'ListPreference': ('list', 'listState'),
    'MultiSelectListPreference': ('multiselect', 'multiSelectState'),
}


class SourcePreferencesConflict(ValueError):
    """The source's current screen is no longer the one displayed by the browser."""


class SourceControlFailed(SuwayomiError):
    """A deliberately safe, actionable message (never arbitrary extension text)."""


def source_id(value):
    value = str(value)
    if not re.fullmatch(r'-?[0-9]{1,19}', value) or not -(1 << 63) <= int(value) < (1 << 63):
        raise ValueError('Invalid source ID')
    return str(int(value))


def normalize_preferences(source, raw):
    rows = []
    for index, pref in enumerate(raw):
        kind = TYPE_FIELDS.get(pref.get('__typename'), ('unsupported', None))[0]
        default = pref.get(kind + '_default')
        value = pref.get(kind + '_value')
        if value is None:
            value = default
        if value is None:
            value = [] if kind == 'multiselect' else False if kind in {'switch', 'checkbox'} else ''
        if kind == 'multiselect':
            # Android persists a Set<String>; iteration order is not a change.
            value = sorted(set(value))
            default = sorted(set(default)) if default is not None else None
        key = pref.get('key')
        title = pref.get('title') or key or 'Source setting'
        row = {'index': index, 'key': key, 'type': kind, 'title': title,
               'summary': pref.get('summary') or '', 'visible': pref.get('visible', True),
               'enabled': bool(pref.get('enabled', True) and key and kind != 'unsupported'),
               'value': value, 'default': default, 'entries': pref.get('entries') or [],
               'entry_values': pref.get('entryValues') or [],
               'dialog_title': pref.get('dialogTitle') or '', 'dialog_message': pref.get('dialogMessage') or '',
               'secret': bool(re.search(r'password|passwd|secret|token|authorization|cookie|credential|api.?key', str(key) + ' ' + title, re.I))}
        rows.append(row)
    result = {'source_id': str(source['id']), 'name': str(source.get('name') or 'Source'),
              'configurable': bool(source.get('isConfigurable')), 'preferences': rows}
    result['revision'] = hashlib.sha256(json.dumps(result, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return result


async def preferences_snapshot(client, source):
    source = source_id(source)
    query = f'''query AutoSourcarrSourcePreferences($id: LongString!) {{
      source(id: $id) {{ id name isConfigurable preferences {{ {PREFERENCE_FIELDS} }} }}
    }}'''
    try:
        data = await client.graphql(query, {'id': source})
    except SuwayomiError as exc:
        # An extension may put a password in its own exception. Do not return its
        # arbitrary text to browser diagnostics or the application's request logs.
        raise SourceControlFailed('This source could not provide its settings. Update the extension or check source-engine logs.') from None
    item = data.get('source')
    if not isinstance(item, dict) or str(item.get('id')) != source:
        raise ValueError('Source is no longer installed. Refresh Sources before opening its settings.')
    return normalize_preferences(item, item.get('preferences') or [])


def preference_change(snapshot, change):
    if not isinstance(change, dict):
        raise ValueError('Provide one source setting change')
    if change.get('revision') != snapshot['revision']:
        raise SourcePreferencesConflict('Source settings changed. Reload them before saving your changes.')
    index = change.get('index')
    if type(index) is not int or not 0 <= index < len(snapshot['preferences']):
        raise SourcePreferencesConflict('Source setting position changed. Reload its settings.')
    pref = snapshot['preferences'][index]
    if change.get('key') != pref['key'] or change.get('type') != pref['type']:
        raise SourcePreferencesConflict('Source setting identity changed. Reload its settings.')
    if not pref['enabled'] or not pref['visible']:
        raise ValueError('This source setting is currently unavailable')
    kind, value = pref['type'], change.get('value')
    if kind in {'switch', 'checkbox'}:
        if type(value) is not bool:
            raise ValueError('This setting requires an on/off value')
    elif kind in {'text', 'list'}:
        if not isinstance(value, str) or len(value) > 8192 or '\x00' in value:
            raise ValueError('This setting requires text of at most 8192 characters')
        if kind == 'list' and value not in pref['entry_values']:
            raise ValueError('Choose one of the source setting options')
    elif kind == 'multiselect':
        if not isinstance(value, list) or len(value) > len(pref['entry_values']) or any(not isinstance(v, str) or v not in pref['entry_values'] for v in value):
            raise ValueError('Choose only the available source setting options')
        value = list(dict.fromkeys(value))
    else:
        raise ValueError('This source setting type is not supported by the source engine')
    field = next(v[1] for v in TYPE_FIELDS.values() if v[0] == kind)
    return {'position': index, field: value}


async def _drain_mutation(awaitable):
    """A disconnected browser cannot release the maintenance lane mid-mutation."""
    task = asyncio.create_task(awaitable)
    try:
        return await asyncio.shield(task)
    except asyncio.CancelledError:
        while not task.done():
            try:
                await asyncio.shield(task)
            except asyncio.CancelledError:
                continue
            except Exception:
                break
        if task.done() and not task.cancelled():
            task.exception()
        raise


async def update_preference(client, source, change):
    async def mutate():
        current = await preferences_snapshot(client, source)
        checked = preference_change(current, change)
        query = f'''mutation AutoSourcarrSourcePreference($input: UpdateSourcePreferenceInput!) {{
          updateSourcePreference(input: $input) {{
            source {{ id name isConfigurable }} preferences {{ {PREFERENCE_FIELDS} }}
          }}
        }}'''
        try:
            data = await client.graphql(query, {'input': {'source': current['source_id'], 'change': checked}})
        except SuwayomiError:
            raise SourceControlFailed('The source could not confirm the setting change. Reload settings to check its saved value before retrying.') from None
        payload = data.get('updateSourcePreference') or {}
        item = payload.get('source')
        if not isinstance(item, dict) or str(item.get('id')) != current['source_id'] or not isinstance(payload.get('preferences'), list):
            raise SourceControlFailed('The source did not confirm the setting change. Reload settings before retrying.')
        return normalize_preferences(item, payload['preferences'])
    return await _drain_mutation(mutate())


def inspect_apk(data, filename):
    filename = str(filename or '')
    if not filename.lower().endswith('.apk') or len(filename) > 240 or any(c in filename for c in '/\\\r\n\x00'):
        raise ValueError('Choose a source APK file with a plain filename')
    if not 100 < len(data) <= MAX_PACKAGE:
        raise ValueError('Source APK must be nonempty and at most 32 MiB')
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            names = [x.filename for x in entries]
            if len(entries) > 4096 or len(set(names)) != len(names) or sum(x.file_size for x in entries) > 160 * 1024 * 1024:
                raise ValueError('APK archive is oversized or contains duplicate paths')
            for item in entries:
                if item.filename.startswith('/') or '\\' in item.filename or '..' in item.filename.split('/') or stat.S_ISLNK(item.external_attr >> 16) or item.flag_bits & 1:
                    raise ValueError('APK archive contains an unsafe or encrypted path')
            manifest = archive.getinfo('AndroidManifest.xml')
            if manifest.file_size > 256 * 1024:
                raise ValueError('APK manifest is too large')
            pkg = manifest_package(archive.read(manifest))
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){2,}', pkg) or len(pkg) > 200:
                raise ValueError('APK package identity is invalid')
            if not any(re.fullmatch(r'classes(?:[2-9][0-9]*)?\.dex', n) for n in names):
                raise ValueError('The file does not contain Android extension code; choose its APK, not a renamed JAR')
            if pkg == 'eu.kanade.tachiyomi.extension.all.autosourcarr':
                raise ValueError('Install the AutoSourcarr reader extension in your main Suwayomi, not in its integrated source engine')
            return {'pkgName': pkg, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
    except (zipfile.BadZipFile, KeyError, IndexError, UnicodeError, RuntimeError, struct.error, ET.ParseError):
        raise ValueError('This file is not a readable source APK') from None


async def install_uploaded_apk(client, data, filename):
    """Use Suwayomi's native multipart APK installer; do not replay uncertain work."""
    checked = await run_blocking(inspect_apk, data, filename)
    async def install():
        pkg, digest = checked['pkgName'], checked['sha256']
        previous, baseline = None, None
        receipt = {**checked, 'state': 'preparing', 'started_at': time.time(), 'backup_saved': False}
        receipt_path = storage() / 'uploads' / digest / 'receipt.json'
        rows = (await client.graphql(INVENTORY))['extensions']['nodes']
        previous = next((x for x in rows if x.get('pkgName') == pkg and x.get('isInstalled')), None)
        if previous and settings.source_engine_mode == 'integrated':
            baseline = await run_blocking(save_backup, previous)
            receipt['backup_saved'] = True
            receipt['backup_sha256'] = hashlib.sha256(baseline).hexdigest()
        await run_blocking(atomic, receipt_path.parent / 'uploaded.apk', data)
        receipt['state'] = 'installing'
        await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
        install_returned, inventory_loaded, confirmed = False, False, False
        try:
            # Never forward the user's filename: one package cannot collide with a
            # different package's installed jar through a reused upload filename.
            await client.install_extension_file(pkg + '.apk', data, apk=True)
            install_returned = True
            rows = (await client.graphql(INVENTORY))['extensions']['nodes']
            inventory_loaded = True
            current = next((x for x in rows if x.get('pkgName') == pkg and x.get('isInstalled')), None)
            if not current:
                raise SuwayomiError('Source engine did not confirm installation')
            confirmed = True
            receipt.update(state='installed', completed_at=time.time())
            await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
            await run_blocking(source_orphans.set_label, current, True, 'manual-upload')
            source_orphans.accept_inventory(rows)
            current = (await run_blocking(source_orphans.annotate_extensions, [current]))[0]
            return {'extension': current, 'backup_saved': receipt['backup_saved'], 'sha256': digest,
                    'message': 'Source APK installed in Installed orphan. Website access and chapter retrieval have not been checked.'}
        except (httpx.RequestError, TimeoutError):
            receipt.update(state='uncertain', message='Connection interrupted. Refresh installed sources before retrying; no automatic retry was sent.')
            await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
            raise SourceControlFailed(receipt['message']) from None
        except SourceWorkDeferred:
            receipt.update(state='deferred', message='Source engine maintenance interrupted this request. Refresh installed sources before retrying.')
            await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
            raise
        except Exception as exc:
            if isinstance(exc, httpx.HTTPStatusError) and exc.response.status_code in {408, 429, 502, 503, 504}:
                # These may come from a gateway while the native converter is
                # still running. A restoration would race that admitted install.
                receipt.update(state='uncertain', message='Source engine or gateway could not confirm the APK install. Refresh installed sources before retrying; no rollback or install retry was sent.')
                await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
                raise SourceControlFailed(receipt['message']) from None
            if confirmed or (install_returned and not inventory_loaded):
                receipt.update(state='installed' if confirmed else 'uncertain', message=(
                    'Source APK installed, but its local upload details could not be saved. Refresh installed sources; do not install it again.' if confirmed else
                    'The engine accepted the APK, but installed-source confirmation failed. Refresh installed sources before retrying; no rollback or install retry was sent.'))
                try:
                    await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
                except OSError:
                    pass
                raise SourceControlFailed(receipt['message']) from None
            # A completed error response may follow native replacement unloading.
            # Restore only a known-complete failure; never race a timed-out install.
            restored = False
            if baseline is not None:
                try:
                    name = previous['apkName'][:-4] + '.jar'
                    await client.install_extension_file(name, baseline, apk=False)
                    rows = (await client.graphql(INVENTORY))['extensions']['nodes']
                    restored = any(x.get('pkgName') == pkg and x.get('isInstalled') and identities(x) == identities(previous) for x in rows)
                except Exception:
                    pass
            receipt.update(state='restored' if restored else 'failed', completed_at=time.time())
            receipt['message'] = ('Source APK installation failed; the previous version was restored.' if restored else
                'Source APK installation was not confirmed. Refresh installed sources before retrying.' + (' A previous-package backup is retained.' if baseline else ''))
            await run_blocking(atomic, receipt_path, json.dumps(receipt).encode())
            raise SourceControlFailed(receipt['message']) from None
    return await _drain_mutation(install())
