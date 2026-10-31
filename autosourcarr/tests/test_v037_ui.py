import subprocess
from pathlib import Path


def test_versioned_diagnostics_export_and_readable_errors():
    result = subprocess.run(['node', str(Path(__file__).with_name('ui_v037_diagnostics.js'))], capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stdout + result.stderr
