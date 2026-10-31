"""Bounded post-setup cleanup; deliberately not a general-purpose file deleter."""
from pathlib import Path
import os
import re
import time
from .setup_store import record_cleanup

REPORT = re.compile(r'^(?:autosourcarr-v(\d+\.\d+\.\d+)-(?:upgrade|regression-audit)|(?:UPGRADE|REGRESSION-AUDIT)-(\d+\.\d+\.\d+))\.md$', re.I)
SKIP = {'data', 'config', 'source-host', 'node_modules', '.git', '.venv', 'venv', 'build', 'bundled', 'repairs', 'extension', 'translate'}


def scan(root, version, *, max_files=1500, seconds=2):
    root = Path(root)
    removed, skipped, checked = [], 0, 0
    deadline = time.monotonic() + seconds
    current = tuple(map(int, version.split('.')))
    stack = [(root, 0)]
    device = root.stat().st_dev
    limited = False
    while stack:
        directory, depth = stack.pop()
        try:
            with os.scandir(directory) as entries:
                for item in entries:
                    checked += 1
                    if checked > max_files or time.monotonic() > deadline:
                        limited = True; stack.clear(); break
                    if item.is_symlink(): continue
                    path = Path(item.path)
                    try:
                        stat = item.stat(follow_symlinks=False)
                        if stat.st_dev != device: continue
                        if item.is_dir(follow_symlinks=False):
                            if depth < 4 and item.name not in SKIP: stack.append((path, depth+1))
                            continue
                        if not item.is_file(follow_symlinks=False): continue
                        obsolete = False
                        # Python bytecode is generated, disposable, and only removed when its source is gone.
                        relative = directory.relative_to(root).parts
                        if relative in (('app', '__pycache__'), ('autosourcarr', 'app', '__pycache__')) and item.name.endswith('.pyc'):
                            source = directory.parent / (item.name.split('.cpython-')[0]+'.py')
                            obsolete = '.cpython-' in item.name and not source.exists()
                        # Only old one-off release reports in the project root, with a matching product/version header.
                        match = REPORT.fullmatch(item.name) if depth == 0 else None
                        if match and stat.st_size <= 256000:
                            old = match.group(1) or match.group(2)
                            with path.open('r', errors='replace') as report: header = report.read(512)
                            obsolete = tuple(map(int, old.split('.'))) < current and bool(re.search(r'^#.*AutoSourcarr.*'+re.escape(old)+r'\b', header, re.M|re.I))
                        if obsolete:
                            # Recheck inode before deleting; never follow a replacement link.
                            now = path.lstat()
                            if (now.st_ino, now.st_dev, now.st_mtime_ns) == (stat.st_ino, stat.st_dev, stat.st_mtime_ns):
                                path.unlink(); removed.append(str(path.relative_to(root)))
                    except OSError: skipped += 1
        except OSError: skipped += 1
    return {'status': 'completed', 'removed': removed, 'checked': checked, 'skipped': skipped, 'limited': limited,
            'scope': 'App installation only. Databases, configuration, source profiles, downloads, repair backups and unknown files were retained. Host folders not mounted in the app cannot be scanned.'}


def cleanup_after_setup(config_dir, transaction, version):
    try:
        app_root = Path(__file__).resolve().parent.parent
        # A source checkout has a project VERSION above autosourcarr/. Container /app is its own root.
        if (app_root.parent / 'scripts' / 'check-project.sh').is_file(): app_root = app_root.parent
        result = scan(app_root, version)
    except Exception:
        result = {'status': 'failed', 'removed': [], 'message': 'Setup was saved, but cleanup could not complete. Your existing files were retained.'}
    try: record_cleanup(Path(config_dir), transaction, result)
    except (OSError, ValueError): pass
