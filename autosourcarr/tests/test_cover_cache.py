import asyncio
import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)

from app.cover_cache import CoverCache


def test_cover_cache_can_clear_selected_and_all_files(tmp_path):
    cache = CoverCache()
    cache.root = Path(tmp_path)
    cache.root.mkdir(parents=True, exist_ok=True)
    cache.write("entry-1", b"one", "image/jpeg")
    cache.write("source-2", b"two", "image/png")
    assert cache.read("entry-1") is not None
    removed = asyncio.run(cache.clear_keys(["entry-1"]))
    assert removed >= 1
    assert cache.read("entry-1") is None
    assert cache.read("source-2") is not None
    removed_all = asyncio.run(cache.clear_all())
    assert removed_all >= 1
    assert cache.read("source-2") is None


def test_cover_cache_is_cached_probes_without_reading_image_bytes(tmp_path):
    cache = CoverCache()
    cache.root = Path(tmp_path)
    cache.root.mkdir(parents=True, exist_ok=True)
    assert cache.is_cached("entry-42") is False
    cache.write("entry-42", b"cached-cover", "image/jpeg")
    assert cache.is_cached("entry-42") is True
    asyncio.run(cache.clear_keys(["entry-42"]))
    assert cache.is_cached("entry-42") is False
