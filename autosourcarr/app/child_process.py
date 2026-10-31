"""Linux helper supervisor: reap detached browser/Gradle descendants too.

Each wrapper becomes a subreaper for its own process tree. It never selects by
executable name or UID, and cannot terminate another AutoSourcarr helper's work.
"""
import ctypes
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def children(root):
    rows={}
    for entry in Path('/proc').iterdir():
        if not entry.name.isdigit(): continue
        try:
            fields=(entry/'stat').read_text().rsplit(')',1)[1].split()
            rows[int(entry.name)]=(int(fields[1]),fields[19]) # parent and starttime
        except (OSError,ValueError,IndexError): continue
    found={};parents={root}
    while parents:
        next_parents=set()
        for pid,(parent,start) in rows.items():
            if parent in parents and pid not in found: found[pid]=start;next_parents.add(pid)
        parents=next_parents
    return found


def signal_children(signum):
    for pid,start in children(os.getpid()).items():
        handle=None
        try:
            # Bind identity first; PID recycling cannot redirect the later signal.
            handle=os.pidfd_open(pid)
            current=Path(f'/proc/{pid}/stat').read_text().rsplit(')',1)[1].split()[19]
            if current==start: signal.pidfd_send_signal(handle,signum)
        except (OSError,ValueError,IndexError): pass
        finally:
            if handle is not None:os.close(handle)


def main(args):
    if not args: return 64
    libc=ctypes.CDLL(None,use_errno=True)
    parent=os.getppid()
    if libc.prctl(36,1,0,0,0) or libc.prctl(1,signal.SIGTERM,0,0,0):
        print('Internal process supervision could not initialize.',file=sys.stderr);return 70
    stopping=False
    def stop(*_):
        nonlocal stopping
        stopping=True
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    if os.getppid()!=parent: return 70
    try:
        witness=os.pidfd_open(os.getpid())
        try:signal.pidfd_send_signal(witness,0)
        finally:os.close(witness)
    except OSError:
        print('Internal process identity checks are unavailable.',file=sys.stderr);return 70
    process=subprocess.Popen(args)
    try:
        while process.poll() is None and not stopping: time.sleep(.1)
        code=process.returncode if process.returncode is not None else 143
    finally:
        deadline=time.monotonic()+6
        signal_children(signal.SIGTERM)
        while time.monotonic()<deadline:
            process.poll()
            try:
                while os.waitpid(-1,os.WNOHANG)[0]: pass
            except ChildProcessError: break
            if not children(os.getpid()): break
            if time.monotonic()>deadline-3: signal_children(signal.SIGKILL)
            time.sleep(.05)
        # A process can fork while its parent is being signalled. Keep scanning
        # adopted descendants rather than blocking forever on an unsignalled child.
        hard_deadline=time.monotonic()+1
        while True:
            signal_children(signal.SIGKILL)
            process.poll()
            try:
                while os.waitpid(-1,os.WNOHANG)[0]: pass
            except ChildProcessError: break
            if time.monotonic()>=hard_deadline:
                print('Internal descendant cleanup could not be confirmed.',file=sys.stderr)
                return 75
            time.sleep(.05)
    return code if code>=0 else 128-code


if __name__=='__main__': raise SystemExit(main(sys.argv[1:]))
