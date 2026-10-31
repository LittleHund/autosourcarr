"""Run response-ownership and import-acknowledgment regressions against shipped JS."""
import subprocess
from pathlib import Path


def test_response_ownership_and_local_review_rendering():
    script = Path(__file__).with_name('ui_v0326_response_ownership.js')
    result = subprocess.run(['node', str(script)], text=True, capture_output=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
