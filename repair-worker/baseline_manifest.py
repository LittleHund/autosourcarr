"""Read package metadata and prove a validation envelope for converted APKs.

The original installed JAR is never modified. A retained APK is not proof until
the isolated engine's conversion has identical uncompressed JAR payloads.
"""
import hashlib
import io
import json
import math
import re
import stat
import struct
import xml.etree.ElementTree as ET
import zipfile

MAX_MANIFEST = 256 * 1024
MAX_PACKAGE = 32 * 1024 * 1024
ANDROID = 'http://schemas.android.com/apk/res/android'
NS = '{' + ANDROID + '}'
ET.register_namespace('android', ANDROID)


def _binary_tree(data):
    def unpack(fmt, offset, end):
        if offset < 0 or offset + struct.calcsize(fmt) > end:
            raise ValueError('Incomplete binary extension manifest')
        return struct.unpack_from(fmt, data, offset)

    kind, header, size = unpack('<HHI', 0, len(data))
    if kind != 3 or header != 8 or size != len(data):
        raise ValueError('Unknown or incomplete binary extension manifest')
    strings, stack, root = None, [], None
    offset, elements, attribute_characters = header, 0, 0
    while offset < size:
        kind, header, chunk_size = unpack('<HHI', offset, size)
        end = offset + chunk_size
        if header < 8 or chunk_size < header or end > size:
            raise ValueError('Invalid binary manifest chunk')
        if kind == 1:
            if strings is not None or root is not None or header < 28:
                raise ValueError('Invalid binary manifest string pool')
            count, styles, flags, start, styles_start = unpack('<IIIII', offset + 8, offset + header)
            if count > 10000 or styles > 10000 or header + (count + styles) * 4 > start or start > chunk_size:
                raise ValueError('Invalid binary manifest string offsets')
            string_end = offset + (styles_start or chunk_size)
            if not offset + start <= string_end <= end:
                raise ValueError('Invalid binary manifest string boundary')

            def length(pos, width):
                value = unpack('<B' if width == 1 else '<H', pos, string_end)[0]
                pos += width
                high = 0x80 if width == 1 else 0x8000
                if value & high:
                    second = unpack('<B' if width == 1 else '<H', pos, string_end)[0]
                    value = ((value & (high - 1)) << (8 * width)) | second
                    pos += width
                return value, pos

            strings, decoded_units, decoded = [], 0, {}
            for index in range(count):
                relative = unpack('<I', offset + header + index * 4, offset + start)[0]
                if relative in decoded:
                    strings.append(decoded[relative])
                    continue
                pos = offset + start + relative
                if pos >= string_end:
                    raise ValueError('Invalid binary manifest string location')
                units, pos = length(pos, 1 if flags & 0x100 else 2)
                decoded_units += units
                if decoded_units > MAX_MANIFEST * 2:
                    raise ValueError('Binary manifest decoded strings exceed their limit')
                if flags & 0x100:
                    byte_count, pos = length(pos, 1)
                    stop = pos + byte_count
                    if stop >= string_end or data[stop] != 0:
                        raise ValueError('Unterminated binary manifest string')
                    value = data[pos:stop].decode('utf-8')
                    if len(value.encode('utf-16-le')) // 2 != units:
                        raise ValueError('Invalid binary manifest UTF8 length')
                else:
                    stop = pos + units * 2
                    if stop + 2 > string_end or data[stop:stop + 2] != b'\0\0':
                        raise ValueError('Unterminated binary manifest string')
                    value = data[pos:stop].decode('utf-16-le')
                if '\x00' in value:
                    raise ValueError('Invalid binary manifest string')
                strings.append(value)
                decoded[relative] = value
        elif kind == 0x180:
            if strings is None or header != 8 or (chunk_size - header) % 4:
                raise ValueError('Invalid binary manifest resource map')
        elif kind in {0x100, 0x101, 0x102, 0x103}:
            if strings is None or header != 16:
                raise ValueError('Invalid binary manifest XML node')

            def string(index, *, optional=False):
                if optional and index == 0xffffffff:
                    return ''
                if index >= len(strings):
                    raise ValueError('Invalid binary manifest string index')
                return strings[index]

            def qualified(namespace, name):
                name = string(name)
                if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_.-]*', name):
                    raise ValueError('Invalid binary manifest XML name')
                uri = string(namespace, optional=True)
                return '{' + uri + '}' + name if uri else name

            extension = offset + header
            if kind in {0x100, 0x101}:
                prefix, uri = unpack('<II', extension, end)
                string(prefix, optional=True)
                string(uri)
            elif kind == 0x102:
                namespace, name, start, step, count, _, _, _ = unpack('<IIHHHHHH', extension, end)
                if step < 20 or count > 1024 or start < 20 or extension + start + count * step > end:
                    raise ValueError('Invalid binary manifest attributes')
                element = ET.Element(qualified(namespace, name))
                for index in range(count):
                    position = extension + start + index * step
                    namespace, name, raw, value_size, reserved, value_type, value = unpack('<IIIHBBI', position, end)
                    if value_size != 8 or reserved:
                        raise ValueError('Invalid binary manifest typed value')
                    key = qualified(namespace, name)
                    if key in element.attrib:
                        raise ValueError('Duplicate binary manifest attribute')
                    if raw != 0xffffffff:
                        text = string(raw)
                    elif value_type == 3:
                        text = string(value)
                    elif value_type in {0x10, 0x11}:
                        text = str(value)
                    elif value_type == 4:
                        number = struct.unpack('<f', struct.pack('<I', value))[0]
                        if not math.isfinite(number):
                            raise ValueError('Invalid binary manifest float')
                        # Match the shortest float32 decimal, rather than exposing
                        # Python's float64 promotion of a library value such as1.6.
                        for precision in range(1, 10):
                            text = format(number, '.' + str(precision) + 'g')
                            if struct.pack('<f', float(text)) == struct.pack('<I', value):
                                break
                        if '.' not in text and 'e' not in text.lower():
                            text += '.0'
                    elif value_type == 0x12:
                        text = 'true' if value else 'false'
                    elif 0x1c <= value_type <= 0x1f:
                        text = '#' + format(value, '08x')
                    elif value_type in {1, 2}:
                        text = ('@' if value_type == 1 else '?') + '0x' + format(value, '08x')
                    else:
                        raise ValueError('Unsupported binary manifest typed value')
                    attribute_characters += len(key) + len(text)
                    if attribute_characters > MAX_MANIFEST * 2:
                        raise ValueError('Binary manifest expanded attributes exceed their limit')
                    element.set(key, text)
                elements += 1
                if elements > 4096 or len(stack) >= 64:
                    raise ValueError('Binary manifest tree exceeds its limit')
                if stack:
                    stack[-1].append(element)
                elif root is None:
                    root = element
                else:
                    raise ValueError('Multiple binary manifest roots')
                stack.append(element)
            else:
                namespace, name = unpack('<II', extension, end)
                if not stack or stack[-1].tag != qualified(namespace, name):
                    raise ValueError('Unbalanced binary manifest elements')
                stack.pop()
        else:
            raise ValueError('Unsupported binary manifest chunk')
        offset = end
    if root is None or stack:
        raise ValueError('Incomplete binary manifest elements')
    return root


def manifest_tree(raw):
    if not raw or len(raw) > MAX_MANIFEST:
        raise ValueError('Extension manifest is empty or exceeds its size limit')
    try:
        if raw[:2] == b'\x03\0':
            return _binary_tree(raw)
        if raw.startswith((b'\xff\xfe', b'\xfe\xff')):
            text = raw.decode('utf-16')
        elif raw.startswith(b'<\0'):
            text = raw.decode('utf-16-le')
        elif raw.startswith(b'\0<'):
            text = raw.decode('utf-16-be')
        else:
            text = raw.decode('utf-8-sig')
        if re.search(r'<!\s*(?:DOCTYPE|ENTITY)\b', text, re.I):
            raise ValueError('Extension manifest contains unsupported XML declarations')
        root = ET.fromstring(text)
        pending, count = [(root, 1)], 0
        while pending:
            element, depth = pending.pop()
            count += 1
            if count > 4096 or depth > 64:
                raise ValueError('Extension manifest tree exceeds its limit')
            pending.extend((child, depth + 1) for child in element)
        return root
    except (ET.ParseError, UnicodeError, struct.error, IndexError):
        raise ValueError('Extension manifest is not readable') from None


def _identity(root):
    if root.tag != 'manifest' or len(root.findall('application')) != 1:
        raise ValueError('Extension manifest lacks one package application')
    package = root.get('package', '')
    metadata = {}
    for element in root.findall('./application/meta-data'):
        name = element.get(NS + 'name')
        if name in {'tachiyomi.extension.class', 'tachiyomix.extensionLib'}:
            if name in metadata:
                raise ValueError('Extension manifest has ambiguous identity metadata')
            metadata[name] = element.get(NS + 'value', '')
    classname = str(metadata.get('tachiyomi.extension.class') or '').strip()
    if classname.startswith('.'):
        classname = package + classname
    library = metadata.get('tachiyomix.extensionLib')
    if not library or library == '0':
        library = root.get(NS + 'versionName', '').rsplit('.', 1)[0]
    qualified = r'[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)+'
    if not re.fullmatch(qualified, package) or not re.fullmatch(qualified, classname) or not re.fullmatch(r'[0-9]{1,3}\.[0-9]{1,3}', library):
        raise ValueError('Extension manifest lacks package, source entrypoint or library version')
    return package, classname, library


def read_manifest(data):
    if len(data) > MAX_PACKAGE:
        raise ValueError('Package exceeds the repair size limit')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) > 4096 or len({info.filename for info in entries}) != len(entries):
            raise ValueError('Package has duplicate or excessive entries')
        info = archive.getinfo('AndroidManifest.xml')
        if info.file_size > MAX_MANIFEST:
            raise ValueError('Extension manifest exceeds the size limit')
        return manifest_tree(archive.read(info))


def manifest_contract(data):
    return _identity(read_manifest(data))


def _payloads(data):
    if not 100 < len(data) <= MAX_PACKAGE:
        raise ValueError('Package is empty or exceeds the size limit')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) > 4096 or len({i.filename for i in entries}) != len(entries) or sum(i.file_size for i in entries) > 160 * 1024 * 1024:
            raise ValueError('Package has duplicate or oversized entries')
        result = {}
        for info in entries:
            name = info.filename
            if name.startswith('/') or '\\' in name or '..' in name.split('/') or info.flag_bits & 1 or stat.S_ISLNK(info.external_attr >> 16) or info.file_size > MAX_PACKAGE:
                raise ValueError('Package has an unsafe entry')
            if not info.is_dir():
                payload = archive.read(info)
                result[name] = (len(payload), hashlib.sha256(payload).hexdigest())
        return result


class ConversionMismatch(ValueError):
    """Unverified comparison facts, never a successful conversion proof."""
    def __init__(self, installed, checked, baseline, converted):
        shared = installed.keys() & checked.keys()
        changed = {name for name in shared if installed[name] != checked[name]}
        missing, added = installed.keys() - checked.keys(), checked.keys() - installed.keys()
        self.comparison = {'installed_entries': len(installed), 'converted_entries': len(checked),
            'identical_entries': len(shared) - len(changed), 'changed_entries': len(changed),
            'missing_entries': len(missing), 'added_entries': len(added),
            'changed_class_entries': sum(name.endswith('.class') for name in changed),
            'changed_resource_entries': sum(not name.endswith('.class') for name in changed),
            'missing_class_entries': sum(name.endswith('.class') for name in missing),
            'added_class_entries': sum(name.endswith('.class') for name in added),
            'installed_jar_sha256': hashlib.sha256(baseline).hexdigest(),
            'converted_jar_sha256': hashlib.sha256(converted).hexdigest()}
        super().__init__('Retained APK conversion does not match the installed JAR payloads '
                         '(changed '+str(len(changed))+', missing '+str(len(missing))+', added '+str(len(added))+'); no identity was inferred')


def installed_envelope(baseline, apk):
    """Prepare exact installed bytes with real APK metadata for cold validation.

    This is not APK conversion equivalence or an import success. The controller
    must bind the backup hash/version and cold-import this envelope against the
    installed source identities before accepting its loading contract.
    """
    installed = _payloads(baseline)
    if 'AndroidManifest.xml' in installed:
        raise ValueError('Installed JAR already has a native manifest')
    apk_payloads = _payloads(apk)
    if 'classes.dex' not in apk_payloads:
        raise ValueError('Retained package is not the original Android APK')
    with zipfile.ZipFile(io.BytesIO(apk)) as source:
        info = source.getinfo('AndroidManifest.xml')
        if info.file_size > MAX_MANIFEST:
            raise ValueError('APK manifest exceeds the size limit')
        root = manifest_tree(source.read(info))
    identity = _identity(root)
    classname = identity[1].replace('.', '/') + '.class'
    if classname not in installed:
        raise ValueError('Original APK entrypoint is absent from the installed JAR')
    if not re.fullmatch(r'[1-9][0-9]{0,9}', root.get(NS + 'versionCode', '')) or not root.get(NS + 'versionName'):
        raise ValueError('Original APK version metadata is unavailable')
    if not any(element.get(NS + 'name') == 'tachiyomi.extension' for element in root.findall('uses-feature')):
        raise ValueError('Original APK is not a Tachiyomi extension')
    xml = ET.tostring(root, encoding='utf-8', xml_declaration=True)
    if len(xml) > MAX_MANIFEST:
        raise ValueError('Decoded APK manifest exceeds the native import limit')
    result = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(baseline)) as source, zipfile.ZipFile(result, 'w', zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            target.writestr(info, source.read(info))
        target.writestr('AndroidManifest.xml', xml)
    envelope = result.getvalue()
    if len(envelope) > MAX_PACKAGE:
        raise ValueError('Validation envelope exceeds the package limit')
    digest = lambda value: hashlib.sha256(value).hexdigest()
    proof = {'source_apk_sha256': digest(apk), 'installed_jar_sha256': digest(baseline),
             'payload_sha256': digest(json.dumps(installed, sort_keys=True).encode()),
             'validation_envelope_sha256': digest(envelope)}
    return envelope, identity, proof


def restore_manifest(baseline, apk, converted):
    installed, checked = _payloads(baseline), _payloads(converted)
    if 'AndroidManifest.xml' in installed:
        raise ValueError('Installed JAR already has a native manifest')
    if installed != checked:
        raise ConversionMismatch(installed, checked, baseline, converted)
    envelope, identity, proof = installed_envelope(baseline, apk)
    proof['converted_jar_sha256'] = hashlib.sha256(converted).hexdigest()
    return envelope, identity, proof
