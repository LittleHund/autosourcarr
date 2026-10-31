"""Execute the release's UI regressions with controlled asynchronous responses."""
from pathlib import Path
import subprocess


def test_ui_bootstrap_feedback_authentication_and_preference_races():
    tests = Path(__file__).resolve().parent
    result = subprocess.run(
        ["node", str(tests / "ui_v090.js"), str(tests.parent / "app/static/app.js")],
        text=True, capture_output=True, timeout=15,
    )
    assert result.returncode == 0, result.stdout + result.stderr
