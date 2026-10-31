"""Execute duplicate-review concurrency and snapshot regressions in real UI code."""
from pathlib import Path
import subprocess


def test_duplicate_review_concurrency_and_snapshot_races():
    tests = Path(__file__).resolve().parent
    result = subprocess.run(
        ["node", str(tests / "ui_v091.js"), str(tests.parent / "app/static/app.js")],
        text=True, capture_output=True, timeout=15,
    )
    assert result.returncode == 0, result.stdout + result.stderr
