from pathlib import Path
import subprocess


def test_solver_diagnostics_and_settings_shortcut_preserve_drafts():
    result = subprocess.run(['node', str(Path(__file__).with_name('ui_v033.js'))], capture_output=True, text=True, timeout=20)
    assert result.returncode == 0, result.stdout + result.stderr
