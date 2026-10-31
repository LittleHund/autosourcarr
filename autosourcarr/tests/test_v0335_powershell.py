"""Exercise native build-wrapper exit handling without Docker or a build."""
import os
import shutil
import subprocess
from pathlib import Path

import pytest


@pytest.mark.skipif(os.name != 'nt', reason='Windows native command regression')
@pytest.mark.parametrize('exit_code', [0, 17])
def test_builder_propagates_native_exit_before_reporting_success(tmp_path, exit_code):
    shell = shutil.which('pwsh') or shutil.which('powershell')
    if not shell:
        pytest.skip('PowerShell unavailable')
    root = tmp_path / 'project'
    extension = root / 'extension'
    extension.mkdir(parents=True)
    shutil.copyfile(Path(__file__).resolve().parents[2] / 'extension/build.ps1', extension / 'build.ps1')
    fake_bin = tmp_path / 'bin'
    fake_bin.mkdir()
    (fake_bin / 'docker.cmd').write_text(f'@exit /b {exit_code}\n')
    environment = {**os.environ, 'PATH': str(fake_bin) + os.pathsep + os.environ['PATH'],
                   'AUTOSOURCARR_HOST_CONFIG_DIR': str(tmp_path / 'config')}
    result = subprocess.run([shell, '-NoProfile', '-NonInteractive', '-File', str(extension / 'build.ps1')],
                            env=environment, capture_output=True, text=True, timeout=15)
    assert result.returncode == exit_code, result.stdout + result.stderr
    assert ('Build output is in extension/dist' in result.stdout) == (exit_code == 0)
