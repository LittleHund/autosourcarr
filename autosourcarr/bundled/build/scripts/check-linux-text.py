#!/usr/bin/env python3
"""Reject shell/Docker text that will change meaning when executed on Linux.

Run before packaging and against the ZIP itself. The image also runs this guard
on its copied startup/bundled scripts, without changing config or runtime data.
"""
from __future__ import annotations
import argparse
from pathlib import Path,PurePosixPath
import sys
import zipfile

SKIP={'config','data','.git','.venv','venv','node_modules','__pycache__','.pytest_cache','.work'}


def linux_text(name):
    path=PurePosixPath(str(name).replace('\\','/'))
    return not SKIP.intersection(path.parts) and (path.suffix=='.sh' or path.name=='Dockerfile' or path.name.startswith('Dockerfile.'))


def validate_bytes(name,data):
    if data.startswith(b'\xef\xbb\xbf'):raise ValueError(f'{name}: UTF-8 BOM before the interpreter; save as UTF-8 without BOM')
    if b'\r' in data:raise ValueError(f'{name}: CR/CRLF bytes; Linux scripts require LF line endings')
    try:data.decode('utf-8')
    except UnicodeDecodeError as exc:raise ValueError(f'{name}: invalid UTF-8 text') from exc
    if not data or not data.endswith(b'\n'):raise ValueError(f'{name}: missing final LF newline')
    if str(name).endswith('.sh') and not data.startswith(b'#!'):raise ValueError(f'{name}: missing shell interpreter line')


def check_paths(paths):
    checked=[];errors=[]
    for root in paths:
        root=Path(root)
        if not root.exists():errors.append(f'{root}: missing input');continue
        candidates=sorted(root.rglob('*')) if root.is_dir() else [root]
        for path in candidates:
            name=path.relative_to(root).as_posix() if root.is_dir() else path.name
            if not path.is_file() or not linux_text(name):continue
            try:
                if path.is_symlink():raise ValueError(f'{name}: expected an ordinary maintained file')
                validate_bytes(name,path.read_bytes());checked.append(str(path))
            except (ValueError,OSError) as exc:errors.append(str(exc))
    if errors:raise ValueError('\n'.join(errors))
    if not checked:raise ValueError('No shell/Docker build files were checked')
    return checked


def check_archive(path):
    checked=[];errors=[]
    with zipfile.ZipFile(path) as archive:
        for item in archive.infolist():
            if item.is_dir() or not linux_text(item.filename):continue
            try:validate_bytes(item.filename,archive.read(item));checked.append(item.filename)
            except (ValueError,OSError) as exc:errors.append(str(exc))
    if errors:raise ValueError('\n'.join(errors))
    if not checked:raise ValueError('No shell/Docker build files were checked in the archive')
    return checked


def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('paths',nargs='*',type=Path)
    parser.add_argument('--archive',type=Path)
    args=parser.parse_args(argv)
    try:
        if args.archive and args.paths:raise ValueError('Use either source paths or --archive')
        checked=check_archive(args.archive) if args.archive else check_paths(args.paths or [Path(__file__).resolve().parents[1]])
    except (ValueError,OSError,zipfile.BadZipFile) as exc:
        print('Linux text validation failed:\n'+str(exc),file=sys.stderr);return 1
    print(f'Linux text OK: {len(checked)} shell/Docker files; UTF-8, LF, no BOM')
    return 0


if __name__=='__main__':raise SystemExit(main())
