"""Serve the APK published by our authenticated, persistent-key SDK builder.

No compilation or filesystem scan occurs at startup. The build manifest is local
trusted configuration; its hash binds Android-tool validation to immutable bytes.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import stat
from threading import BoundedSemaphore

MAX_APK_BYTES = 32 * 1024 * 1024
PACKAGE = 'eu.kanade.tachiyomi.extension.all.autosourcarr'
_download_slots = BoundedSemaphore(2)


class ApkUnavailable(ValueError):
    pass


def extension_version(version: str) -> str:
    major, minor, patch = (int(x) for x in version.split('.'))
    return f'1.4.{max(9, major * 1_000_000 + minor * 1_000 + patch)}'


def _open_regular(path: Path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    if not stat.S_ISREG(os.fstat(fd).st_mode):
        os.close(fd)
        raise ApkUnavailable('The APK publication is not a regular file.')
    return os.fdopen(fd, 'rb')


def _publication_metadata(directory: Path, kind: str) -> dict:
    try:
        stream = _open_regular(directory / 'current-build.json')
    except FileNotFoundError:
        # Older releases and standalone legacy publishers remain readable until
        # this instance's first complete generation has been published.
        name = 'current.json' if kind == 'apk' else 'current-jar.json'
        with _open_regular(directory / name) as stream:
            raw = stream.read(8193)
    else:
        with stream:
            raw = stream.read(8193)
        if len(raw) > 8192:
            raise ValueError('oversized build metadata')
        publication = json.loads(raw)
        if (not isinstance(publication, dict) or publication.get('format') != 1
                or not all(isinstance(publication.get(item), dict) for item in ('apk', 'jar'))
                or any(publication[item].get('app_version') != publication.get('app_version')
                       for item in ('apk', 'jar'))):
            raise ValueError('invalid build generation')
        return publication[kind]
    if len(raw) > 8192:
        raise ValueError('oversized build metadata')
    data = json.loads(raw)
    if not isinstance(data, dict):
        raise ValueError('invalid metadata')
    return data


def _metadata(config_dir: str, version: str) -> tuple[dict, Path]:
    directory = Path(config_dir) / 'extension-downloads'
    try:
        data = _publication_metadata(directory, 'apk')
        if data.get('app_version') != version:
            raise ApkUnavailable(f'No APK has been published for v{version}. Run the extension builder for this release.')
        digest = data.get('sha256', '')
        size = data.get('size')
        if (not isinstance(digest, str) or not re.fullmatch(r'[a-f0-9]{64}', digest)
                or type(size) is not int or not 0 < size <= MAX_APK_BYTES
                or data.get('package') != PACKAGE
                or data.get('extension_version') != extension_version(version)
                or data.get('signature_verified') is not True):
            raise ValueError('invalid build metadata')
        # No user- or manifest-controlled relative path is accepted.
        path = directory / f'{version}-{digest}.apk'
        with _open_regular(path) as stream:
            if os.fstat(stream.fileno()).st_size != size:
                raise ValueError('size changed')
        return data, path
    except ApkUnavailable:
        raise
    except FileNotFoundError as exc:
        raise ApkUnavailable(f'APK not built for v{version}. Run the extension builder once to publish it here.') from exc
    except (OSError, ValueError, TypeError, KeyError) as exc:
        raise ApkUnavailable('The published APK is incomplete or invalid. Rebuild the extension.') from exc


def apk_status(config_dir: str, version: str) -> dict:
    result = {'version': version, 'available': False, 'build_command': 'bash extension/build.sh'}
    try:
        data, _ = _metadata(config_dir, version)
        result.update(available=True, size=data['size'], extension_version=data['extension_version'],
                      filename=f'AutoSourcarr-v{version}-Tachiyomi-Mihon.apk')
    except ApkUnavailable as exc:
        result['message'] = str(exc)
    return result


def apk_bytes(config_dir: str, version: str) -> bytes:
    if not _download_slots.acquire(blocking=False):
        raise ApkUnavailable('Two APK downloads are being prepared. Retry shortly.')
    try:
        data, path = _metadata(config_dir, version)
        try:
            # Read, hash and serve the same bytes. Publication can advance safely
            # while a client downloads the preceding immutable artifact.
            with _open_regular(path) as stream:
                content = stream.read(MAX_APK_BYTES + 1)
        except OSError as exc:
            raise ApkUnavailable('The APK changed during download. Retry or rebuild it.') from exc
        if len(content) != data['size'] or hashlib.sha256(content).hexdigest() != data['sha256']:
            raise ApkUnavailable('The APK failed its integrity check. Rebuild the extension before downloading.')
        return content
    finally:
        _download_slots.release()


def jar_metadata(config_dir, version):
    directory = Path(config_dir)/'extension-downloads'
    try:
        data = _publication_metadata(directory, 'jar')
        digest, size = data.get('sha256',''), data.get('size')
        if (data.get('app_version') != version or data.get('validated') is not True
                or not re.fullmatch('[a-f0-9]{64}',digest) or type(size) is not int or not 0 < size <= MAX_APK_BYTES):
            raise ValueError('Invalid JAR metadata')
        path = directory/f'{version}-{digest}.jar'
        with _open_regular(path) as stream:
            if os.fstat(stream.fileno()).st_size != size: raise ValueError('JAR size changed')
        return data,path
    except (OSError,ValueError,TypeError,AttributeError) as exc:
        raise ApkUnavailable('No validated native JAR is available for this release. Build reader extensions from Status.') from exc


def jar_status(config_dir, version):
    try:
        data,_ = jar_metadata(config_dir,version)
        return {'available':True,'version':version,'size':data['size']}
    except ApkUnavailable as exc: return {'available':False,'version':version,'message':str(exc)}


def jar_bytes(config_dir, version):
    if not _download_slots.acquire(blocking=False): raise ApkUnavailable('Downloads busy; retry shortly')
    try:
        data,path = jar_metadata(config_dir,version)
        try:
            with _open_regular(path) as stream: content = stream.read(MAX_APK_BYTES+1)
        except OSError as exc: raise ApkUnavailable('JAR changed during download; retry or rebuild') from exc
        if len(content) != data['size'] or hashlib.sha256(content).hexdigest() != data['sha256']:
            raise ApkUnavailable('JAR failed its integrity check; rebuild before downloading')
        return content
    finally: _download_slots.release()
