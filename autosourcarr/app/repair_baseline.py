"""Select retained APK evidence without replacing an installed rollback binary."""
import base64
import io
import json
import math
import os
import re
import stat
import time
import zipfile

LIMIT = 32 * 1024 * 1024


def _read_regular(path, maximum):
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= maximum:
        raise ValueError('Retained source upload is unsafe, empty or oversized')
    descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
    try:
        stream = os.fdopen(descriptor, 'rb')
    except BaseException:
        os.close(descriptor)
        raise
    with stream:
        actual = os.fstat(stream.fileno())
        if not stat.S_ISREG(actual.st_mode) or actual.st_nlink != 1 or (before.st_dev, before.st_ino) != (actual.st_dev, actual.st_ino):
            raise ValueError('Retained source upload changed while being opened')
        data = stream.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError('Retained source upload exceeded its size limit')
    return data


def retained_apk(extension, *, with_provenance=False, baseline_sha256=None):
    from .source_maintenance import storage, sha
    from .source_controls import inspect_apk
    choices, seen = [], 0
    for relative, accepted in (('repair-apks', 'provided'), ('uploads', 'installed')):
        root = storage()/relative
        if not root.is_dir() or root.is_symlink() or getattr(root, 'is_junction', lambda: False)():
            continue
        for folder in root.iterdir():
            seen += 1
            if seen > 512:
                raise ValueError('Retained upload selection exceeded its bounded receipt limit; no package was selected')
            if not re.fullmatch(r'[0-9a-f]{64}', folder.name) or folder.is_symlink() or getattr(folder, 'is_junction', lambda: False)() or not folder.is_dir():
                continue
            try:
                receipt = json.loads(_read_regular(folder/'receipt.json', 65536))
            except (OSError, ValueError, UnicodeError):
                continue
            if not isinstance(receipt, dict) or receipt.get('state') != accepted or receipt.get('pkgName') != extension['pkgName']:
                continue
            if accepted == 'provided':
                if (receipt.get('confirmed_original') is not True
                        or receipt.get('target_version') != extension.get('versionName')
                        or not baseline_sha256 or receipt.get('target_jar_sha256') != baseline_sha256):
                    continue
            stamp = receipt.get('completed_at')
            if type(stamp) not in {int, float} or not math.isfinite(stamp) or stamp < 0 or receipt.get('sha256') != folder.name:
                continue
            choices.append((stamp, folder, 'user-confirmed-original' if accepted == 'provided' else 'recorded-installed-upload'))
        if choices:
            break  # Explicit original evidence takes precedence over older uploads.
    if not choices:
        return None
    choices.sort(key=lambda item: item[0], reverse=True)
    if len(choices) > 1 and choices[0][0] == choices[1][0]:
        raise ValueError('Original source upload receipts are ambiguous; no APK was selected')
    folder = choices[0][1]
    data = _read_regular(folder/'uploaded.apk', LIMIT)
    if sha(data) != folder.name or inspect_apk(data, 'retained.apk')['pkgName'] != extension['pkgName']:
        raise ValueError('Retained original APK failed its saved digest or package check')
    return (data, choices[0][2]) if with_provenance else data


def save_original_apk(extension, data, *, confirmed=False):
    """Store evidence only; conversion proof still runs in the isolated worker."""
    from .source_controls import inspect_apk
    from .source_maintenance import atomic, storage, sha, installed_path
    if confirmed is not True:
        raise ValueError('Confirm that this is the original APK used for the selected installed version')
    checked = inspect_apk(data, 'original.apk')
    if checked['pkgName'] != extension['pkgName']:
        raise ValueError('The APK belongs to a different package; choose the installed source it came from')
    version = extension.get('versionName')
    if not isinstance(version, str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}', version):
        raise ValueError('Installed source version is unavailable; refresh its inventory before attaching the original APK')
    target_digest = sha(_read_regular(installed_path(extension), LIMIT))
    root = storage()/'repair-apks'
    if root.is_symlink() or getattr(root, 'is_junction', lambda: False)():
        raise ValueError('Original APK evidence directory is unsafe')
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    folder = root/sha(data)
    if folder.is_symlink() or getattr(folder, 'is_junction', lambda: False)():
        raise ValueError('Original APK evidence path is unsafe')
    folder.mkdir(exist_ok=True, mode=0o700)
    for name in ('uploaded.apk', 'receipt.json'):
        target = folder/name
        if target.is_symlink() or target.exists() and (not stat.S_ISREG(target.lstat().st_mode) or target.lstat().st_nlink != 1):
            raise ValueError('Original APK evidence file is unsafe')
    atomic(folder/'uploaded.apk', data)
    atomic(folder/'receipt.json', json.dumps({**checked, 'state': 'provided', 'confirmed_original': True,
        'target_version': version, 'target_jar_sha256': target_digest, 'completed_at': time.time()}).encode())
    return {'pkg_name': extension['pkgName'], 'sha256': checked['sha256'],
            'message': 'Original APK retained as repair evidence. Installed source and settings are preserved; start a new repair to verify the match.'}


def repair_input(extension, *, raw_first=False):
    from .source_maintenance import save_backup
    baseline = save_backup(extension)
    result = {'baseline': base64.b64encode(baseline).decode('ascii')}
    if raw_first:
        result.update(raw_first=True,installed_version=str(extension.get('versionName') or ''),
            installed_sources=[{key:row.get(key,'') for key in ('id','name','lang','baseUrl')}
                               for row in extension.get('source',{}).get('nodes',[])])
    try:
        with zipfile.ZipFile(io.BytesIO(baseline)) as archive:
            names = archive.namelist()
    except zipfile.BadZipFile:
        return result  # Preserve worker rejection of other malformed old inputs.
    if 'AndroidManifest.xml' in names or not any(name.endswith('.class') for name in names):
        return result
    from .source_maintenance import sha
    original = retained_apk(extension, with_provenance=True, baseline_sha256=sha(baseline))
    if original is None:
        if raw_first:return result
        raise ValueError('The installed APK was converted to a JAR without its manifest, and its matching original APK is not retained. In Tasks → Repairs → Start a source repair, select this source and attach its Original APK for repair, then start a new repair. Existing source settings, library data and rollback backup are retained.')
    apk, basis = original
    result.update(source_apk=base64.b64encode(apk).decode('ascii'), original_apk_basis=basis, installed_version=str(extension.get('versionName') or ''),
                  installed_sources=[{key: row.get(key, '') for key in ('id', 'name', 'lang', 'baseUrl')}
                                     for row in extension.get('source', {}).get('nodes', [])])
    return result
