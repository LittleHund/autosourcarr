"""Converted-APK rollback proof with actual ZIP/XML bytes; no JVM execution."""
import importlib
import io
from pathlib import Path
import struct
import zipfile

import pytest

PKG = 'eu.kanade.tachiyomi.extension.en.example'
ANDROID = 'http://schemas.android.com/apk/res/android'
XML = ('<manifest xmlns:android="' + ANDROID + '" package="' + PKG + '" '
       'android:versionName="1.4.23" android:versionCode="23">'
       '<uses-feature android:name="tachiyomi.extension"/>'
       '<application android:label="Tachiyomi: Example">'
       '<meta-data android:name="tachiyomi.extension.class" android:value=".Example"/>'
       '</application></manifest>').encode()
CLASS = PKG.replace('.', '/') + '/Example.class'
CODE = b'\xca\xfe\xba\xbe' + b'SYNTHETIC_BYTECODE_PAYLOAD'


def package(entries, date=(2020, 1, 1, 0, 0, 0)):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        for name, value in entries:
            archive.writestr(zipfile.ZipInfo(name, date_time=date), value)
    return stream.getvalue()


def binary_manifest(utf8=True, library=None):
    """Independent Android resource structures: strings, elements, typed values."""
    values = ['manifest', 'package', PKG, ANDROID, 'versionName', '1.4.23', 'versionCode',
              'uses-feature', 'name', 'tachiyomi.extension', 'application', 'meta-data',
              'tachiyomi.extension.class', 'value', '.Example']
    if library is not None:
        values.append('tachiyomix.extensionLib')
    indexes = {value: i for i, value in enumerate(values)}
    strings, offsets = b'', []
    for value in values:
        offsets.append(len(strings))
        if utf8:
            encoded = value.encode('utf-8')
            strings += bytes((len(value), len(encoded))) + encoded + b'\0'
        else:
            strings += struct.pack('<H', len(value)) + value.encode('utf-16-le') + b'\0\0'
    start = 28 + len(values) * 4
    pool = (struct.pack('<HHI', 1, 28, start + len(strings))
            + struct.pack('<IIIII', len(values), 0, 0x100 if utf8 else 0, start, 0)
            + b''.join(struct.pack('<I', offset) for offset in offsets) + strings)

    def element(name, attributes=()):
        attrs = b''
        for namespace, key, value, typed in attributes:
            namespace = 0xffffffff if namespace is None else indexes[namespace]
            raw = 0xffffffff if typed else indexes[value]
            if typed == 'float':
                kind, data = 4, struct.unpack('<I', struct.pack('<f', value))[0]
            else:
                kind, data = (0x10, value) if typed else (3, indexes[value])
            attrs += struct.pack('<IIIHBBI', namespace, indexes[key], raw, 8, 0, kind, data)
        extension = struct.pack('<IIHHHHHH', 0xffffffff, indexes[name], 20, 20, len(attributes), 0, 0, 0)
        return struct.pack('<HHIII', 0x102, 16, 36 + len(attrs), 1, 0xffffffff) + extension + attrs

    def end(name):
        return struct.pack('<HHIIIII', 0x103, 16, 24, 1, 0xffffffff, 0xffffffff, indexes[name])

    nodes = (element('manifest', [(None, 'package', PKG, False), (ANDROID, 'versionName', '1.4.23', False),
                                  (ANDROID, 'versionCode', 23, True)])
             + element('uses-feature', [(ANDROID, 'name', 'tachiyomi.extension', False)]) + end('uses-feature')
             + element('application')
             + element('meta-data', [(ANDROID, 'name', 'tachiyomi.extension.class', False), (ANDROID, 'value', '.Example', False)])
             + end('meta-data'))
    if library is not None:
        nodes += element('meta-data', [(ANDROID, 'name', 'tachiyomix.extensionLib', False), (ANDROID, 'value', library, 'float')]) + end('meta-data')
    nodes += end('application') + end('manifest')
    return struct.pack('<HHI', 3, 8, 8 + len(pool) + len(nodes)) + pool + nodes


@pytest.fixture
def manifest(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2] / 'repair-worker'))
    return importlib.import_module('baseline_manifest')


@pytest.mark.parametrize('raw', [XML, binary_manifest(True), binary_manifest(False), XML.decode().encode('utf-16')],
                         ids=['plain', 'binary-utf8', 'binary-utf16', 'plain-utf16'])
def test_original_apk_loading_contract_survives_plain_and_binary_xml(manifest, raw):
    apk = package([('AndroidManifest.xml', raw), ('classes.dex', b'dex\n035\0fixture')])
    assert manifest.manifest_contract(apk) == (PKG, PKG + '.Example', '1.4')


def test_matching_conversion_adds_only_a_validation_manifest_and_retains_raw_backup(manifest):
    entries = [(CLASS, CODE), ('assets/retained.txt', b'ORIGINAL_ASSET')]
    baseline = package(entries)
    converted = package(list(reversed(entries)), date=(2024, 2, 3, 0, 0, 0))
    apk = package([('AndroidManifest.xml', binary_manifest()), ('classes.dex', b'dex\n035\0fixture')])
    before = bytes(baseline)
    envelope, identity, proof = manifest.restore_manifest(baseline, apk, converted)
    assert identity == (PKG, PKG + '.Example', '1.4')
    assert baseline == before
    with zipfile.ZipFile(io.BytesIO(envelope)) as archive:
        assert set(archive.namelist()) == {CLASS, 'assets/retained.txt', 'AndroidManifest.xml'}
        for name, value in entries:
            assert archive.read(name) == value
    assert manifest.manifest_contract(envelope) == identity
    assert proof['installed_jar_sha256'] != proof['converted_jar_sha256']  # ZIP order/timestamps can differ.
    assert all(len(value) == 64 for value in proof.values())


@pytest.mark.parametrize('changed', [[(CLASS, CODE + b'CHANGED')], [(CLASS, CODE), ('assets/extra', b'new')],
                                    [('Other.class', CODE)]])
def test_wrong_or_changed_conversion_cannot_manufacture_rollback_identity(manifest, changed):
    baseline = package([(CLASS, CODE)])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    with pytest.raises(ValueError, match='match'):
        manifest.restore_manifest(baseline, apk, package(changed))


def test_class_declared_by_original_apk_must_exist_in_the_installed_bytes(manifest):
    baseline = package([('Unrelated.class', CODE)])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    with pytest.raises(ValueError, match='entrypoint'):
        manifest.restore_manifest(baseline, apk, baseline)


@pytest.mark.parametrize('raw', [binary_manifest()[:-1], b'\x03\0\x08\0\xff\xff\xff\xff',
                               XML.replace(b'</application>', b'<meta-data android:name="tachiyomi.extension.class" android:value=".Other"/></application>'),
                               b'<!DOCTYPE manifest [<!ENTITY x "bad">]>' + XML],
                         ids=['truncated', 'bad-length', 'duplicate-identity', 'entity-declaration'])
def test_incomplete_or_ambiguous_manifest_fails_closed(manifest, raw):
    apk = package([('AndroidManifest.xml', raw), ('classes.dex', b'dex\n035\0fixture')])
    with pytest.raises(ValueError):
        manifest.manifest_contract(apk)


def test_native_baseline_is_not_silently_rewrapped(manifest):
    native = package([('AndroidManifest.xml', XML), (CLASS, CODE)])
    apk = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    with pytest.raises(ValueError, match='already'):
        manifest.restore_manifest(native, apk, native)


def test_duplicate_archive_entries_cannot_supply_different_manifest_interpretations(manifest):
    with pytest.warns(UserWarning, match='Duplicate name'):
        data = package([('AndroidManifest.xml', XML), ('AndroidManifest.xml', XML.replace(b'.Example', b'.Other'))])
    with pytest.raises(ValueError, match='duplicate'):
        manifest.manifest_contract(data)


def test_utf16_entity_declaration_is_rejected_before_xml_parsing(manifest):
    xml = ('<!DOCTYPE manifest [<!ENTITY x "bad">]>' + XML.decode()).encode('utf-16')
    with pytest.raises(ValueError, match='declaration'):
        manifest.manifest_contract(package([('AndroidManifest.xml', xml)]))


def test_real_public_suwayomi_apk_manifest_decodes_without_executing_its_code(manifest):
    # Generated manifest from the official Suwayomi extension1.4.29 APK:
    # https://raw.githubusercontent.com/Suwayomi/tachiyomi-extension/repo/apk/tachiyomi-all.tachidesk-v1.4.29.apk
    # APK SHA256:2214e48213d5001fd7ebdf9008b6d165af564fd8481352efbe535f30953b1db6
    raw = (Path(__file__).parent/'fixtures/suwayomi-extension-1.4.29-manifest.axml').read_bytes()
    data = package([('AndroidManifest.xml', raw)])
    pkg = 'eu.kanade.tachiyomi.extension.all.tachidesk'
    assert manifest.manifest_contract(data) == (pkg, pkg + '.Tachidesk', '1.4')
    assert manifest.read_manifest(data).get(manifest.NS + 'versionName') == '1.4.29'


@pytest.mark.parametrize('library', [1.4, 1.6])
def test_android_typed_float_library_metadata_keeps_its_declared_version(manifest, library):
    data = package([('AndroidManifest.xml', binary_manifest(library=library))])
    assert manifest.manifest_contract(data)[2] == str(library)


def test_nonfinite_binary_float_and_excessive_xml_depth_fail_closed(manifest):
    with pytest.raises(ValueError, match='float'):
        manifest.manifest_contract(package([('AndroidManifest.xml', binary_manifest(library=float('nan')))]))
    xml = b'<manifest>' + b'<nested>' * 70 + b'</nested>' * 70 + b'</manifest>'
    with pytest.raises(ValueError, match='limit'):
        manifest.manifest_contract(package([('AndroidManifest.xml', xml)]))
