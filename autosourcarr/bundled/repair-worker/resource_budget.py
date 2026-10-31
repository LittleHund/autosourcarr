"""Hard inherited process limits and a controller-owned repair watchdog.

The launcher runs inside the existing PID/network/filesystem sandbox. The sampled
watchdog runs outside it, where repair code cannot stop it. Neither changes the
host/container policy or needs writable cgroups. Aggregate limits are sampled;
they are not a hard cgroup reservation and can overshoot between checks.
"""
from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import resource
import stat
import sys
import time


MIB = 1024 * 1024
GIB = 1024 * MIB


@dataclass(frozen=True)
class Budget:
    # JVM/Node reserve substantially more virtual address space than their heaps.
    address_space: int = 8 * GIB
    open_files: int = 1024
    file_bytes: int = 512 * MIB
    uid_tasks: int = 1024
    resident_bytes: int = 4 * GIB
    job_tasks: int = 256
    workspace_bytes: int = 8 * GIB
    workspace_files: int = 200000
    minimum_free_bytes: int = 512 * MIB


DEFAULT = Budget()


def diagnostics(budget=DEFAULT):
    return {
        'mechanism': 'inherited per-process rlimits and controller watchdog',
        'hard_per_process': {'virtual_memory_bytes': budget.address_space,
                             'open_files': budget.open_files, 'file_bytes': budget.file_bytes},
        'hard_per_uid_tasks': budget.uid_tasks,
        'sampled_job_limits': {'resident_memory_bytes': budget.resident_bytes,
                               'tasks_including_threads': budget.job_tasks,
                               'workspace_bytes': budget.workspace_bytes,
                               'workspace_files': budget.workspace_files,
                               'minimum_free_bytes': budget.minimum_free_bytes},
        'sampling_seconds': {'processes': 0.2, 'workspace': 2.0},
        'hard_aggregate_memory': False,
        'note': 'Aggregate limits can overshoot between samples. The process-count rlimit shares the runtime UID; existing host limits are never increased.',
    }


def apply_limits(budget=DEFAULT, *, trusted_account=False):
    """Keep host ceilings; only fixed account commands retain the UID task limit.

    Linux NPROC counts threads sharing the real UID, including other workloads.
    Imposing the repair UID ceiling on login can prevent its very first fork.
    Source jobs keep the default hard ceiling and their external watchdog.
    """
    if type(trusted_account) is not bool:
        raise ValueError('Invalid process budget mode')
    limits = ((resource.RLIMIT_AS, budget.address_space),
              (resource.RLIMIT_NOFILE, budget.open_files),
              (resource.RLIMIT_FSIZE, budget.file_bytes))
    if not trusted_account:
        limits += ((resource.RLIMIT_NPROC, budget.uid_tasks),)
    effective = {}
    for name, requested in limits:
        soft, hard = resource.getrlimit(name)
        limit = requested if hard == resource.RLIM_INFINITY else min(requested, hard)
        if soft != resource.RLIM_INFINITY:
            limit = min(limit, soft)
        resource.setrlimit(name, (limit, limit))
        effective[str(name)] = limit
    return effective


def process_usage(pid, proc=Path('/proc')):
    """Account only the monitor's descendants, including reparented namespace jobs."""
    memory = tasks = 0
    visited = set()
    pending = [int(pid)]
    page_size = os.sysconf('SC_PAGE_SIZE')
    while pending:
        child = pending.pop()
        if child in visited:
            continue
        visited.add(child)
        path = proc / str(child)
        try:
            fields = (path/'stat').read_text().rsplit(')', 1)[1].split()
            tasks += int(fields[17])
            memory += max(0, int(fields[21])) * page_size
            # Every thread can fork. Traverse kernel-owned child lists instead
            # of reading unrelated processes whose proc entries may be private.
            for thread in (path/'task').iterdir():
                try:
                    pending.extend(int(value) for value in (thread/'children').read_text().split())
                except FileNotFoundError:
                    continue
        except FileNotFoundError:
            continue
        except (OSError, ValueError, IndexError) as exc:
            raise RuntimeError('Repair process resource usage could not be checked') from exc
    return memory, tasks


def storage_usage(root, max_files):
    """Count allocated bytes without following job-created symlinks or races."""
    total = count = 0
    deadline = time.monotonic() + 2.0
    stack = []
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    stack.append((descriptor, os.scandir(descriptor)))
    try:
        while stack:
            if time.monotonic() >= deadline:
                raise RuntimeError('Repair workspace resource check exceeded its time budget; live source unchanged')
            descriptor, entries = stack[-1]
            try:
                entry = next(entries)
            except StopIteration:
                entries.close(); os.close(descriptor); stack.pop(); continue
            try:
                item = entry.stat(follow_symlinks=False)
            except FileNotFoundError:
                continue
            count += 1
            if count > max_files:
                raise RuntimeError('Repair exceeded its workspace file-count budget; live source unchanged')
            total += item.st_blocks * 512
            if stat.S_ISDIR(item.st_mode):
                if len(stack) >= 64:
                    raise RuntimeError('Repair exceeded its workspace directory-depth budget; live source unchanged')
                try:
                    child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=descriptor)
                except FileNotFoundError:
                    continue
                stack.append((child, os.scandir(child)))
        return total, count
    finally:
        for descriptor, entries in stack:
            entries.close(); os.close(descriptor)


class Watchdog:
    def __init__(self, pid, storage, budget=DEFAULT, *, proc=Path('/proc')):
        self.pid = int(pid)
        self.storage = Path(storage)
        self.budget = budget
        self.proc = proc
        self.next_storage_check = 0.0

    def check(self):
        memory, tasks = process_usage(self.pid, self.proc)
        if tasks <= 0:
            raise RuntimeError('Owned repair process is not visible to resource accounting')
        if memory > self.budget.resident_bytes:
            raise RuntimeError('Repair exceeded its sampled resident-memory budget; live source unchanged')
        if tasks > self.budget.job_tasks:
            raise RuntimeError('Repair exceeded its process/thread budget; live source unchanged')
        usage = os.statvfs(self.storage)
        if usage.f_bavail * usage.f_frsize < self.budget.minimum_free_bytes:
            raise RuntimeError('Repair stopped to retain free workspace disk space; live source unchanged')
        now = time.monotonic()
        if now >= self.next_storage_check:
            allocated, _ = storage_usage(self.storage, self.budget.workspace_files)
            if allocated > self.budget.workspace_bytes:
                raise RuntimeError('Repair exceeded its workspace storage budget; live source unchanged')
            self.next_storage_check = now + 2.0


def main(argv=None):
    command = list(sys.argv[1:] if argv is None else argv)
    trusted_account = command[:1] == ['--trusted-account']
    if trusted_account:
        command.pop(0)
        if command[:1] != ['--']:
            raise ValueError('Trusted account mode requires a command separator')
    if command[:1] == ['--']:
        command.pop(0)
    if not command:
        raise ValueError('A limited command is required')
    apply_limits(trusted_account=trusted_account)
    os.execvpe(command[0], command, os.environ)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as exc:
        print('Repair process resource limits could not be applied; no repair command was started.', file=sys.stderr)
        raise SystemExit(70) from exc
