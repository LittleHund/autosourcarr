#!/usr/bin/env python3
"""Stage maintained tool sources into the existing ./autosourcarr build context.

Release packaging runs this before creating the archive, so a TrueNAS Compose
build does not need to run a separate host command. No configs/keys/caches copied.
"""
from __future__ import annotations
import argparse
import hashlib
from pathlib import Path
import shutil
import tempfile

EXTENSION_FILES = (
    'build-in-container.sh', 'reader_build_config.py', 'signing_state.py', 'publish-apk.sh', 'publish-jar.py', 'publish-pair.py', 'verify-suwayomi-jar.sh',
    'verify-tachiyomi-apk.sh',
)
SCRIPTS = ('cleanup-old-helper-files.sh','check-linux-text.py')


def sources(project: Path) -> dict[str, Path]:
    files: dict[str, Path] = {'build/VERSION': project / 'VERSION'}
    for src in sorted((project / 'repair-worker').glob('*.py')):
        files['repair-worker/' + src.name] = src
    for src in sorted((project / 'source_tools').glob('*.py')):
        # Both runtime entrypoints receive the same maintained pure contracts.
        files['source_tools/' + src.name] = src
        files['repair-worker/source_tools/' + src.name] = src
    for src in sorted((project / 'repair-worker/licenses').glob('*.txt')):
        files['repair-worker/licenses/' + src.name] = src
    files['repair-worker/requirements.txt'] = project / 'repair-worker/requirements.txt'
    files['start-solver.sh'] = project / 'scripts/start-solver.sh'
    for name in ('repair-seccomp.json','autosourcarr-internal.apparmor','LICENSE-moby','repair-host-setup.md'):
        files['host-security/'+name] = project / 'security' / name
    for name in EXTENSION_FILES:
        files['build/extension/' + name] = project / 'extension' / name
    for src in sorted((project / 'extension/module').rglob('*')):
        if src.is_file() and src.suffix in {'.kt', '.kts', '.png', '.xml'}:
            files['build/extension/module/' + src.relative_to(project / 'extension/module').as_posix()] = src
    for name in SCRIPTS:
        files['build/scripts/' + name] = project / 'scripts' / name
    files['build/extension/keiyoushi-default.lock'] = project / 'extension/keiyoushi-default.lock'
    lock = project / 'extension/keiyoushi-commit.lock'
    if lock.is_file():
        files['build/extension/keiyoushi-commit.lock'] = lock
    for name, src in files.items():
        if not src.is_file() or src.is_symlink() or any(p.is_symlink() for p in src.parents if p != project.parent):
            raise SystemExit(f'Expected ordinary canonical source file: {name}')
    return files


def same(a: Path, b: Path) -> bool:
    return not b.is_symlink() and b.is_file() and hashlib.sha256(a.read_bytes()).digest() == hashlib.sha256(b.read_bytes()).digest()


def sync(project: Path, *, check: bool = False) -> None:
    project = project.resolve(strict=True)
    destination = project / 'autosourcarr/bundled'
    expected = sources(project)
    if destination.is_symlink():
        raise SystemExit('Refusing a symlinked bundled directory.')
    existing = {p.relative_to(destination).as_posix() for p in destination.rglob('*') if p.is_file() or p.is_symlink()} if destination.exists() else set()
    changed = sorted(name for name, src in expected.items() if not same(src, destination / name))
    extras = sorted(existing - set(expected))
    if check:
        if changed or extras:
            raise SystemExit('Bundled tool sources differ; run scripts/sync-bundled-tools.py before image build. Missing/changed: ' + ', '.join(changed) + '; extra: ' + ', '.join(extras))
        print('Bundled tool sources match maintained sources.')
        return
    # Only an explicitly generated directory is replaced; never traverse a cache or
    # copy the entire project. Build/distribution artifacts are not part of inputs.
    with tempfile.TemporaryDirectory(prefix='.bundled-stage-', dir=destination.parent) as tmp:
        stage = Path(tmp)
        for name, src in expected.items():
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, target)
        # Keep the old directory until staging is complete. This script is a release
        # operation, not a runtime operation; running Docker builds must not overlap.
        previous = destination.with_name('.bundled-previous')
        if previous.exists():
            raise SystemExit('Previous bundle recovery directory exists; inspect it before retrying.')
        if destination.exists():
            destination.rename(previous)
        try:
            stage.rename(destination)
        except BaseException:
            if previous.exists() and not destination.exists():
                previous.rename(destination)
            raise
        if previous.exists():
            if (previous.is_symlink() or getattr(previous,'is_junction',lambda:False)()
                    or previous.resolve().parent != destination.parent.resolve()
                    or previous.name != '.bundled-previous'):
                raise SystemExit('Refusing cleanup outside the verified generated bundle directory.')
            shutil.rmtree(previous)
    print(f'Staged {len(expected)} maintained tool files into autosourcarr/bundled.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    sync(args.project, check=args.check)
