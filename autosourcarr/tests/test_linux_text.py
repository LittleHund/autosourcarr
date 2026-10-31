"""Real deployment bytes, not universal-newline reads or syntax-only checks."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile
import pytest

PROJECT=Path(__file__).resolve().parents[2]
TOOL=PROJECT/'scripts/check-linux-text.py'
spec=importlib.util.spec_from_file_location('linux_text_guard',TOOL)
guard=importlib.util.module_from_spec(spec);spec.loader.exec_module(guard)


def test_project_linux_text_is_deployable():
    checked=guard.check_paths([PROJECT])
    assert str(PROJECT/'autosourcarr/start-truenas.sh') in checked


@pytest.mark.parametrize('data,message',[(b'#!/bin/sh\r\nset -eu\r\n','CR/CRLF'),
    (b'\xef\xbb\xbf#!/bin/sh\nset -eu\n','BOM'),(b'#!/bin/sh\n#\xff\n','UTF-8'),
    (b'#!/bin/sh\nset -eu','final LF'),(b'set -eu\n','interpreter')])
def test_invalid_script_bytes_are_rejected(data,message):
    with pytest.raises(ValueError,match=message):guard.validate_bytes('startup.sh',data)


def test_valid_posix_and_bash_interpreters_are_retained():
    for content in (b'#!/bin/sh\nset -eu\n',b'#!/usr/bin/env bash\nset -euo pipefail\nitems=(one two)\n'):
        guard.validate_bytes('fixture.sh',content)


def test_cli_reports_bad_file_without_rewriting_it(tmp_path):
    script=tmp_path/'startup.sh';content=b'#!/bin/sh\r\nset -eu\r\n';script.write_bytes(content)
    result=subprocess.run([sys.executable,str(TOOL),str(script)],capture_output=True,text=True,timeout=10)
    assert result.returncode==1 and 'CR/CRLF' in result.stderr and 'startup.sh' in result.stderr
    assert script.read_bytes()==content


def test_archive_guard_reads_exact_member_bytes(tmp_path):
    path=tmp_path/'fixture.zip'
    with zipfile.ZipFile(path,'w') as archive:
        archive.writestr('project/autosourcarr/start-truenas.sh',b'#!/bin/sh\r\nset -eu\r\n')
        archive.writestr('project/config/private.env',b'NOT_A_SCRIPT=private\r\n')
    with pytest.raises(ValueError,match='CR/CRLF'):guard.check_archive(path)
    with zipfile.ZipFile(path,'w') as archive:
        archive.writestr('project/autosourcarr/start-truenas.sh',b'#!/bin/sh\nset -eu\n')
        archive.writestr('project/config/private.env',b'NOT_A_SCRIPT=private\r\n')
    assert guard.check_archive(path)==['project/autosourcarr/start-truenas.sh']


def test_image_guard_checks_both_startup_and_bundle(tmp_path):
    startup=tmp_path/'start-truenas.sh';startup.write_bytes(b'#!/bin/sh\nset -eu\n')
    bundle=tmp_path/'bundled';bundle.mkdir();tool=bundle/'builder.sh';tool.write_bytes(b'#!/bin/bash\r\nset -eu\r\n')
    result=subprocess.run([sys.executable,str(TOOL),str(startup),str(bundle)],capture_output=True,text=True,timeout=10)
    assert result.returncode==1 and 'builder.sh' in result.stderr
    tool.write_bytes(b'#!/bin/bash\nset -eu\n')
    assert subprocess.run([sys.executable,str(TOOL),str(startup),str(bundle)],capture_output=True,timeout=10).returncode==0


@pytest.mark.skipif(os.name=='nt',reason='Windows/MSYS may translate/accept CR; this check requires the native POSIX shell')
def test_native_posix_shell_rejects_reported_bytes_and_accepts_lf():
    shell=shutil.which('sh')
    if not shell:pytest.skip('No native POSIX sh')
    prefix=b'#!/bin/sh\r\nset -eu\r\nprintf "startup prefix OK\\n"\r\n'
    bad=subprocess.run([shell,'-s'],input=prefix,capture_output=True,timeout=10)
    assert bad.returncode!=0
    good=subprocess.run([shell,'-s'],input=prefix.replace(b'\r\n',b'\n'),capture_output=True,timeout=10)
    assert good.returncode==0 and good.stdout==b'startup prefix OK\n'
