import asyncio
import os
import tempfile

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.main import _ui_user_context
from app.service import AutoSourcarrService


class FakeRequest:
    def __init__(self, headers):
        self.headers = {str(k).lower(): v for k, v in headers.items()}


def source_row(source_id: str, name: str, priority: int = 10):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = "", author: str = "A"):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": author, "artist": author,
        "genre": ["Action"], "status": 1, "initialized": True, "realUrl": "",
    }


def test_existing_authelia_remote_variables_are_primary_and_groups_are_retained():
    ctx = _ui_user_context(FakeRequest({
        "Remote-User": "alice",
        "Remote-Email": "alice@example.test",
        "Remote-Groups": "admins, manga",
        "X-Forwarded-Proto": "https",
        "Host": "autosourcarr.example.test",
    }))
    assert ctx["authelia"] is True
    assert ctx["header"] == "Remote-User"
    assert ctx["user"] == "alice"
    assert ctx["email"] == "alice@example.test"
    assert ctx["groups"] == ["admins", "manga"]
    assert ctx["direct_access"] is False


def test_direct_backend_access_is_reported_as_authelia_bypass():
    ctx = _ui_user_context(FakeRequest({"Host": "192.168.1.121:30060"}))
    assert ctx["authelia"] is False
    assert ctx["direct_access"] is True
    assert ctx["host"] == "192.168.1.121:30060"


def test_equivalent_exact_title_summary_cluster_can_auto_match_any_safe_row():
    s = AutoSourcarrService()
    source_row("v58-equiv-a", "A", 10)
    source_row("v58-equiv-b", "B", 20)
    summary = (
        "A long identical synopsis describing the same wizard who retires to the countryside "
        "after an arrow injury and protects a small village with his companions."
    )
    a = manga(58001, "The Strongest Wizard Becomes a Countryside Guard After Taking an Arrow to the Knee", summary)
    b = manga(58002, "The Strongest Wizard Becomes a Countryside Guard After Taking an Arrow to the Knee", summary)
    e1 = s._create_entry(a, "v58-equiv-a"); s._upsert_match(e1, "v58-equiv-a", a, 1.0)
    e2 = s._create_entry(b, "v58-equiv-b"); s._upsert_match(e2, "v58-equiv-b", b, 1.0)
    chosen, reason = s._import_auto_resolve_candidate({
        "title": a["title"], "description": summary, "author": "A"
    })
    assert chosen in {e1, e2}
    assert "equivalent catalog rows" in reason


def test_equivalent_import_cluster_cannot_cross_manga_webtoon_gate():
    s = AutoSourcarrService()
    source_row("v58-media-a", "A", 10)
    source_row("v58-media-b", "B", 20)
    summary = "A sufficiently long shared synopsis for two adaptations of one original work that must remain distinct editions."
    a = manga(58101, "Adaptation Pair (Manga)", summary)
    b = manga(58102, "Adaptation Pair (Webtoon)", summary)
    e1 = s._create_entry(a, "v58-media-a"); s._upsert_match(e1, "v58-media-a", a, 1.0)
    e2 = s._create_entry(b, "v58-media-b"); s._upsert_match(e2, "v58-media-b", b, 1.0)
    s._recompute_entry_media_mode(e1); s._recompute_entry_media_mode(e2)
    accepted = [
        {"entry_id": e1, "title_score": 1.0, "summary_similarity": 1.0, "shared_identities": []},
        {"entry_id": e2, "title_score": 1.0, "summary_similarity": 1.0, "shared_identities": []},
    ]
    chosen, _ = s._import_equivalent_candidate_choice({"title": "Adaptation Pair", "description": summary}, accepted)
    assert chosen is None


def test_import_thumbnail_fingerprints_include_provider_record_cover(monkeypatch):
    s = AutoSourcarrService()
    seen = []

    async def fake(url, referer=""):
        seen.append(url)
        return {"fingerprint": "provider-cover-fingerprint", "width": 100, "height": 150}

    monkeypatch.setattr(s, "_thumbnail_fingerprint_for_url", fake)
    item = {
        "title": "Metadata Cover Work",
        "_autosourcarr_metadata_identity": {
            "matches": [{"provider": "anilist", "external_id": "1", "confidence": 1.0}],
            "records": [{
                "provider": "anilist", "confidence": 1.0,
                "candidate": {"thumbnail_url": "https://metadata.invalid/exact-cover.jpg"},
            }],
        },
    }
    values = asyncio.run(s._import_thumbnail_fingerprints(item))
    assert "https://metadata.invalid/exact-cover.jpg" in seen
    assert values == {"provider-cover-fingerprint"}


def test_colored_marker_is_never_kept_in_unified_preferred_title():
    s = AutoSourcarrService()
    source_row("v58-color", "Color", 10)
    record = manga(58201, "Edition Name (Colored)")
    eid = s._create_entry(record, "v58-color")
    s._upsert_match(eid, "v58-color", record, 1.0)
    with db.catalog() as c:
        c.execute("UPDATE entries SET preferred_title=? WHERE id=?", ("Edition Name (colored)", eid))
    s._sync_colored_title_label(eid)
    assert s.entry_preferred_title(eid) == "Edition Name"

def test_final_import_pass_hydrates_candidate_db_identity_before_giving_up(monkeypatch):
    s = AutoSourcarrService()
    source_row("v58-id-hydrate-a", "A", 10)
    source_row("v58-id-hydrate-b", "B", 20)
    a = manga(58301, "Pending Identity Work", "First scraper synopsis that does not corroborate the backup.")
    b = manga(58302, "Pending Identity Work", "Second scraper synopsis that also does not corroborate the backup.")
    e1 = s._create_entry(a, "v58-id-hydrate-a"); s._upsert_match(e1, "v58-id-hydrate-a", a, 1.0)
    e2 = s._create_entry(b, "v58-id-hydrate-b"); s._upsert_match(e2, "v58-id-hydrate-b", b, 1.0)
    item = {
        "title": "Pending Identity Work",
        "description": "A backup synopsis intentionally unlike either scraper synopsis.",
        "_autosourcarr_metadata_identity": {
            "identities": [{"provider": "anilist", "external_id": "58399", "confidence": 1.0}],
            "matches": [], "records": [], "titles": ["Pending Identity Work"], "descriptions": [], "authors": [],
        },
    }

    async def fake_resolve(entry_id, force=False):
        if int(entry_id) == e2:
            with db.catalog() as c:
                c.execute(
                    """INSERT OR REPLACE INTO identity_links(
                       entry_id,provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,
                       title,description,thumbnail_url,status,author,artist,genre,media_type,country,year,adult,metadata_json,last_checked)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,datetime('now'))""",
                    (e2, "anilist", "AniList", "58399", "", "work", .99, "test", 0,
                     "Pending Identity Work", "", "", 0, "", "", "", "", "", None, 0, "{}"),
                )
        return {"entry_id": int(entry_id)}

    monkeypatch.setattr(s, "resolve_entry_identity", fake_resolve)
    chosen, reason = asyncio.run(s._import_auto_resolve_candidate_async(item, candidate_ids=[e1, e2]))
    assert chosen == e2
    assert "anilist" in reason.lower()
