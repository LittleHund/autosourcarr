import os
import tempfile

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.service import AutoSourcarrService


def _source_row(source_id: str, name: str, priority: int = 10):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen)
            VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def _manga(mid: int, title: str, description: str = "Short source summary."):
    return {
        "id": mid,
        "title": title,
        "url": f"/manga/{mid}",
        "thumbnailUrl": "",
        "description": description,
        "author": "Source Author",
        "artist": "Source Artist",
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def _identity(provider: str, external_id: str, title: str, **overrides):
    data = {
        "provider": provider,
        "provider_name": provider.title(),
        "external_id": external_id,
        "url": f"https://metadata.invalid/{provider}/{external_id}",
        "identity_scope": "work",
        "title": title,
        "titles": [{"title": title, "language": "en", "type": "primary"}],
        "description": "A canonical database description with enough detail to be preferred over the short source summary.",
        "cover_url": "https://images.invalid/canonical.jpg",
        "status": "completed",
        "author": "Canonical Author",
        "artist": "Canonical Artist",
        "genres": ["Drama", "Fantasy"],
        "media_type": "MANGA",
        "country": "JP",
        "year": 2020,
        "adult": False,
        "links": {},
        "raw": {},
    }
    data.update(overrides)
    return data


def test_identity_layer_becomes_canonical_metadata_and_is_exposed():
    s = AutoSourcarrService()
    _source_row("identity-source-a", "Identity Source A")
    eid, _, _ = s.ingest_manga("identity-source-a", _manga(920001, "Identity Layer Work"))
    s._upsert_identity_link(eid, _identity("anilist", "123456", "Identity Layer Work"), 0.99, "test")
    s._choose_metadata(eid)

    entry = s.get_entry(eid)
    assert entry["description"].startswith("A canonical database description")
    assert entry["thumbnail_url"] == "https://images.invalid/canonical.jpg"
    assert entry["author"] == "Canonical Author"
    assert entry["status"] == 2
    assert entry["identities"][0]["provider"] == "anilist"
    assert entry["identities"][0]["external_id"] == "123456"
    assert any(t["title_type"] == "identity-primary" for t in entry["titles"])


def test_weaker_crossref_cannot_replace_stronger_identity():
    s = AutoSourcarrService()
    _source_row("identity-source-b", "Identity Source B")
    eid, _, _ = s.ingest_manga("identity-source-b", _manga(920002, "Identity Confidence Work"))
    s._upsert_identity_link(eid, _identity("anilist", "strong-id", "Identity Confidence Work"), 0.99, "direct")
    s._upsert_identity_link(
        eid,
        _identity("anilist", "weak-id", "", titles=[], description="", cover_url=""),
        0.95,
        "crossref:mangadex",
    )
    rows = [x for x in s.get_entry(eid)["identities"] if x["provider"] == "anilist"]
    assert len(rows) == 1
    assert rows[0]["external_id"] == "strong-id"


def test_entry_merge_preserves_external_identity_rows():
    s = AutoSourcarrService()
    _source_row("identity-merge-a", "Identity Merge A")
    _source_row("identity-merge-b", "Identity Merge B")
    a = _manga(920003, "Identity Merge Work")
    b = _manga(920004, "Identity-Merge Work")
    e1 = s._create_entry(a, "identity-merge-a")
    s._upsert_match(e1, "identity-merge-a", a, 1.0)
    e2 = s._create_entry(b, "identity-merge-b")
    s._upsert_match(e2, "identity-merge-b", b, 1.0)
    s._upsert_identity_link(e1, _identity("anilist", "777", "Identity Merge Work"), 0.99, "test")
    s._upsert_identity_link(e2, _identity("mangadex", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Identity Merge Work"), 0.99, "test")

    merged = s.merge_entries(e1, e2, "identity-test")
    providers = {x["provider"] for x in s.get_entry(merged)["identities"]}
    assert {"anilist", "mangadex"} <= providers


def test_import_combines_original_metadata_with_external_identity_without_replacing_backup(monkeypatch):
    s = AutoSourcarrService()
    item = {
        "title": "The War of Corpses",
        "description": "Uce dreams of becoming the champion in the brutal War of Corpses and revives Eleonora to compete.",
        "author": "MAYO-NUGURI",
    }
    candidate = _identity(
        "anilist", "900001", "War of Corpses",
        titles=[
            {"title": "War of Corpses", "language": "en", "type": "primary"},
            {"title": "The War of Corpses", "language": "en", "type": "alias"},
        ],
        description="Uce dreams of becoming the champion in the brutal War of Corpses and revives Eleonora to compete.",
        author="MAYO-NUGURI",
    )

    async def fake_search(provider, query, force=False):
        return [candidate] if provider == "anilist" else []

    monkeypatch.setattr(s, "_metadata_provider_search", fake_search)
    bundle = __import__("asyncio").run(s._resolve_import_metadata_identity(item))
    assert item["title"] == "The War of Corpses"
    assert item["description"].startswith("Uce dreams")
    assert "War of Corpses" in bundle["titles"]
    assert any(x["provider"] == "anilist" and x["external_id"] == "900001" for x in bundle["identities"])


def test_import_shared_external_identity_is_direct_matching_evidence():
    s = AutoSourcarrService()
    _source_row("identity-import-shared", "Identity Import Shared")
    eid, _, _ = s.ingest_manga("identity-import-shared", _manga(930001, "Completely Different Source Display Title", "Source summary that is not enough alone."))
    s._upsert_identity_link(eid, _identity("anilist", "910001", "The War of Corpses"), 0.99, "test")
    item = {
        "title": "The War of Corpses",
        "description": "Backup synopsis.",
        "_autosourcarr_metadata_identity": {
            "checked": True,
            "titles": ["The War of Corpses", "War of Corpses"],
            "descriptions": ["A canonical database description with enough detail to be preferred over the short source summary."],
            "authors": [],
            "identities": [{"provider": "anilist", "external_id": "910001", "confidence": 0.99}],
            "matches": [],
        },
    }
    resolved, reason = s._import_auto_resolve_candidate(item)
    assert resolved == eid
    assert "shared anilist identity" in reason


def test_war_of_corpses_compatible_source_reference_auto_matches(monkeypatch):
    s = AutoSourcarrService()
    _source_row("940001", "MangaFire (EN)")
    backup_description = (
        "Uce, a down-on-his-luck young man, dreams of becoming the champion in the brutal and legendary "
        "War of Corpses—a sport where necromancers and their knights battle for supremacy. In a desperate bid, "
        "he revives Eleonora, an abandoned corpse who claims to have been the deadliest mercenary alive."
    )
    item = {
        "source": 940001,
        "sourceName": "MangaFire (EN)",
        "url": "/manga/the-war-of-corpses.5w2mv",
        "title": "The War of Corpses QA 940001",
        "description": backup_description,
        "author": "MAYO-NUGURI",
    }
    created = {"id": None}

    async def fake_reference(_item):
        eid, _, _ = s.ingest_manga(
            "940001",
            {
                "id": 940002,
                "title": "War of Corpses",
                "url": "/manga/war-of-corpses",
                "thumbnailUrl": "",
                "description": backup_description,
                "author": "MAYO-NUGURI",
                "artist": "MAYO-NUGURI",
                "genre": ["Action", "Webtoon"],
                "status": 1,
                "initialized": True,
                "realUrl": "",
            },
        )
        created["id"] = eid
        return {"entry_id": eid, "exact": False, "reason": "enabled source exact-title reference"}

    monkeypatch.setattr(s, "_hydrate_backup_source_reference", fake_reference)
    details = __import__("asyncio").run(s.resolve_backup_manga_entry_details(item, use_metadata=False))
    assert details["entry_id"] == created["id"]
    assert details["reference_entry_id"] == created["id"]
    assert "summary" in details["reason"] or "compatible" in details["reason"]


def test_normal_source_match_evidence_does_not_trust_identity_database_descriptions():
    s = AutoSourcarrService()
    _source_row("identity-evidence-a", "Identity Evidence A")
    eid, _, _ = s.ingest_manga("identity-evidence-a", _manga(950001, "Native Display Only", "Unhelpful source text."))
    canonical = _identity(
        "anilist", "950099", "War of Corpses",
        description="The canonical synopsis about Uce, Eleonora, necromancers and the War of Corpses tournament.",
    )
    s._upsert_identity_link(eid, canonical, 0.99, "test")
    entry = s.get_entry(eid)
    evidence = s._source_match_evidence(
        entry,
        {
            "title": "The War of Corpses",
            "description": "The canonical synopsis about Uce, Eleonora, necromancers and the War of Corpses tournament.",
            "author": "Canonical Author",
        },
    )
    assert evidence["eligible"] is False
    assert evidence["reason"] in {"insufficient trusted-anchor evidence", "summary similarity cannot authorize an automatic source attachment by itself"}


def test_import_metadata_is_transferred_to_resolved_catalog_entry():
    s = AutoSourcarrService()
    _source_row("identity-import-transfer", "Identity Import Transfer")
    eid, _, _ = s.ingest_manga(
        "identity-import-transfer",
        _manga(960001, "Sparse Imported Work", "Short source summary."),
    )
    candidate = _identity(
        "anilist",
        "960099",
        "Canonical Imported Work",
        titles=[
            {"title": "Canonical Imported Work", "language": "en", "type": "primary"},
            {"title": "Sparse Imported Work", "language": "en", "type": "alias"},
        ],
        description="A richer database summary retained after the import match is accepted.",
    )
    item = {
        "title": "Sparse Imported Work",
        "_autosourcarr_metadata_identity": {
            "checked": True,
            "titles": ["Canonical Imported Work", "Sparse Imported Work"],
            "descriptions": [candidate["description"]],
            "authors": [candidate["author"]],
            "identities": [{"provider": "anilist", "external_id": "960099", "confidence": 0.99}],
            "matches": [{"provider": "anilist", "external_id": "960099", "confidence": 0.99}],
            "records": [{"provider": "anilist", "confidence": 0.99, "reason": "test", "candidate": candidate}],
        },
    }
    assert s._apply_import_metadata_to_entry(eid, item) == 1
    entry = s.get_entry(eid)
    assert any(x["provider"] == "anilist" and x["external_id"] == "960099" for x in entry["identities"])
    assert any(x["title"] == "Canonical Imported Work" for x in entry["titles"])
    assert "richer database summary" in entry["description"].lower()
