"""The host validator accepts safe relative paths without requiring a full JDK."""
from pathlib import Path
import shutil
import subprocess
import zipfile

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / 'extension' / 'verify-suwayomi-jar.sh'

@pytest.mark.parametrize('case', ['valid', 'dex-only', 'bad-magic', 'missing-manifest', 'bad-archive'])
def test_jar_validator_on_standard_host(tmp_path, case):
    if not shutil.which('unzip'):
        pytest.skip('unzip not installed')
    archive = tmp_path / 'release with spaces.jar'
    if case == 'bad-archive':
        archive.write_bytes(b'not a zip')
    else:
        with zipfile.ZipFile(archive, 'w') as z:
            if case != 'missing-manifest':
                z.writestr('AndroidManifest.xml', b'manifest')
            if case == 'dex-only':
                z.writestr('classes.dex', b'dex\n035\0')
            else:
                z.writestr('a/b/C.class', b'\xca\xfe\xba\xbehello' if case != 'bad-magic' else b'nope')
    result = subprocess.run(['bash', str(SCRIPT), archive.name], cwd=tmp_path, capture_output=True, text=True)
    assert (result.returncode == 0) == (case == 'valid'), result.stdout + result.stderr
    assert sorted(p.name for p in tmp_path.iterdir()) == [archive.name]
