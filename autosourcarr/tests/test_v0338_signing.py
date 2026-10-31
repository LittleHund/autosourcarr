"""Actual shell helper failure contracts with synthetic signer output only."""
from pathlib import Path
import shutil
import subprocess

import pytest

PROJECT = Path(__file__).resolve().parents[2]


def shell():
    bash = shutil.which('bash')
    if bash:
        return bash
    git = shutil.which('git')
    candidate = Path(git).parent.parent / 'usr/bin/sh.exe' if git else None
    if candidate is not None and candidate.is_file():
        return str(candidate)
    pytest.skip('No GNU Bash available for the signing control-flow fixture')


@pytest.mark.parametrize('sign_status,verify_status,digest,expected,calls', [
    (42, 7, '', 42, ['sign']),
    (0, 7, 'a' * 64, 7, ['sign', 'verify']),
    (0, 0, 'b' * 64, 0, ['sign', 'verify']),
    (0, 0, 'INVALID_FINGERPRINT', 18, ['sign', 'verify']),
])
def test_nested_signing_checks_status_once_and_never_puts_password_in_argv(tmp_path, sign_status, verify_status, digest, expected, calls):
    text = (PROJECT / 'extension/build-in-container.sh').read_text(encoding='utf-8')
    helper = text[text.index('sign_android_apk() {'):text.index("printf 'Suwayomi JAR base URL:")]
    tool = '''
prepare_signing_key(){ printf SYNTHETIC_SIGNING_PASSWORD; }
find_apksigner(){ printf fixture_signer; }
fixture_signer(){
  printf '%s\n' "$1" >> calls.txt
  for argument in "$@"; do
    if [[ "$argument" == *SYNTHETIC_SIGNING_PASSWORD* ]]; then printf leaked > argv-password.txt; fi
  done
  if [[ "$1" == sign ]]; then
    [[ "${AUTOSOURCARR_SIGNING_PASSWORD:-}" == SYNTHETIC_SIGNING_PASSWORD ]] && printf present > env-password.txt
    if [[ SIGN_STATUS == 0 ]]; then printf SYNTHETIC_APK_BYTES > signed.apk; fi
    return SIGN_STATUS
  fi
  if [[ -n 'DIGEST' ]]; then printf 'Signer #1 certificate SHA-256 digest: %s\n' 'DIGEST'; fi
  return VERIFY_STATUS
}
'''.replace('SIGN_STATUS', str(sign_status)).replace('VERIFY_STATUS', str(verify_status)).replace('DIGEST', digest)
    script = ('PATH="/usr/bin:/bin:$PATH"\nset +o posix\nshopt -u inherit_errexit\nset -euo pipefail\n'
              + tool + '\nSIGN_KEYSTORE=fixture.jks\nSIGN_ALIAS=fixture\n' + helper
              + '\nfingerprint="$(sign_android_apk input.apk signed.apk)"\nprintf "%s" "$fingerprint"\n')
    result = subprocess.run([shell(), '--noprofile', '--norc', '-s'], input=script,
                            cwd=tmp_path, text=True, capture_output=True, timeout=10)
    assert result.returncode == expected, result.stderr
    assert (tmp_path / 'calls.txt').read_text().splitlines() == calls
    assert not (tmp_path / 'argv-password.txt').exists()
    assert (tmp_path / 'env-password.txt').read_text() == 'present'
    assert result.stdout == (digest if expected == 0 else '')
