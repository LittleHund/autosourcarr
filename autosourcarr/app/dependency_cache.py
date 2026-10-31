"""Build isolated, ABI-keyed TrueNAS dependencies without deleting the old cache.

This module uses only Python's standard library and is executable before pip runs.
"""
from __future__ import annotations
import fcntl
import hashlib
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile

IMPORTS = "fastapi uvicorn httpx rapidfuzz unidecode PIL imagehash pydantic_settings".split()

def _valid(directory: Path) -> bool:
    script = ("import importlib,pathlib,sys; root=pathlib.Path(sys.argv[1]).resolve(); "
              "mods=[importlib.import_module(n) for n in sys.argv[2:]]; "
              "assert all(pathlib.Path(m.__file__).resolve().is_relative_to(root) for m in mods)")
    env = dict(os.environ, PYTHONPATH=str(directory))
    return subprocess.run([sys.executable, "-c", script, str(directory), *IMPORTS],
                          env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0

def ensure_dependencies(app_dir: Path, deps_dir: Path, requirements: Path) -> Path:
    app_dir, deps_dir = app_dir.resolve(), deps_dir.resolve()
    protected = [app_dir, Path(os.environ.get("DATA_DIR", "/data")).resolve(),
                 Path(os.environ.get("SETTINGS_CONFIG_DIR", "/config")).resolve()]
    if deps_dir == Path(deps_dir.anchor) or any(deps_dir == p or deps_dir in p.parents for p in protected):
        raise ValueError("Refusing unsafe dependency cache directory; use a dedicated directory")
    digest = hashlib.sha256(requirements.read_bytes() + repr((sys.implementation.cache_tag,
                            platform.machine(), platform.system(), platform.libc_ver())).encode()).hexdigest()
    deps_dir.mkdir(parents=True, exist_ok=True)
    # One cache may be mounted by two simultaneous restarts. Serialize publication.
    with (deps_dir / ".install.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        current = deps_dir / "current"
        active = current.resolve() if current.is_symlink() else deps_dir
        marker = active / ".requirements.sha256"
        if marker.is_file() and marker.read_text().strip() == digest and _valid(active):
            return active
        print("AutoSourcarr: staging Python dependencies; previous cache is retained...", file=sys.stderr)
        stage = Path(tempfile.mkdtemp(prefix=".packages-", dir=deps_dir))
        temporary_link = deps_dir / ".current.next"
        try:
            subprocess.run([sys.executable, "-m", "pip", "install", "--disable-pip-version-check",
                            "--no-cache-dir", "--retries", os.environ.get("PIP_RETRIES", "5"),
                            "--timeout", os.environ.get("PIP_TIMEOUT", "60"), "--target", str(stage),
                            "-r", str(requirements)], check=True, stdout=sys.stderr)
            if not _valid(stage):
                raise RuntimeError("Installed dependency cache failed import/path validation")
            (stage / ".requirements.sha256").write_text(digest)
            temporary_link.unlink(missing_ok=True)
            temporary_link.symlink_to(stage.name, target_is_directory=True)
            os.replace(temporary_link, current)
            # Keep prior generations: a different process may still import them.
            # They are not on the new process's PYTHONPATH.
            return stage
        except BaseException:
            temporary_link.unlink(missing_ok=True)
            shutil.rmtree(stage, ignore_errors=True)
            raise

if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("usage: dependency_cache.py APP_DIR DEPS_DIR REQUIREMENTS")
    print(ensure_dependencies(*(Path(value) for value in sys.argv[1:])))
