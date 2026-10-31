from pathlib import Path
import subprocess


def test_theme_export_and_candidate_behaviors():
    result=subprocess.run(['node',str(Path(__file__).with_name('ui_v093.js'))],capture_output=True,text=True,timeout=15)
    assert result.returncode==0,result.stdout+result.stderr
