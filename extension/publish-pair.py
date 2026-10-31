"""Commit validated reader artifacts through one per-instance generation pointer.

The existing APK/JAR publishers validate and prepare private staging files. Their
individual pointers never become current downloads. Immutable artifacts are moved
first; one final manifest replace selects both or neither. Legacy pointers are
retained for upgrade fallback and are not rewritten by generation publication.
"""
from __future__ import annotations

import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def publish(apk, jar, config, version):
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
        raise ValueError('Invalid release version')
    uid = int(os.environ.get('AUTOSOURCARR_PUBLISH_UID', os.getuid()))
    gid = int(os.environ.get('AUTOSOURCARR_PUBLISH_GID', os.getgid()))
    if uid < 0 or gid < 0:
        raise ValueError('Publication ownership must be nonnegative')
    root = Path(config) / 'extension-downloads'
    root.mkdir(parents=True, exist_ok=True)
    if root.is_symlink():
        raise ValueError('Publication directory must not be a symbolic link')
    os.chmod(root, 0o700)
    if (root.stat().st_uid, root.stat().st_gid) != (uid, gid):
        os.chown(root, uid, gid)
    lock_fd = os.open(root / '.publication.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    directory_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fchmod(lock_fd, 0o600)
        if (os.fstat(lock_fd).st_uid, os.fstat(lock_fd).st_gid) != (uid, gid):
            os.fchown(lock_fd, uid, gid)
        fcntl.flock(lock_fd, fcntl.LOCK_EX)
        helpers = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix='.publish-pair.', dir=root) as temporary:
            stage = Path(temporary)
            # Ownership failures, invalid APKs and failed JAR preparation all happen
            # before the shared current pointer is touched.
            subprocess.run(['bash', str(helpers / 'publish-apk.sh'), str(apk), str(stage), version], check=True)
            subprocess.run([sys.executable, str(helpers / 'publish-jar.py'), str(jar), str(stage), version], check=True)
            staged_downloads = stage / 'extension-downloads'
            metadata = {
                'format': 1,
                'app_version': version,
                'apk': json.loads((staged_downloads / 'current.json').read_bytes()),
                'jar': json.loads((staged_downloads / 'current-jar.json').read_bytes()),
            }
            for kind in ('apk', 'jar'):
                item = metadata[kind]
                digest = item.get('sha256', '')
                if item.get('app_version') != version or not re.fullmatch(r'[a-f0-9]{64}', digest):
                    raise ValueError('Invalid staged build metadata')
                artifact = staged_downloads / f'{version}-{digest}.{kind}'
                with artifact.open('rb') as stream:
                    os.fsync(stream.fileno())
                os.replace(artifact, root / artifact.name)
        # Remove the staging tree before committing. Cleanup failure must not
        # report a failed build after the new generation became current.
        os.fsync(directory_fd)  # Durable immutable paths precede their selection.
        fd, temporary_pointer = tempfile.mkstemp(prefix='.generation.', dir=root)
        pointer = Path(temporary_pointer)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(json.dumps(metadata, separators=(',', ':')).encode() + b'\n')
                stream.flush()
                os.fsync(stream.fileno())
                os.fchmod(stream.fileno(), 0o600)
                if (os.fstat(stream.fileno()).st_uid, os.fstat(stream.fileno()).st_gid) != (uid, gid):
                    os.fchown(stream.fileno(), uid, gid)
            # Last fallible publication operation. A crash before this retains the
            # old generation; after this both artifacts are selected together.
            os.replace(pointer, root / 'current-build.json')
            try:
                os.fsync(directory_fd)
            except OSError:
                # The commit already happened. Do not make the builder roll back
                # its distribution or imply that the previous generation remains
                # selected merely because the filesystem could not confirm sync.
                print('Reader generation committed; filesystem crash durability could not be confirmed.', file=sys.stderr)
                return {'committed': True, 'durable': False}
            return {'committed': True, 'durable': True}
        finally:
            pointer.unlink(missing_ok=True)
    finally:
        os.close(directory_fd)
        os.close(lock_fd)


if __name__ == '__main__':
    try:
        publish(*sys.argv[1:])
    except subprocess.CalledProcessError as exc:
        raise SystemExit(exc.returncode) from None
