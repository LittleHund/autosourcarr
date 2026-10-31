"""Bounded regular-file operations under caller-owned source/history roots."""
from contextlib import contextmanager
import json
import os
from pathlib import Path
import stat
import tempfile
import time


def directory(path, *, public=False):
    path=Path(path).absolute()
    for parent in (*reversed(path.parents),path):
        try:info=parent.lstat()
        except FileNotFoundError:
            try:parent.mkdir(mode=0o755 if public else 0o700)
            except FileExistsError:pass
            info=parent.lstat()
        if (not stat.S_ISDIR(info.st_mode) or parent.is_symlink()
                or getattr(parent,'is_junction',lambda:False)()):
            raise ValueError('Source storage contains a linked or non-directory path')
    return path


def read_regular(path, maximum):
    path=Path(path)
    before=path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_nlink!=1 or not 0<=before.st_size<=maximum:
        raise ValueError('Source storage file is linked, special or oversized')
    descriptor=os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
    try:stream=os.fdopen(descriptor,'rb')
    except BaseException:
        os.close(descriptor);raise
    with stream:
        actual=os.fstat(stream.fileno())
        if (not stat.S_ISREG(actual.st_mode) or actual.st_nlink!=1
                or (actual.st_dev,actual.st_ino)!=(before.st_dev,before.st_ino)):
            raise ValueError('Source storage file changed while opening')
        value=stream.read(maximum+1)
    if len(value)>maximum:raise ValueError('Source storage file exceeds its limit')
    return value


def fsync_directory(path):
    if os.name=='nt':return  # Windows fixtures do not claim Linux directory durability.
    descriptor=os.open(path,os.O_RDONLY|getattr(os,'O_DIRECTORY',0)|getattr(os,'O_NOFOLLOW',0))
    try:os.fsync(descriptor)
    finally:os.close(descriptor)


def atomic_write(path, value, *, public=False, immutable=False, executable=False):
    path=Path(path);directory(path.parent,public=public)
    try:existing=read_regular(path,len(value)+1 if immutable else max(len(value)+1,2*1024*1024))
    except FileNotFoundError:existing=None
    if existing is not None:
        if immutable and existing!=value:raise ValueError('Immutable source object has changed')
        if existing==value:return
    descriptor,temporary=tempfile.mkstemp(prefix='.source-',dir=path.parent)
    try:
        try:stream=os.fdopen(descriptor,'wb')
        except BaseException:
            os.close(descriptor);raise
        with stream:
            stream.write(value);stream.flush();os.fsync(stream.fileno())
        os.chmod(temporary,0o755 if executable else 0o644 if public else 0o600)
        # Concurrent immutable writers use the same content-derived name/bytes.
        # A publication never follows an existing target link.
        if immutable and path.exists() and read_regular(path,len(value)+1)!=value:
            raise ValueError('Immutable source object changed during publication')
        os.replace(temporary,path);fsync_directory(path.parent)
    finally:
        Path(temporary).unlink(missing_ok=True)


def json_bytes(value):
    return json.dumps(value,ensure_ascii=True,sort_keys=True,separators=(',',':'),allow_nan=False).encode('utf-8')


@contextmanager
def file_lock(path, timeout=5):
    """A separate regular lock file keeps atomic journal replacement safe."""
    path=Path(path);directory(path.parent)
    descriptor=os.open(path,os.O_RDWR|os.O_CREAT|getattr(os,'O_NOFOLLOW',0),0o600)
    locked=False
    try:
        info=os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_size>64:
            raise ValueError('Unsafe source-history lock file')
        if info.st_size==0:os.write(descriptor,b'0')
        deadline=time.monotonic()+timeout
        while not locked:
            try:
                if os.name=='nt':
                    import msvcrt
                    os.lseek(descriptor,0,os.SEEK_SET);msvcrt.locking(descriptor,msvcrt.LK_NBLCK,1)
                else:
                    import fcntl
                    fcntl.flock(descriptor,fcntl.LOCK_EX|fcntl.LOCK_NB)
                locked=True
            except (BlockingIOError,OSError):
                if time.monotonic()>=deadline:raise ValueError('Source history is busy; refresh before retrying') from None
                time.sleep(.02)
        yield
    finally:
        try:
            if locked:
                if os.name=='nt':
                    import msvcrt
                    os.lseek(descriptor,0,os.SEEK_SET);msvcrt.locking(descriptor,msvcrt.LK_UNLCK,1)
                else:
                    import fcntl
                    fcntl.flock(descriptor,fcntl.LOCK_UN)
        finally:os.close(descriptor)
