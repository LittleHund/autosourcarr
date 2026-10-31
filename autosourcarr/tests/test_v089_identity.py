"""Exact-title import shortcuts must obey the normal edition safeguards."""
import asyncio

import pytest

from test_v087_recovery import env, manga


def setup_exact_entry(s):
    data = {**manga(99201, "An Example Adventure"), "genre": ["Manga"]}
    eid = s._create_entry(data, "seed")
    s._upsert_match(eid, "seed", data, 1.0, "known")
    return eid, {
        "title": data["title"], "genre": ["Webtoon"], "source": 0,
        "author": data["author"], "description": data["description"],
    }


def resolve(s, backup, eid, path):
    if path == "resolver":
        result = asyncio.run(s.resolve_backup_manga_entry_details(backup, use_metadata=False))
        return result["entry_id"], result["reason"]
    if path == "legacy-existing":
        return s._backup_existing_entry(backup, {eid})
    return s._backup_existing_from_index(backup, s._backup_existing_index({eid}))


@pytest.mark.parametrize("path", ["resolver", "existing-index", "legacy-existing"])
@pytest.mark.parametrize("backup_media,accepted", [("Webtoon", False), ("Manga", True), ("", True)])
def test_exact_title_respects_explicit_media_conflict(env, path, backup_media, accepted):
    s, _ = env
    eid, backup = setup_exact_entry(s)
    backup["genre"] = [backup_media] if backup_media else []
    found, _ = resolve(s, backup, eid, path)
    assert found == (eid if accepted else None)


@pytest.mark.parametrize("path", ["resolver", "existing-index", "legacy-existing"])
def test_exact_source_url_retains_precedence(env, monkeypatch, path):
    s, _ = env
    eid, backup = setup_exact_entry(s)
    backup["url"] = "/work/99201"
    monkeypatch.setattr(s, "_current_source_id_for_backup", lambda *a: "seed")
    found, reason = resolve(s, backup, eid, path)
    assert found == eid
    assert "exact source URL" in reason


@pytest.mark.parametrize("path", ["resolver", "existing-index", "legacy-existing"])
def test_exact_title_does_not_restore_rejected_import(env, path):
    s, _ = env
    eid, backup = setup_exact_entry(s)
    backup["genre"] = ["Manga"]
    s._remember_import_rejection(backup, eid)
    found, _ = resolve(s, backup, eid, path)
    assert found is None
