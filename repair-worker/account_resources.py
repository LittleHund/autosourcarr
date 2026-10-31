"""Read-only, bounded launch evidence; never expose kernel paths or alter limits."""
import os
from pathlib import PurePosixPath
import re
import stat

MAX_UINT64 = (1 << 64) - 1
NUMERIC_FIELDS = frozenset((
    'nproc_soft', 'nproc_hard', 'address_space_soft_bytes', 'address_space_hard_bytes',
    'open_files_soft', 'open_files_hard', 'cgroup_pids_current', 'cgroup_pids_max',
    'cgroup_memory_current_bytes', 'cgroup_memory_max_bytes',
))
LIMIT_FIELDS = NUMERIC_FIELDS - {'cgroup_pids_current', 'cgroup_memory_current_bytes'}
FIELDS = NUMERIC_FIELDS | {'cgroup_scope', 'ancestor_limits'}


def numeric_string(value):
    return (type(value) is str and re.fullmatch(r'(?:0|[1-9][0-9]{0,19})', value) is not None
            and int(value) <= MAX_UINT64)


def validate_resources(value):
    if type(value) is not dict or value.keys() - FIELDS:
        raise ValueError('Invalid account resource evidence')
    for key, item in value.items():
        if type(item) is not str:
            raise ValueError('Invalid account resource evidence')
        if key == 'cgroup_scope':
            valid = type(item) is str and item in {'unknown', 'visible-v2-only'}
        elif key == 'ancestor_limits':
            valid = item == 'unknown'
        else:
            valid = numeric_string(item) or item == 'unknown' or key in LIMIT_FIELDS and item == 'unlimited'
        if not valid:
            raise ValueError('Invalid account resource evidence')
    return dict(value)


def _read_limited(path, limit):
    # These are fixed proc/cgroup files, not application or user-selected paths.
    # Unsupported platforms/layouts remain unknown rather than following a link.
    nofollow = getattr(os, 'O_NOFOLLOW', None)
    if nofollow is None:
        return None
    descriptor = None
    try:
        descriptor = os.open(path, os.O_RDONLY | nofollow | os.O_NONBLOCK | os.O_CLOEXEC)
        stream = os.fdopen(descriptor, 'rb')
        descriptor = None  # the stream now owns it
        with stream:
            if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                return None
            data = stream.read(limit + 1)
        return data.decode('utf-8') if len(data) <= limit else None
    except (OSError, UnicodeError):
        return None
    finally:
        if descriptor is not None:
            try:
                os.close(descriptor)
            except OSError:
                pass


def _ordinary_path(value):
    return (value.startswith('/') and len(value) <= 4096
            and not any(part in {'.', '..'} for part in value.split('/'))
            and re.fullmatch(r'/[A-Za-z0-9._:/-]*', value) is not None)


def cgroup_location(membership, mounts):
    """Resolve only a verified common cgroup-v2 mount/member relationship.

    Escaped/unusual layouts and ambiguity are deliberately unsupported. Never
    substitute an unrelated mount root and describe its counters as this group.
    """
    if not isinstance(membership, str) or not isinstance(mounts, str):
        return None
    if len(membership) > 4096 or len(mounts) > 65536:
        return None
    members = [line[3:] for line in membership.splitlines() if line.startswith('0::')]
    if len(members) != 1 or not _ordinary_path(members[0]):
        return None
    member = PurePosixPath(members[0])
    candidates = set()
    for line in mounts.splitlines():
        before, separator, after = line.partition(' - ')
        left, right = before.split(), after.split()
        if not separator or len(left) < 6 or len(right) < 3 or right[0] != 'cgroup2':
            continue
        root, point = left[3:5]
        if point != '/sys/fs/cgroup' or not _ordinary_path(root):
            continue
        try:
            relative = member.relative_to(PurePosixPath(root))
        except ValueError:
            continue
        candidates.add(str(PurePosixPath(point) / relative))
    return candidates.pop() if len(candidates) == 1 else None


def _limit_value(value, infinity):
    if type(value) is int:
        if value == infinity:
            return 'unlimited'
        if 0 <= value <= MAX_UINT64:
            return str(value)
    return 'unknown'


def snapshot():
    """Best-effort supervisor limits and visible counters, not a quota diagnosis."""
    result = {key: 'unknown' for key in FIELDS}
    try:
        import resource
    except ImportError:
        resource = None
    if resource is not None:
        for name, soft_key, hard_key in (
            ('RLIMIT_NPROC', 'nproc_soft', 'nproc_hard'),
            ('RLIMIT_AS', 'address_space_soft_bytes', 'address_space_hard_bytes'),
            ('RLIMIT_NOFILE', 'open_files_soft', 'open_files_hard'),
        ):
            try:
                soft, hard = resource.getrlimit(getattr(resource, name))
                result[soft_key] = _limit_value(soft, resource.RLIM_INFINITY)
                result[hard_key] = _limit_value(hard, resource.RLIM_INFINITY)
            except (OSError, ValueError, TypeError, AttributeError):
                continue
    membership = _read_limited('/proc/self/cgroup', 4096)
    mounts = _read_limited('/proc/self/mountinfo', 65536)
    location = cgroup_location(membership, mounts)
    if location is not None:
        values = {}
        for key, filename in (
            ('cgroup_pids_current', 'pids.current'), ('cgroup_pids_max', 'pids.max'),
            ('cgroup_memory_current_bytes', 'memory.current'), ('cgroup_memory_max_bytes', 'memory.max'),
        ):
            raw = _read_limited(location + '/' + filename, 64)
            value = raw.strip() if raw is not None else ''
            values[key] = ('unlimited' if value == 'max' and key in LIMIT_FIELDS
                           else value if numeric_string(value) else 'unknown')
        # A concurrent move makes the multi-file observation ambiguous.
        if _read_limited('/proc/self/cgroup', 4096) == membership:
            result.update(values)
            if any(value != 'unknown' for value in values.values()):
                result['cgroup_scope'] = 'visible-v2-only'
    # Visible current/max can omit ancestor ceilings and change immediately.
    # Keep exact decimal strings across JSON/JS, including values above2**53.
    return validate_resources(result)
