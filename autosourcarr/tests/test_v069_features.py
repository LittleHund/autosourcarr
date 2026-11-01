import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.service import AutoSourcarrService


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def source_row(source_id: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, source_id, source_id, "en", "https://example.invalid", "", 1, 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = "", author: str = ""):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": author, "artist": "", "genre": ["Drama"],
        "status": 1, "initialized": True, "realUrl": "",
    }


def test_v069_uses_persistent_sibling_config_and_signing_state():
    r = root()
    compose = (r / "docker-compose.truenas.yml").read_text()
    helper = (r / "compose-env-file.yml").read_text()
    example = (r / "examples/manga-cluster-autosourcarr-services.yml").read_text()
    init = (r / "scripts/init-truenas-config.sh").read_text()
    builder = (r / "extension/build-in-container.sh").read_text()
    build_sh = (r / "extension/build.sh").read_text()

    persistent = "/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config"
    for text in (compose, helper, example):
        assert persistent + "/autosourcarr.env" in text or text is example
        assert persistent + ":/config" in text or text is example
    # The integrated builder uses this same retained /config inside each app.
    # No separate SDK service or second config mount is required.
    assert persistent + ":/config" in example
    assert "APP_SERVICES_MODE: integrated" in example
    assert 'CONFIG="${3:-$BASE/config}"' in init
    assert "Generated persistent AutoSourcarr reader API credential" in init
    assert 'SIGN_DIR="$CONFIG_DIR/.extension-signing"' in builder
    assert "Migrated project-local Android signing identity" in builder
    assert "AUTOSOURCARR_HOST_CONFIG_DIR" in build_sh
    # Release bundles contain templates only, so an overlay cannot overwrite the
    # user's legacy project-local live values before the one-time migration runs.
    assert not (r / "config/autosourcarr.env").exists()
    assert not (r / "config/source-host.env").exists()
    assert (r / "config/autosourcarr.env.example").exists()
    assert (r / "config/source-host.env.example").exists()


def test_v069_reader_source_reports_authelia_interception_instead_of_empty_catalog():
    r = root()
    source = (r / "extension/module/src/autosourcarr/AutoSourcarr.kt").read_text()
    assert "readerJsonBody(response)" in source
    assert "reader API was intercepted by Authelia" in source
    assert "Bypass only /api/reader/v1/" in source
    assert 'header("X-AutoSourcarr-API-Key", key)' in source


def test_v069_subbed_and_matrix_have_same_filter_controls_and_entry_unbind_actions():
    r = root()
    html = (r / "autosourcarr/app/static/index.html").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    main = (r / "autosourcarr/app/main.py").read_text()
    assert 'id="matrixEntryMatches"' in html
    assert 'id="subbedEntryMatches"' in html
    for suffix in ("MinMatches", "EntryMatches", "Status", "PublicationStatus", "Sort"):
        assert f'id="matrix{suffix}"' in html
        assert f'id="subbed{suffix}"' in html
    changelog = (r / "CHANGELOG.md").read_text()
    assert "Unbind automatic matches" in changelog
    assert "Split / unbind duplicates" in changelog
    assert "Redo automation" in js
    assert "/unbind-auto-matches" in main  # compatibility endpoint retained


def test_generic_exact_title_cannot_auto_attach_without_independent_corroboration(monkeypatch):
    s = AutoSourcarrService()
    source_row("v69-a", 1); source_row("v69-b", 2)
    anchor = manga(69001, "First Love", "A school romance centered on two childhood friends and their families.", "Author A")
    eid = s._create_entry(anchor, "v69-a")
    s._upsert_match(eid, "v69-a", anchor, 1.0, "auto")
    monkeypatch.setattr(s, "_candidate_entries_for_manga", lambda _manga, limit=250: [eid])
    unrelated = manga(69002, "First Love", "An unrelated workplace romance between adults in another city.", "Author B")
    assert s._local_match_candidates(unrelated, "v69-b") == []


def test_prior_auto_attachment_cannot_authorize_next_automatic_attachment(monkeypatch):
    s = AutoSourcarrService()
    for i, sid in enumerate(("v69-anchor", "v69-wrong", "v69-next"), 1): source_row(sid, i)
    anchor = manga(69101, "Disastrous Necromancer", "A class-awakened soldier fights monsters in a changed modern world.")
    eid = s._create_entry(anchor, "v69-anchor")
    s._upsert_match(eid, "v69-anchor", anchor, 1.0, "auto")
    wrong = manga(69102, "Martial Peak", "A martial cultivator climbs a sect hierarchy through ancient inheritance trials.")
    s._upsert_match(eid, "v69-wrong", wrong, 1.0, "auto-additional")
    monkeypatch.setattr(s, "_candidate_entries_for_manga", lambda _manga, limit=250: [eid])
    assert s._local_match_candidates(manga(69103, "Martial Peak", wrong["description"]), "v69-next") == []


def test_startup_repair_targets_snowball_shape_and_splits_clearly_foreign_auto_records():
    s = AutoSourcarrService()
    for i in range(8): source_row(f"v69-repair-{i}", i + 1)
    anchor = manga(69300, "Disastrous Necromancer", "A soldier awakens a necromancer class while modern cities are overrun by monsters.")
    eid = s._create_entry(anchor, "v69-repair-0")
    s._upsert_match(eid, "v69-repair-0", anchor, 1.0, "auto")
    wrong_titles = [
        "Martial Peak", "The Real Daughter", "Star Martial God Technique",
        "Rebirth of the Urban Immortal", "Vampires and Knight", "Starting From Today", "Apotheosis",
    ]
    for i, title in enumerate(wrong_titles, 1):
        s._upsert_match(
            eid, f"v69-repair-{i}",
            manga(69300 + i, title, f"Unrelated synopsis {i} about a completely different cast and setting."),
            1.0, "auto", False,
        )
    out = s._repair_unsafe_automatic_source_attachments()
    assert out["repaired_entries"] >= 1
    assert out["split_records"] >= 7
    original = s.get_entry(eid)
    assert {(m["source_id"], int(m["source_manga_id"])) for m in original["matches"]} == {("v69-repair-0", 69300)}


def test_one_click_unbind_splits_each_automatic_record_and_keeps_anchor():
    s = AutoSourcarrService()
    source_row("v69-unbind-a", 1); source_row("v69-unbind-b", 2)
    anchor = manga(69201, "Stable Anchor Work", "Anchor summary.")
    wrong_a = manga(69202, "Unrelated First Work", "Wrong one.")
    wrong_b = manga(69203, "Unrelated Second Work", "Wrong two.")
    eid = s._create_entry(anchor, "v69-unbind-a")
    s._upsert_match(eid, "v69-unbind-a", anchor, 1.0, "auto")
    s._upsert_source_variant(eid, "v69-unbind-a", wrong_a, 1.0, "auto-additional", False)
    s._upsert_match(eid, "v69-unbind-b", wrong_b, 1.0, "auto", False)

    out = s.unbind_entry_automatic_records(eid)
    assert out["split_records"] == 2
    assert len(out["new_entry_ids"]) == 2
    original = s.get_entry(eid)
    attached = {(m["source_id"], int(m["source_manga_id"])) for m in original["matches"]}
    attached |= {(m["source_id"], int(m["source_manga_id"])) for m in original.get("source_variants", [])}
    assert attached == {("v69-unbind-a", 69201)}
    for new_id in out["new_entry_ids"]:
        new_entry = s.get_entry(new_id)
        assert len(new_entry["matches"]) == 1


def test_v069_repair_and_history_remain_present_after_later_releases():
    r = root()
    service = (r / "autosourcarr/app/service.py").read_text()
    changelog = (r / "CHANGELOG.md").read_text()
    assert "def _repair_unsafe_automatic_source_attachments" in service
    assert "## 0.2.69 - 2026-08-28" in changelog
    assert "0.2.69 → 1.4.2069" in changelog
