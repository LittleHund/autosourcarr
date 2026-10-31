#!/usr/bin/env python3
"""Retain Android signing identity; stage only a genuinely new complete pair."""
from contextlib import contextmanager
import os
from pathlib import Path
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile

DIRECTORY = '.extension-signing'
ENV_FILE = 'signing.env'
KEY_FILE = 'autosourcarr-extension.jks'
PASSWORD_KEY = 'AUTOSOURCARR_EXTENSION_SIGNING_PASSWORD='
STAGE_PREFIX = '.extension-signing-stage-'


class SigningStateError(RuntimeError):
    def __init__(self, message, exit_code=15):
        super().__init__(message)
        self.exit_code = exit_code


@contextmanager
def _exclusive_lock(config):
    import fcntl
    descriptor = os.open(config / '.extension-signing.lock',
                         os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise SigningStateError('Signing lock is unsafe; existing identity was retained.')
        fcntl.flock(descriptor, fcntl.LOCK_EX)
        yield
    finally:
        os.close(descriptor)


def _regular_nonempty(path, *, maximum=None):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    return stat.S_ISREG(info.st_mode) and info.st_size > 0 and (maximum is None or info.st_size <= maximum)


def _password(folder):
    if not _regular_nonempty(folder / ENV_FILE, maximum=1024 * 1024) or not _regular_nonempty(folder / KEY_FILE):
        raise SigningStateError('Persistent Android signing state is incomplete or unsafe. Restore both retained signing files; no key was replaced.')
    with (folder / ENV_FILE).open('r', encoding='utf-8', newline='') as source:
        text = source.read(1024 * 1024 + 1)
    values = [line.removesuffix('\r')[len(PASSWORD_KEY):] for line in text.split('\n') if line.startswith(PASSWORD_KEY)]
    if len(values) != 1 or not values[0] or len(values[0]) > 4096 or '\x00' in values[0]:
        raise SigningStateError('Persistent signing password is missing or ambiguous. Existing signing files were retained.')
    return values[0]


def _sync_regular(path):
    with path.open('rb') as source:
        os.fsync(source.fileno())


def _sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _generate_keystore(path, password):
    # Keytool needs neither the reader key nor arbitrary app/JVM hooks.
    allowed = {'PATH', 'JAVA_HOME', 'LANG', 'LC_ALL', 'TZ', 'HOME', 'TMPDIR', 'SYSTEMROOT', 'WINDIR'}
    env = {key: value for key, value in os.environ.items() if key.upper() in allowed}
    env['AUTOSOURCARR_SIGNING_PASSWORD'] = password
    command = ['keytool', '-genkeypair', '-noprompt', '-keystore', str(path), '-storetype', 'JKS',
               '-storepass:env', 'AUTOSOURCARR_SIGNING_PASSWORD', '-keypass:env', 'AUTOSOURCARR_SIGNING_PASSWORD',
               '-alias', 'autosourcarr', '-keyalg', 'RSA', '-keysize', '4096', '-validity', '36500',
               '-dname', 'CN=AutoSourcarr Extension,OU=Local Extension,O=AutoSourcarr']
    try:
        result = subprocess.run(command, env=env, stdin=subprocess.DEVNULL, stdout=sys.stderr)
    except FileNotFoundError:
        raise SigningStateError('Keytool is unavailable; no signing identity was published.', 16) from None
    if result.returncode:
        code = result.returncode if result.returncode > 0 else 128 - result.returncode
        raise SigningStateError('Keytool generation failed; no signing identity was published.', code if 1 <= code <= 255 else 16)


def _remove_owned_stage(stage, config):
    # This is only a newly created private staging directory, never retained state.
    if stage.parent != config or not stage.name.startswith(STAGE_PREFIX) or stage.is_symlink():
        raise SigningStateError('Refusing unsafe signing staging cleanup.')
    if not stage.resolve().is_relative_to(config):
        raise SigningStateError('Refusing signing staging cleanup outside its config directory.')
    shutil.rmtree(stage)


def prepare(config):
    stage = None
    try:
        config = Path(config).resolve(strict=True)
        if not config.is_dir():
            raise SigningStateError('Signing configuration directory is unavailable.')
        with _exclusive_lock(config):
            destination = config / DIRECTORY
            try:
                info = destination.lstat()
            except FileNotFoundError:
                info = None
            if info is not None:
                if not stat.S_ISDIR(info.st_mode):
                    raise SigningStateError('Persistent signing directory is unsafe; no key was replaced.')
                try:
                    os.chmod(destination, 0o700)
                except PermissionError:
                    pass  # Retain legacy group-readable identities if owner differs.
                if any(destination.iterdir()):
                    password = _password(destination)
                    _sync_regular(destination / ENV_FILE)
                    _sync_regular(destination / KEY_FILE)
                    _sync_directory(destination)
                    _sync_directory(config)
                    return password
            stage = Path(tempfile.mkdtemp(prefix=STAGE_PREFIX, dir=config))
            os.chmod(stage, 0o700)
            password = secrets.token_hex(48)
            with (stage / ENV_FILE).open('x', encoding='utf-8') as target:
                os.chmod(stage / ENV_FILE, 0o600)
                target.write(PASSWORD_KEY + password + '\n')
                target.flush()
                os.fsync(target.fileno())
            _generate_keystore(stage / KEY_FILE, password)
            _password(stage)  # A successful tool exit must still produce the pair.
            os.chmod(stage / KEY_FILE, 0o600)
            _sync_regular(stage / KEY_FILE)
            _sync_directory(stage)
            # Same config filesystem; replace only absent or still-empty destination.
            # A newly nonempty directory makes rename fail, preserving that identity.
            os.replace(stage, destination)
            stage = None
            # After rename, retain the complete pair even if directory sync fails.
            # A retry must reuse it rather than generate a different Android identity.
            _sync_directory(config)
            print('Created persistent Android signing identity. Back up config/.extension-signing.', file=sys.stderr)
            return password
    except (OSError, UnicodeError):
        raise SigningStateError('Signing state could not be prepared or synced. Existing identity was retained; check config permissions and disk health.') from None
    finally:
        if stage is not None:
            try:
                _remove_owned_stage(stage, config)
            except (OSError, SigningStateError):
                print('Signing staging cleanup needs attention; retained identity was not changed.', file=sys.stderr)


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if len(args) != 1:
        print('Usage: signing_state.py <instance-config-directory>', file=sys.stderr)
        return 15
    def stop(*_):
        raise SystemExit(143)
    signal.signal(signal.SIGTERM, stop)
    try:
        password = prepare(args[0])
    except (SigningStateError, ImportError) as error:
        print('ERROR: ' + (str(error) if isinstance(error, SigningStateError) else 'Signing state locking is unavailable.'), file=sys.stderr)
        return error.exit_code if isinstance(error, SigningStateError) else 15
    print(password, end='', flush=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
