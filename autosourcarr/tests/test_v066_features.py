import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db, now_iso
from app.service import AutoSourcarrService


def source_row(source_id: str, name: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = ""):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": "", "artist": "", "genre": ["Action"],
        "status": 1, "initialized": True, "realUrl": "",
    }


def add_identity(entry_id: int, provider: str, external_id: str, title: str, description: str, match_type: str = "metadata:test"):
    with db.catalog() as c:
        c.execute(
            """INSERT INTO identity_links(entry_id,provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,title,description,thumbnail_url,status,author,artist,genre,media_type,country,year,adult,metadata_json,last_checked)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (entry_id, provider, provider, external_id, "", "work", .99, match_type, 0, title, description, "", 0, "", "", "", "", "", None, 0, "{}", now_iso()),
        )


def test_fuzzy_metadata_identity_cannot_merge_unrelated_source_titles():
    s = AutoSourcarrService(); source_row("v66-a", "A", 1); source_row("v66-b", "B", 2)
    a = manga(66001, "Disastrous Necromancer", "A soldier becomes a necromancer after the world changes and monsters appear across modern cities.")
    b = manga(66002, "Infinite Necromancer", "A different hunter enters endless dungeons and gains a unique summoning class after a tower appears.")
    e1 = s._create_entry(a, "v66-a"); s._upsert_match(e1, "v66-a", a, 1.0)
    e2 = s._create_entry(b, "v66-b"); s._upsert_match(e2, "v66-b", b, 1.0)
    # Simulate the dangerous state: a provider search mistakenly attached the same
    # external work, title and synopsis to both entries.
    provider_summary = "A provider synopsis that is identical because both entries were incorrectly linked to one metadata record and therefore must not become duplicate proof."
    add_identity(e1, "anilist", "999001", "Necromancer", provider_summary)
    add_identity(e2, "anilist", "999001", "Necromancer", provider_summary)
    s.add_title(e1, "Necromancer", title_type="identity-primary", source_id="metadata:anilist")
    s.add_title(e2, "Necromancer", title_type="identity-primary", source_id="metadata:anilist")
    evidence = s._auto_merge_evidence(e1, e2)
    assert evidence["shared_identities"] == [("anilist", "999001")]
    assert evidence["exact_title_count"] == 0
    assert evidence["eligible"] is False


def test_metadata_generated_titles_and_summaries_do_not_count_as_native_duplicate_proof():
    s = AutoSourcarrService(); source_row("v66-c", "C", 1); source_row("v66-d", "D", 2)
    a = manga(66003, "Warrior Versus Hero", "A martial hero defends a kingdom in a long source-provided synopsis about war and politics.")
    b = manga(66004, "The Greatest Estate Developer", "An engineer reincarnates into a fantasy noble and uses construction knowledge to save an estate.")
    e1 = s._create_entry(a, "v66-c"); s._upsert_match(e1, "v66-c", a, 1.0)
    e2 = s._create_entry(b, "v66-d"); s._upsert_match(e2, "v66-d", b, 1.0)
    generated = "A long identical generated metadata synopsis that would have exceeded the automatic summary threshold in older builds and caused a feedback loop."
    for eid in (e1, e2):
        s.add_title(eid, "Shared Provider Alias One", title_type="identity-primary", source_id="metadata:anilist")
        s.add_title(eid, "Shared Provider Alias Two", title_type="identity-alias", source_id="metadata:anilist")
        add_identity(eid, "anilist", "999002", "Shared Provider Alias One", generated)
    snap1 = s._entry_duplicate_evidence_snapshot(e1)
    snap2 = s._entry_duplicate_evidence_snapshot(e2)
    assert not ({"shared provider alias one", "shared provider alias two"} & set(snap1["normalized_titles"]))
    assert not ({"shared provider alias one", "shared provider alias two"} & set(snap2["normalized_titles"]))
    assert generated not in snap1["descriptions"] and generated not in snap2["descriptions"]
    assert s._auto_merge_snapshot_evidence(snap1, snap2)["eligible"] is False


def test_exact_thumbnail_needs_exact_native_title_before_auto_merge():
    s = AutoSourcarrService(); source_row("v66-e", "E", 1); source_row("v66-f", "F", 2)
    a = manga(66005, "Necromancer Academy"); b = manga(66006, "Necromancer Evolution")
    e1 = s._create_entry(a, "v66-e"); s._upsert_match(e1, "v66-e", a, 1.0)
    e2 = s._create_entry(b, "v66-f"); s._upsert_match(e2, "v66-f", b, 1.0)
    with db.catalog() as c:
        for subject in ("source:v66-e:66005", "source:v66-f:66006"):
            c.execute("INSERT OR REPLACE INTO thumbnail_fingerprints(subject_key,fingerprint,width,height,source_url,updated_at) VALUES(?,?,?,?,?,?)", (subject, "same-placeholder-pixels", 120, 160, "", now_iso()))
    evidence = s._auto_merge_evidence(e1, e2)
    assert evidence["exact_thumbnail_match"] is True
    assert evidence["exact_title_count"] == 0
    assert evidence["eligible"] is False


def test_external_android_build_uses_public_url_and_suwayomi_uses_internal_url():
    root = Path(__file__).resolve().parents[2]
    gradle = (root / "extension/module/build.gradle.kts").read_text(encoding="utf-8")
    builder = (root / "extension/build-in-container.sh").read_text(encoding="utf-8")
    kotlin = (root / "extension/module/src/autosourcarr/AutoSourcarr.kt").read_text(encoding="utf-8")
    service = (root / "autosourcarr/app/service.py").read_text(encoding="utf-8")
    main = (root / "autosourcarr/app/main.py").read_text(encoding="utf-8")
    assert "AUTOSOURCARR_EXTENSION_BASE_URL" in gradle
    assert "AUTOSOURCARR_INTERNAL_URL" in builder
    assert "AUTOSOURCARR_PUBLIC_URL" in builder
    assert "AUTOSOURCARR_ALLOW_DOCKER_ANDROID_URL" in builder and "exit 11" in builder
    assert "Suwayomi release JAR" in builder and "Tachiyomi/Mihon release APK" in builder
    assert "absolute(baseUrl, url)" in kotlin
    assert 'f"/api/v1/pages/{candidate_id}/' in service
    assert 'f"{public}/api/v1/pages/' not in service
    assert "request_base = str(request.base_url).rstrip" in main
    assert 'request_base}/{str(url).lstrip' in main


def test_v066_duplicate_safety_repair_is_still_version_gated():
    root = Path(__file__).resolve().parents[1]
    service = (root / "app/service.py").read_text(encoding="utf-8")
    main = (root / "app/main.py").read_text(encoding="utf-8")
    assert 'automatic_merge_safety_repair_version' in service
    assert 'automatic_merge_safety_repair_version' in main


def test_local_source_ingest_ignores_provider_titles_and_later_automatic_matches(monkeypatch):
    s = AutoSourcarrService()
    for i, sid in enumerate(("v66-anchor", "v66-wrong", "v66-next"), 1):
        source_row(sid, sid, i)
    anchor_payload = manga(
        66101,
        "Disastrous Necromancer",
        "The native source synopsis follows a class-awakened player fighting monsters in a changed modern world.",
    )
    entry_id = s._create_entry(anchor_payload, "v66-anchor")
    s._upsert_match(entry_id, "v66-anchor", anchor_payload, 1.0, "auto")

    provider_summary = "A provider synopsis for an entirely different tower hunter and an infinite summoning progression system."
    add_identity(entry_id, "anilist", "66199", "Infinite Necromancer", provider_summary)
    s.add_title(entry_id, "Infinite Necromancer", title_type="identity-primary", source_id="metadata:anilist")
    s._choose_metadata(entry_id)
    monkeypatch.setattr(s, "_candidate_entries_for_manga", lambda _manga, limit=250: [entry_id])

    # Even if metadata made this the preferred display title, it is not source-match
    # authority and cannot create a 100% local match.
    poisoned = manga(66102, "Infinite Necromancer", provider_summary)
    assert s._local_match_candidates(poisoned, "v66-wrong") == []

    # A genuine exact title from the original ingestion anchor is still accepted.
    genuine = manga(66103, "Disastrous Necromancer", anchor_payload["description"])
    candidates = s._local_match_candidates(genuine, "v66-wrong")
    assert candidates and candidates[0][0] == entry_id and candidates[0][1] >= 0.99

    # Simulate an already-bad later automatic source attachment. Its title must not
    # become evidence that pulls a third source into the same snowball.
    s._upsert_match(entry_id, "v66-wrong", poisoned, 1.0, "auto")
    assert s._local_match_candidates(manga(66104, "Infinite Necromancer", provider_summary), "v66-next") == []


def test_metadata_provider_cannot_bootstrap_from_another_provider_identity():
    s = AutoSourcarrService(); source_row("v66-native", "Native", 1)
    native = manga(
        66201,
        "Disastrous Necromancer",
        "A native source synopsis about a modern class awakening, city monsters, and a soldier becoming a necromancer.",
    )
    entry_id = s._create_entry(native, "v66-native")
    s._upsert_match(entry_id, "v66-native", native, 1.0, "auto")
    wrong_summary = "A different work about an infinite tower, a lone hunter, summoning skills, and repeated dungeon floors."
    add_identity(entry_id, "anilist", "66299", "Infinite Necromancer", wrong_summary)
    s.add_title(entry_id, "Infinite Necromancer", title_type="identity-primary", source_id="metadata:anilist")
    s._choose_metadata(entry_id)

    score, _reason = s._identity_candidate_score(
        entry_id,
        {
            "provider": "mangadex",
            "external_id": "another-wrong-id",
            "title": "Infinite Necromancer",
            "titles": [{"title": "Infinite Necromancer"}],
            "description": wrong_summary,
            "author": "",
        },
    )
    assert score == 0.0
