import asyncio
import os
import threading
from pathlib import Path

from app.cover_cache import CoverCache
from app.config import settings


def cache(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "cover_cache_ttl_seconds", 60)
    return CoverCache()


def test_repeated_card_hints_avoid_file_stats_but_image_reads_still_validate(tmp_path, monkeypatch):
    c = cache(tmp_path, monkeypatch)
    c.write("entry-1", b"fixture", "image/png")
    real_stat = Path.stat
    calls = []
    def counted(path, *a, **kw):
        if path.suffix == ".img": calls.append(path)
        return real_stat(path, *a, **kw)
    monkeypatch.setattr(Path, "stat", counted)
    for _ in range(50):
        assert c.is_cached("entry-1")
    assert calls == []
    assert c.read("entry-1") == (b"fixture", "image/png")
    assert len(calls) == 1


def test_write_clear_and_expiry_update_hints(tmp_path, monkeypatch):
    c = cache(tmp_path, monkeypatch)
    assert not c.is_cached("entry-1")
    c.write("entry-1", b"fixture", "image/png")
    assert c.is_cached("entry-1")
    c.clear_keys_sync(["entry-1"])
    assert not c.is_cached("entry-1") and c.read("entry-1") is None
    c.write("entry-1", b"fixture", "image/png")
    asyncio.run(c.clear_all())
    assert not c.is_cached("entry-1")


def test_hint_never_outlives_remaining_image_ttl(tmp_path, monkeypatch):
    import time
    c = cache(tmp_path, monkeypatch)
    c.write("entry-1", b"fixture", "image/png")
    path, _ = c._paths("entry-1")
    os.utime(path, (time.time() - 59, time.time() - 59))
    c._presence.clear()
    assert c.is_cached("entry-1")
    expires, _, _ = c._presence[(str(c.root), "entry-1")]
    assert expires - time.monotonic() <= 1


def test_late_stat_cannot_restore_a_cleared_hint(tmp_path, monkeypatch):
    c = cache(tmp_path, monkeypatch)
    c.write("entry-1", b"fixture", "image/png")
    c._presence.clear()
    path, _ = c._paths("entry-1")
    old = path.stat()
    entered, release = threading.Event(), threading.Event()
    real_stat = Path.stat
    def blocked(file, *a, **kw):
        if file == path:
            entered.set()
            assert release.wait(2)
            return old
        return real_stat(file, *a, **kw)
    monkeypatch.setattr(Path, "stat", blocked)
    results = []
    task = threading.Thread(target=lambda: results.append(c.is_cached("entry-1")))
    task.start()
    try:
        assert entered.wait(1)
        c.clear_keys_sync(["entry-1"])
    finally:
        release.set(); task.join(2)
    assert results == [False] and not c.is_cached("entry-1")


def test_presence_cache_is_bounded_and_root_specific(tmp_path, monkeypatch):
    c = cache(tmp_path, monkeypatch)
    for n in range(4200): c._remember_presence(str(n), True, 30)
    assert len(c._presence) == 4096
    c.root = tmp_path / "new-root"
    assert not c.is_cached("4199")


def test_shorter_runtime_ttl_revalidates_cached_hint(tmp_path, monkeypatch):
    import time
    c = cache(tmp_path, monkeypatch)
    c.write("entry-1", b"fixture", "image/png")
    path, _ = c._paths("entry-1")
    os.utime(path, (time.time() - 30, time.time() - 30))
    assert c.is_cached("entry-1")
    monkeypatch.setattr(settings, "cover_cache_ttl_seconds", 10)
    assert not c.is_cached("entry-1") and c.read("entry-1") is None


def test_stat_admitted_during_delete_cannot_restore_removed_file(tmp_path, monkeypatch):
    c = cache(tmp_path, monkeypatch)
    c.write("entry-1", b"fixture", "image/png")
    path, _ = c._paths("entry-1")
    old = path.stat()
    delete_entered, allow_delete = threading.Event(), threading.Event()
    stat_entered, allow_stat = threading.Event(), threading.Event()
    real_clear, real_stat = c._clear_keys_sync, Path.stat
    def deleting(keys):
        delete_entered.set()
        assert allow_delete.wait(3)
        return real_clear(keys)
    def stat(file, *a, **kw):
        if file == path:
            stat_entered.set()
            assert allow_stat.wait(3)
            return old
        return real_stat(file, *a, **kw)
    monkeypatch.setattr(c, "_clear_keys_sync", deleting)
    monkeypatch.setattr(Path, "stat", stat)
    results = []
    clear = threading.Thread(target=lambda: c.clear_keys_sync(["entry-1"]))
    probe = threading.Thread(target=lambda: results.append(c.is_cached("entry-1")))
    clear.start()
    try:
        assert delete_entered.wait(1)
        probe.start()
        assert stat_entered.wait(1)
        allow_delete.set(); clear.join(2)
        assert c._presence[(str(c.root), "entry-1")][1] is False
        allow_stat.set(); probe.join(2)
        assert results == [False] and not c.is_cached("entry-1")
    finally:
        allow_delete.set(); allow_stat.set()
        clear.join(2)
        if probe.ident: probe.join(2)
