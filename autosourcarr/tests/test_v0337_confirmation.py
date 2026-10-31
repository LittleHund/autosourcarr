import subprocess
from pathlib import Path


def test_manual_reset_confirmation_is_visible_and_cancel_safe():
    result=subprocess.run(['node',str(Path(__file__).with_name('ui_v0337_confirmation.js'))],capture_output=True,text=True,timeout=15)
    assert result.returncode==0,result.stdout+result.stderr
