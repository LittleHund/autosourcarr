from __future__ import annotations

import ast
from pathlib import Path

import pytest

from app.suwayomi import SuwayomiClient, SuwayomiError


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def test_v075_release_history_is_present():
    r = root()
    current = tuple(int(x) for x in (r / "VERSION").read_text().strip().split("."))
    assert current >= (0, 2, 75)
    changelog = (r / "CHANGELOG.md").read_text()
    assert "## 0.2.75 - 2026-08-29" in changelog
    assert "0.2.75 → 1.4.2075" in changelog

@pytest.mark.asyncio
async def test_search_http_404_is_empty_result_not_source_failure(monkeypatch):
    client = SuwayomiClient("http://example.invalid")

    async def fake_graphql(*args, **kwargs):
        raise SuwayomiError("Dogul Wang: Exception while fetching data (/fetchSourceManga) : HTTP error 404")

    monkeypatch.setattr(client, "graphql", fake_graphql)
    try:
        payload = await client.fetch_source_manga("123", "SEARCH", 1, "Dogul Wang")
    finally:
        await client.close()
    assert payload["mangas"] == []
    assert payload["hasNextPage"] is False
    assert payload["searchMiss"] is True


@pytest.mark.asyncio
async def test_non_search_404_still_surfaces_as_source_error(monkeypatch):
    client = SuwayomiClient("http://example.invalid")

    async def fake_graphql(*args, **kwargs):
        raise SuwayomiError("Exception while fetching data (/fetchSourceManga) : HTTP error 404")

    monkeypatch.setattr(client, "graphql", fake_graphql)
    try:
        with pytest.raises(SuwayomiError):
            await client.fetch_source_manga("123", "POPULAR", 1, "")
    finally:
        await client.close()


def test_v075_python_files_parse():
    ast.parse((root() / "autosourcarr/app/suwayomi.py").read_text())
    ast.parse((root() / "autosourcarr/app/service.py").read_text())
