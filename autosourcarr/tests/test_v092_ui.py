"""Workspace behavior uses the shipped JS, including theme contrast and races."""
from pathlib import Path
import subprocess


def test_workspace_behavior_scenarios():
    script=Path(__file__).with_name('ui_v092.js')
    result=subprocess.run(['node',str(script)],text=True,capture_output=True,timeout=15)
    assert result.returncode==0,result.stdout+result.stderr
