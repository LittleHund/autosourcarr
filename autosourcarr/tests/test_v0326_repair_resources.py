"""Resource caps use small fixtures; never stress the host or live app."""
from dataclasses import replace
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT/'repair-worker'))
import resource_budget as module


def test_limits_are_inherited_by_a_real_small_child():
    code = 'import json,resource;print(json.dumps([resource.getrlimit(x) for x in [resource.RLIMIT_AS,resource.RLIMIT_NOFILE,resource.RLIMIT_FSIZE,resource.RLIMIT_NPROC]]))'
    result = subprocess.run([sys.executable, str(PROJECT/'repair-worker/resource_budget.py'), '--',
                             sys.executable, '-c', code], capture_output=True, text=True, timeout=5)
    assert result.returncode == 0, result.stderr
    caps = [module.DEFAULT.address_space, module.DEFAULT.open_files,
            module.DEFAULT.file_bytes, module.DEFAULT.uid_tasks]
    for observed, cap in zip(json.loads(result.stdout), caps):
        assert 0 < observed[0] == observed[1] <= cap


def test_small_file_cap_rejects_growth_without_host_exhaustion(tmp_path):
    destination = tmp_path/'bounded.bin'
    code = f'''import sys,signal,json
sys.path.insert(0,{str(PROJECT/'repair-worker')!r})
from dataclasses import replace
from resource_budget import apply_limits,DEFAULT
signal.signal(signal.SIGXFSZ,signal.SIG_IGN)
apply_limits(replace(DEFAULT,file_bytes=16384))
try:
    with open({str(destination)!r},'wb') as stream: stream.write(b'x'*32768)
except OSError as exc:
    print(json.dumps({{'blocked':True,'errno':exc.errno}}))
'''
    result = subprocess.run([sys.executable, '-c', code], capture_output=True, text=True, timeout=5)
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)['blocked'] is True
    assert destination.stat().st_size <= 16384


def test_jvm_executes_small_allocation_threads_and_crypto_under_virtual_cap(tmp_path):
    java = shutil.which('java')
    javac = shutil.which('javac')
    if not java: pytest.skip('No local JVM available; image JVM still needs native verification')
    source=tmp_path/'BudgetSmoke.java'
    source.write_text('''import java.security.MessageDigest;
import java.util.concurrent.atomic.AtomicInteger;
public class BudgetSmoke {
    public static void main(String[] args) throws Exception {
        byte[] bytes=new byte[32*1024*1024];bytes[0]=77;
        AtomicInteger failures=new AtomicInteger();
        Thread[] threads=new Thread[8];
        for(int i=0;i<threads.length;i++){threads[i]=new Thread(()->{try{if(MessageDigest.getInstance("SHA-256").digest(bytes).length!=32)failures.incrementAndGet();}catch(Exception e){failures.incrementAndGet();}});threads[i].start();}
        for(Thread thread:threads)thread.join();
        if(failures.get()!=0)throw new Error("thread work failed");
        if(MessageDigest.getInstance("SHA-256").digest(bytes).length!=32)throw new Error("digest");
        System.out.print("allocation-and-threads-ok");
    }
}
''')
    launcher=[sys.executable,str(PROJECT/'repair-worker/resource_budget.py'),'--']
    java_options=['-Xmx1536m','-XX:ActiveProcessorCount=2']
    if javac:
        compiled=subprocess.run([*launcher,javac,'-d',str(tmp_path),str(source)],capture_output=True,text=True,timeout=15)
        assert compiled.returncode==0,compiled.stderr
        command=[*launcher,java,*java_options,'-cp',str(tmp_path),'BudgetSmoke']
    else:
        # Java11+ source-file launching exercises the actual compiler/JVM when
        # this environment exposes java but does not install a javac executable.
        command=[*launcher,java,*java_options,str(source)]
    result = subprocess.run(command,
                            capture_output=True,text=True,timeout=15)
    assert result.returncode == 0, result.stderr
    assert result.stdout=='allocation-and-threads-ok'


def test_node_initializes_v8_and_runs_code_under_virtual_address_cap():
    node = shutil.which('node')
    if not node: pytest.skip('No local Node; image Node22/Codex still needs native verification')
    code = 'const crypto=require("node:crypto");const data=Array.from({length:4096},(_,i)=>i*i);if(data.reduce((a,b)=>a+b,0)!==22898104320)process.exit(2);process.stdout.write(crypto.createHash("sha256").update(JSON.stringify(data)).digest("hex"));'
    result = subprocess.run([sys.executable, str(PROJECT/'repair-worker/resource_budget.py'), '--',
                             node, '-e', code], capture_output=True, text=True, timeout=10)
    assert result.returncode == 0, result.stderr
    assert result.stdout == '954478b870908b6924d50762e17fbda3146ccdd130a4caeb915c91f0fa47f11f'


def fake_stat(proc, pid, parent, threads, pages):
    directory = proc/str(pid); directory.mkdir()
    fields = ['0']*22
    fields[0] = 'S'; fields[1] = str(parent); fields[17] = str(threads)
    fields[19] = '12345'; fields[21] = str(pages)
    (directory/'stat').write_text(f'{pid} (fixture with spaces) '+' '.join(fields))
    (directory/'task'/str(pid)).mkdir(parents=True)
    (directory/'task'/str(pid)/'children').write_text('')


def test_watchdog_accounts_owned_descendants_and_threads_only(tmp_path):
    proc = tmp_path/'proc'; proc.mkdir()
    fake_stat(proc, 100, 1, 1, 2)
    fake_stat(proc, 200, 100, 4, 3)
    # Detached sessions remain adopted by the owned PID-namespace initializer.
    fake_stat(proc, 300, 200, 6, 5)
    fake_stat(proc, 999, 1, 1000, 1000000)
    (proc/'100/task/100/children').write_text('200')
    (proc/'200/task/200/children').write_text('300')
    (proc/'999/stat').write_text('unreadable unrelated process must not be parsed')
    assert module.process_usage(100, proc) == (10*os.sysconf('SC_PAGE_SIZE'), 11)


def test_unreadable_owned_child_is_a_resource_check_failure(tmp_path):
    fake_stat(tmp_path, 100, 1, 1, 1)
    fake_stat(tmp_path, 200, 100, 1, 1)
    (tmp_path/'100/task/100/children').write_text('200')
    (tmp_path/'200/stat').write_text('malformed owned process data')
    with pytest.raises(RuntimeError, match='could not be checked'):
        module.process_usage(100, tmp_path)


def test_invisible_running_owner_cannot_silently_disable_accounting(tmp_path):
    watcher=module.Watchdog(123,tmp_path,proc=tmp_path)
    with pytest.raises(RuntimeError,match='not visible'):
        watcher.check()


@pytest.mark.parametrize('memory,tasks,reason', [(101, 1, 'resident-memory'), (1, 3, 'thread')])
def test_watchdog_rejects_owned_memory_and_thread_overruns(tmp_path, monkeypatch, memory, tasks, reason):
    monkeypatch.setattr(module, 'process_usage', lambda *args: (memory, tasks))
    watcher = module.Watchdog(123, tmp_path, replace(module.DEFAULT, resident_bytes=100, job_tasks=2,
                                                   minimum_free_bytes=0))
    with pytest.raises(RuntimeError, match=reason): watcher.check()


def test_workspace_scan_refuses_symlink_escape_and_enforces_small_budget(tmp_path, monkeypatch):
    work = tmp_path/'work'; work.mkdir()
    outside = tmp_path/'outside'; outside.mkdir()
    (outside/'large').write_bytes(b'x'*16384)
    (work/'escape').symlink_to(outside, target_is_directory=True)
    before, _ = module.storage_usage(work, 100)
    (work/'bounded').write_bytes(b'x'*8192)
    after, _ = module.storage_usage(work, 100)
    assert after-before == (work/'bounded').stat().st_blocks*512
    monkeypatch.setattr(module, 'process_usage', lambda *args: (0, 1))
    watcher = module.Watchdog(123, work, replace(module.DEFAULT, workspace_bytes=4096,
                                                minimum_free_bytes=0))
    with pytest.raises(RuntimeError, match='storage budget'): watcher.check()


def test_sandbox_limits_precede_bridge_and_direct_probe(tmp_path):
    from internal_sandbox import Sandbox
    for name in ('code', 'work', 'sockets'): (tmp_path/name).mkdir()
    sandbox = Sandbox(tmp_path/'code', tool_roots=())
    arguments = sandbox.command(tmp_path/'work', ['true'], sockets=tmp_path/'sockets')
    assert arguments.index('/sandbox-tools/resource_budget.py') < arguments.index('/sandbox-tools/sandbox_bridge.py')
    direct = sandbox.command(tmp_path/'work', ['true'], bridge=False)
    assert '/sandbox-tools/resource_budget.py' in direct
    assert '--unshare-net' in direct and '--unshare-pid' in direct
    assert module.diagnostics()['hard_aggregate_memory'] is False


def test_directory_swap_cannot_redirect_workspace_accounting(tmp_path, monkeypatch):
    work=tmp_path/'work';work.mkdir();(work/'swap').mkdir()
    outside=tmp_path/'outside';outside.mkdir();(outside/'retained').write_bytes(b'x'*16384)
    original=module.os.open
    def racing_open(path, flags, *args, **kwargs):
        if path == 'swap' and kwargs.get('dir_fd') is not None:
            (work/'swap').rmdir();(work/'swap').symlink_to(outside, target_is_directory=True)
        return original(path, flags, *args, **kwargs)
    monkeypatch.setattr(module.os, 'open', racing_open)
    with pytest.raises(OSError): module.storage_usage(work, 100)
    assert (outside/'retained').read_bytes()==b'x'*16384


def test_resource_overrun_stops_and_drains_controlled_owned_children(tmp_path, monkeypatch):
    import internal_runner
    import time
    marker=tmp_path/'child-pid';stopped=tmp_path/'child-stopped'
    child_code=f'''import signal,time
from pathlib import Path
def stop(*args):
    Path({str(stopped)!r}).write_text('stopped')
    raise SystemExit(0)
signal.signal(signal.SIGTERM,stop)
while True: time.sleep(.05)
'''
    fake_bwrap=tmp_path/'bwrap'
    fake_bwrap.write_text(f'''#!{sys.executable}
import os,signal,subprocess,sys,time
from pathlib import Path
fd=int(sys.argv[2])
child=None
def stop(*args):
    if child is not None:
        child.terminate()
        child.wait(timeout=3)
    raise SystemExit(0)
signal.signal(signal.SIGTERM,stop)
child=subprocess.Popen([sys.executable,'-c',{child_code!r}],pass_fds=(fd,))
time.sleep(.1)
Path({str(marker)!r}).write_text(str(child.pid))
while True: time.sleep(.05)
''')
    fake_bwrap.chmod(0o755)
    class Overrun:
        def __init__(self, *args): pass
        def check(self):
            if marker.exists(): raise RuntimeError('Repair exceeded its sampled resident-memory budget')
    monkeypatch.setattr(internal_runner, 'Watchdog', Overrun)
    runner=internal_runner.InternalRunner(tmp_path/'repair')
    job={'id':'a'*32};runner.path(job['id']).mkdir()
    try:
        with pytest.raises(RuntimeError, match='memory budget'):
            runner.command(job, [str(fake_bwrap)], timeout=5)
        assert stopped.read_text()=='stopped'
        assert runner.cleanup_failed is False
    finally:
        runner.socket_temp.cleanup()
