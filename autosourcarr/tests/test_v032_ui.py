from pathlib import Path
import subprocess


def test_color_roles_migrate_and_remain_independent_and_readable():
    result = subprocess.run(['node', str(Path(__file__).with_name('ui_v032.js'))], capture_output=True, text=True, timeout=20)
    assert result.returncode == 0, result.stdout + result.stderr
