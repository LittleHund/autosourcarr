import os
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]


@pytest.mark.parametrize("target", ["root", "app", "data", "config", "parent"])
def test_dependency_bootstrap_refuses_to_purge_unsafe_paths(tmp_path, target):
    app, data, config = [tmp_path / name for name in ("app", "data", "config")]
    for path in (app, data, config): path.mkdir()
    sentinel = data / "keep.txt"
    sentinel.write_text("retain me")
    deps = {"root": "/", "app": app, "data": data, "config": config, "parent": tmp_path}[target]
    env = {**os.environ, "AUTOSOURCARR_DEPS_DIR": str(deps), "AUTOSOURCARR_APP_DIR": str(app), "DATA_DIR": str(data), "SETTINGS_CONFIG_DIR": str(config)}
    proc = subprocess.run(["sh", str(ROOT / "autosourcarr/start-truenas.sh")], env=env, text=True, capture_output=True, timeout=5)
    assert proc.returncode != 0
    assert "Refusing unsafe dependency cache directory" in proc.stderr
    assert sentinel.read_text() == "retain me"
