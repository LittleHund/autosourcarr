import subprocess
from pathlib import Path


def test_diagnostic_log_download_and_sensitive_browser_data():
    result = subprocess.run(['node',str(Path(__file__).with_name('ui_v0336_support_logs.js'))],
                            capture_output=True,text=True,timeout=20)
    assert result.returncode == 0, result.stdout+result.stderr


def test_ui_harness_handles_large_scripts_and_project_relative_fixtures():
    from test_v086_ui import node
    node("const assert=require('node:assert/strict');const payload='"+('x'*50000)+"';assert.equal(payload.length,50000);require('./autosourcarr/tests/ui_setup.js');")
