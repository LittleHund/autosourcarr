"""v0.2.85: failure-safe dependency and artifact staging."""
import os
from pathlib import Path
import shutil
import subprocess
from types import SimpleNamespace

import pytest
import app.dependency_cache as cache


def test_dependency_install_failure_preserves_working_cache_and_marker(monkeypatch, tmp_path):
    deps, app = tmp_path / "deps", tmp_path / "app"
    deps.mkdir(); app.mkdir()
    requirement = app / "requirements.txt"; requirement.write_text("example==1\n")
    (deps / "working-package.py").write_text("working")
    (deps / ".requirements.sha256").write_text("old-hash")
    def fail(*args, **kwargs):
        raise subprocess.CalledProcessError(1, args[0])
    monkeypatch.setattr(cache.subprocess, "run", fail)
    with pytest.raises(subprocess.CalledProcessError):
        cache.ensure_dependencies(app, deps, requirement)
    assert (deps / "working-package.py").read_text() == "working"
    assert (deps / ".requirements.sha256").read_text() == "old-hash"
    assert not list(deps.glob(".packages-*"))
    assert not (deps / "current").exists()


def test_dependency_publication_reuses_valid_cache_and_keys_python_platform(monkeypatch, tmp_path):
    deps, app = tmp_path / "deps", tmp_path / "app"
    app.mkdir(); req = app / "requirements.txt"; req.write_text("example==1\n")
    calls = []
    def install(command, **kwargs):
        target = Path(command[command.index("--target") + 1])
        (target / "installed").touch()
        calls.append(target)
        return SimpleNamespace(returncode=0)
    monkeypatch.setattr(cache.subprocess, "run", install)
    monkeypatch.setattr(cache, "_valid", lambda directory: (directory / "installed").exists())
    first = cache.ensure_dependencies(app, deps, req)
    assert (deps / "current").resolve() == first
    assert cache.ensure_dependencies(app, deps, req) == first and len(calls) == 1
    monkeypatch.setattr(cache.platform, "machine", lambda: "different-architecture")
    second = cache.ensure_dependencies(app, deps, req)
    assert second != first and first.exists() and len(calls) == 2
    assert (deps / "current").resolve() == second


def test_dependency_validation_cannot_accidentally_use_system_site_packages(monkeypatch, tmp_path):
    monkeypatch.setattr(cache, "IMPORTS", ["json"])
    assert not cache._valid(tmp_path)  # stdlib json is importable but outside this cache

@pytest.mark.parametrize("path", ["root", "app", "parent"])
def test_dependency_directory_rejects_dangerous_locations(tmp_path, path):
    app = tmp_path / "app"; app.mkdir()
    req = app / "requirements.txt"; req.write_text("")
    deps = {"root": Path("/"), "app": app, "parent": tmp_path}[path]
    with pytest.raises(ValueError, match="dedicated directory"):
        cache.ensure_dependencies(app, deps, req)


def test_failed_gradle_bootstrap_does_not_remove_previous_extension_outputs(tmp_path):
    root = Path(__file__).resolve().parents[2]
    work = tmp_path / "project"
    shutil.copytree(root / "extension/module", work / "extension/module")
    shutil.copyfile(root / "extension/keiyoushi-default.lock", work / "extension/keiyoushi-default.lock")
    (work / "VERSION").write_text("0.2.85")
    checkout = work / "extension/.build/extensions-source"
    (checkout / ".git").mkdir(parents=True)
    gradle = checkout / "gradlew"; gradle.write_text("#!/bin/sh\nexit 1\n"); gradle.chmod(0o755)
    out = work / "extension/dist"; out.mkdir()
    (out / "known-good.jar").write_bytes(b"previous JAR")
    (out / "known-good.apk").write_bytes(b"previous APK")
    config = work / "config"; config.mkdir()
    (config / "autosourcarr.env").write_text(
        "AUTOSOURCARR_INTERNAL_URL=http://autosourcarr:8282\n"
        "AUTOSOURCARR_PUBLIC_URL=https://reader.example.invalid\n"
        "AUTOSOURCARR_READER_API_TOKEN=" + "a" * 64 + "\n"
    )
    fakebin = tmp_path / "bin"; fakebin.mkdir()
    git = fakebin / "git"
    git.write_text('''#!/usr/bin/env python3
import os,sys
from pathlib import Path
if 'get-url' in sys.argv:
    print('https://github.com/keiyoushi/extensions-source.git')
elif 'rev-parse' in sys.argv:
    print((Path(os.environ['AUTOSOURCARR_BUILD_ROOT'])/'extension/keiyoushi-default.lock').read_text().strip())
''')
    git.chmod(0o755)
    script = (root / "extension/build-in-container.sh").read_text()
    path = tmp_path / "build.sh"; path.write_text(script)
    shutil.copyfile(root / "extension/reader_build_config.py", tmp_path / "reader_build_config.py")
    result = subprocess.run(["bash", str(path)], env=dict(os.environ,
        PATH=str(fakebin) + ":" + os.environ["PATH"], GRADLE_BOOTSTRAP_RETRIES="1", AUTOSOURCARR_BUILD_ROOT=str(work),
        AUTOSOURCARR_CONFIG_DIR=str(config), AUTOSOURCARR_BUILD_MODE="single",
        AUTOSOURCARR_INTERNAL_URL="", AUTOSOURCARR_PUBLIC_URL="", AUTOSOURCARR_READER_API_TOKEN=""),
        text=True, capture_output=True, timeout=10)
    assert result.returncode == 3, result.stderr
    assert (out / "known-good.jar").read_bytes() == b"previous JAR"
    assert (out / "known-good.apk").read_bytes() == b"previous APK"
    assert not list((work / "extension").glob(".dist-stage.*"))


def test_reader_key_is_scoped_on_every_network_hop():
    root = Path(__file__).resolve().parents[2]
    text = (root / "extension/module/src/autosourcarr/AutoSourcarr.kt").read_text()
    interceptor = text.split(".addNetworkInterceptor {", 1)[1].split("private fun endpoint", 1)[0]
    for check in ("target.scheme == origin.scheme", "target.host == origin.host", "target.port == origin.port"):
        assert check in interceptor
    assert 'removeHeader("X-AutoSourcarr-API-Key")' in interceptor
    assert 'chain.proceed(scoped)' in interceptor
