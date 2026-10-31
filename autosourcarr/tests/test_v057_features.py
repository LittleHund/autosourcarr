import io
import os
import tempfile

from PIL import Image, PngImagePlugin

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db, now_iso
from app.service import AutoSourcarrService
from app.thumbnail_fingerprint import exact_thumbnail_fingerprint


def source_row(source_id: str, name: str, priority: int = 10):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = "", author: str = "A"):
    return {"id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "", "description": description,
            "author": author, "artist": author, "genre": ["Action"], "status": 1, "initialized": True, "realUrl": ""}


def image_bytes(pixel=(100, 20, 200, 255), *, metadata=False):
    image = Image.new("RGBA", (120, 160), pixel)
    out = io.BytesIO()
    info = None
    if metadata:
        info = PngImagePlugin.PngInfo(); info.add_text("note", "different file bytes")
    image.save(out, format="PNG", compress_level=9 if metadata else 1, pnginfo=info)
    return out.getvalue()


def test_exact_thumbnail_fingerprint_compares_decoded_pixels_not_file_bytes():
    a = image_bytes(metadata=False)
    b = image_bytes(metadata=True)
    assert a != b
    fa = exact_thumbnail_fingerprint(a)
    fb = exact_thumbnail_fingerprint(b)
    assert fa["fingerprint"] == fb["fingerprint"]
    assert (fa["width"], fa["height"]) == (120, 160)


def test_exact_thumbnail_fingerprint_rejects_one_pixel_difference():
    a = Image.new("RGBA", (120, 160), (100, 20, 200, 255))
    b = a.copy(); b.putpixel((0, 0), (101, 20, 200, 255))
    ba, bb = io.BytesIO(), io.BytesIO(); a.save(ba, format="PNG"); b.save(bb, format="PNG")
    assert exact_thumbnail_fingerprint(ba.getvalue())["fingerprint"] != exact_thumbnail_fingerprint(bb.getvalue())["fingerprint"]


def test_import_equal_titles_are_disambiguated_by_summary_instead_of_terminal_ambiguity():
    s = AutoSourcarrService(); source_row("v57-import-a", "A", 10); source_row("v57-import-b", "B", 20)
    good_summary = "A long exact backup synopsis about a solitary adventurer crossing another world and collecting skills while avoiding crowded guilds and cities."
    bad_summary = "A completely different story about a martial artist climbing a heavenly tower and fighting ancient clans for immortality."
    e1 = s._create_entry(manga(57001, "Loner Life in Another World", good_summary), "v57-import-a")
    s._upsert_match(e1, "v57-import-a", manga(57001, "Loner Life in Another World", good_summary), 1.0)
    e2 = s._create_entry(manga(57002, "Loner Life in Another World", bad_summary), "v57-import-b")
    s._upsert_match(e2, "v57-import-b", manga(57002, "Loner Life in Another World", bad_summary), 1.0)
    chosen, reason = s._import_auto_resolve_candidate({"title": "Loner Life in Another World", "description": good_summary, "author": "A"})
    assert chosen == e1
    assert reason


def test_shared_metadata_identity_is_automatic_duplicate_evidence():
    s = AutoSourcarrService(); source_row("v57-id-a", "A", 10); source_row("v57-id-b", "B", 20)
    e1 = s._create_entry(manga(57101, "Completely Different A"), "v57-id-a"); s._upsert_match(e1, "v57-id-a", manga(57101, "Completely Different A"), 1.0)
    e2 = s._create_entry(manga(57102, "Unrelated Display B"), "v57-id-b"); s._upsert_match(e2, "v57-id-b", manga(57102, "Unrelated Display B"), 1.0)
    with db.catalog() as c:
        for eid, provider in ((e1, "anilist"), (e2, "anilist")):
            c.execute("""INSERT INTO identity_links(entry_id,provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,title,description,thumbnail_url,status,author,artist,genre,media_type,country,year,adult,metadata_json,last_checked)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                      (eid, provider, provider, "57199", "", "work", .99, "source-exact", 0, "", "", "", 0, "", "", "", "", "", None, 0, "{}", now_iso()))
    evidence = s._auto_merge_evidence(e1, e2)
    assert evidence["eligible"] is True
    assert evidence["shared_identities"] == [("anilist", "57199")]


def test_exact_thumbnail_duplicate_evidence_keeps_manga_webtoon_hard_blocker():
    s = AutoSourcarrService(); source_row("v57-thumb-a", "A", 10); source_row("v57-thumb-b", "B", 20)
    a = manga(57201, "Adaptation Pair (Manga)"); b = manga(57202, "Adaptation Pair (Webtoon)")
    e1 = s._create_entry(a, "v57-thumb-a"); s._upsert_match(e1, "v57-thumb-a", a, 1.0)
    e2 = s._create_entry(b, "v57-thumb-b"); s._upsert_match(e2, "v57-thumb-b", b, 1.0)
    with db.catalog() as c:
        for subject in ("source:v57-thumb-a:57201", "source:v57-thumb-b:57202"):
            c.execute("INSERT OR REPLACE INTO thumbnail_fingerprints(subject_key,fingerprint,width,height,source_url,updated_at) VALUES(?,?,?,?,?,?)",
                      (subject, "same-exact-pixels", 120, 160, "", now_iso()))
    s._recompute_entry_media_mode(e1); s._recompute_entry_media_mode(e2)
    assert s._entries_have_media_conflict(e1, e2) is True
    evidence = s._auto_merge_evidence(e1, e2)
    assert evidence["eligible"] is False
    assert "manga/webtoon" in evidence["reason"]


def test_exact_thumbnail_can_authorize_duplicate_with_weak_title_corroboration():
    s = AutoSourcarrService(); source_row("v57-thumb-pos-a", "A", 10); source_row("v57-thumb-pos-b", "B", 20)
    a = manga(57301, "Perfect Cover Work"); b = manga(57302, "Perfect Cover Work")
    e1 = s._create_entry(a, "v57-thumb-pos-a"); s._upsert_match(e1, "v57-thumb-pos-a", a, 1.0)
    e2 = s._create_entry(b, "v57-thumb-pos-b"); s._upsert_match(e2, "v57-thumb-pos-b", b, 1.0)
    with db.catalog() as c:
        for subject in ("source:v57-thumb-pos-a:57301", "source:v57-thumb-pos-b:57302"):
            c.execute("INSERT OR REPLACE INTO thumbnail_fingerprints(subject_key,fingerprint,width,height,source_url,updated_at) VALUES(?,?,?,?,?,?)",
                      (subject, "same-positive-exact-pixels", 120, 160, "", now_iso()))
    evidence = s._auto_merge_evidence(e1, e2)
    assert evidence["eligible"] is True
    assert evidence["exact_thumbnail_match"] is True
    assert "thumbnail" in evidence["reason"]
