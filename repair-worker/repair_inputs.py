"""Export only immutable job input packages, never account/profile/job text files."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import zipfile

MAX_PACKAGE=32*1024*1024


def _read(root, name, expected):
    if not re.fullmatch(r'[0-9a-f]{64}',str(expected or '')):
        raise ValueError('Saved comparison input hash is unavailable')
    root=Path(root)
    if root.is_symlink() or root.is_junction():raise ValueError('Comparison input directory is linked')
    path=root/name;before=path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_nlink!=1 or not 100<before.st_size<=MAX_PACKAGE:
        raise ValueError('Comparison input is missing, linked or oversized')
    descriptor=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
    try:stream=os.fdopen(descriptor,'rb')
    except BaseException:
        os.close(descriptor);raise
    with stream:
        actual=os.fstat(stream.fileno())
        if not stat.S_ISREG(actual.st_mode) or actual.st_nlink!=1 or (before.st_dev,before.st_ino)!=(actual.st_dev,actual.st_ino):
            raise ValueError('Comparison input changed while opening')
        data=stream.read(MAX_PACKAGE+1)
    if len(data)>MAX_PACKAGE or hashlib.sha256(data).hexdigest()!=expected:
        raise ValueError('Comparison input changed; download refused')
    return data


def input_archive(root, job):
    baseline=_read(root,'baseline.jar',job.get('baseline_sha256'))
    apk=_read(root,'source.apk',job.get('source_apk_sha256'))
    report={key:job[key] for key in ('id','pkg_name','version','baseline_sha256','source_apk_sha256') if key in job}
    report['scope']='Exact saved extension package bytes, shared privately for code analysis. No separate account, cookie, settings, library, prompt or conversation files are included; this package archive is unredacted.'
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('baseline.jar',baseline)
        archive.writestr('source.apk',apk)
        archive.writestr('input-hashes.json',json.dumps(report,sort_keys=True).encode())
    value=output.getvalue()
    if len(value)>MAX_PACKAGE:raise ValueError('Comparison download exceeds the bounded 32 MiB export limit')
    return value
