import json
import os
import sqlite3
import tempfile
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from .config import settings
from .reimagined_state import SCHEMA as REIMAGINED_SCHEMA


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


CATALOG_SCHEMA = r'''
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    preferred_title TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    thumbnail_url TEXT NOT NULL DEFAULT '',
    status INTEGER NOT NULL DEFAULT 0,
    author TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    genre TEXT NOT NULL DEFAULT '',
    manual_preferred_title INTEGER NOT NULL DEFAULT 0,
    color_mode TEXT NOT NULL DEFAULT 'unknown',
    color_confidence REAL NOT NULL DEFAULT 0,
    media_mode TEXT NOT NULL DEFAULT 'unknown',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
-- Preferred titles have their own exact normalized lookup; they remain outside
-- the alternate-title table. Triggers also cover legacy direct SQL title edits.
CREATE TABLE IF NOT EXISTS entry_preferred_titles (
    entry_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
    raw_title TEXT NOT NULL,
    normalized TEXT
);
CREATE INDEX IF NOT EXISTS idx_entry_preferred_titles_normalized ON entry_preferred_titles(normalized);
CREATE TRIGGER IF NOT EXISTS entry_preferred_titles_insert AFTER INSERT ON entries
BEGIN
    INSERT OR REPLACE INTO entry_preferred_titles(entry_id,raw_title,normalized)
    VALUES(NEW.id,NEW.preferred_title,NULL);
END;
CREATE TRIGGER IF NOT EXISTS entry_preferred_titles_update AFTER UPDATE OF preferred_title ON entries
WHEN OLD.preferred_title IS NOT NEW.preferred_title
BEGIN
    INSERT OR REPLACE INTO entry_preferred_titles(entry_id,raw_title,normalized)
    VALUES(NEW.id,NEW.preferred_title,NULL);
END;
CREATE TRIGGER IF NOT EXISTS entry_preferred_titles_delete AFTER DELETE ON entries
BEGIN
    DELETE FROM entry_preferred_titles WHERE entry_id=OLD.id;
END;
-- Tags retain their original namespace/spelling. Decisions survive normal rebuilds;
-- record links follow source identity rather than a mutable merged entry id.
CREATE TABLE IF NOT EXISTS tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    normalized TEXT NOT NULL,
    UNIQUE(namespace, normalized)
);
CREATE TABLE IF NOT EXISTS source_record_tags (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY(source_id, source_manga_id, tag_id)
);
CREATE INDEX IF NOT EXISTS idx_source_record_tags_tag ON source_record_tags(tag_id);
CREATE TABLE IF NOT EXISTS gallery_records (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    metadata_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE IF NOT EXISTS tag_reviews (
    left_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    right_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    decision TEXT NOT NULL CHECK(decision IN ('same','different')),
    updated_at TEXT NOT NULL,
    PRIMARY KEY(left_id, right_id),
    CHECK(left_id < right_id)
);
CREATE INDEX IF NOT EXISTS idx_tag_reviews_right ON tag_reviews(right_id);
CREATE TABLE IF NOT EXISTS titles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    normalized TEXT NOT NULL,
    language TEXT NOT NULL DEFAULT '',
    title_type TEXT NOT NULL DEFAULT 'alias',
    source_id TEXT NOT NULL DEFAULT '',
    source_manga_id INTEGER NOT NULL DEFAULT 0,
    UNIQUE(entry_id, normalized, source_id, title_type)
);
CREATE INDEX IF NOT EXISTS idx_entries_updated_id ON entries(updated_at,id);
CREATE INDEX IF NOT EXISTS idx_titles_normalized ON titles(normalized);
CREATE INDEX IF NOT EXISTS idx_titles_entry ON titles(entry_id);
CREATE INDEX IF NOT EXISTS idx_titles_entry_normalized ON titles(entry_id,normalized);
CREATE INDEX IF NOT EXISTS idx_entries_status ON entries(status);
CREATE INDEX IF NOT EXISTS idx_entries_lower_title ON entries(lower(preferred_title));
CREATE TABLE IF NOT EXISTS source_config (
    source_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    display_name TEXT NOT NULL DEFAULT '',
    lang TEXT NOT NULL DEFAULT '',
    base_url TEXT NOT NULL DEFAULT '',
    icon_url TEXT NOT NULL DEFAULT '',
    supports_latest INTEGER NOT NULL DEFAULT 1,
    enabled_language INTEGER NOT NULL DEFAULT 1,
    enabled_search INTEGER NOT NULL DEFAULT 1,
    enabled_chapters INTEGER NOT NULL DEFAULT 1,
    priority INTEGER NOT NULL DEFAULT 1000,
    last_seen TEXT NOT NULL,
    available INTEGER NOT NULL DEFAULT 1,
    group_id INTEGER,
    official INTEGER NOT NULL DEFAULT 0,
    official_manual INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS source_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    priority INTEGER NOT NULL DEFAULT 1000,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS source_health (
    source_id TEXT PRIMARY KEY,
    state TEXT NOT NULL DEFAULT 'unchecked',
    message TEXT NOT NULL DEFAULT '',
    checked_at REAL NOT NULL DEFAULT 0,
    started_at REAL NOT NULL DEFAULT 0,
    last_success_at REAL NOT NULL DEFAULT 0,
    last_repaired_at REAL NOT NULL DEFAULT 0,
    last_changed_at REAL NOT NULL DEFAULT 0,
    next_check_at REAL NOT NULL DEFAULT 0,
    package_name TEXT NOT NULL DEFAULT '',
    checked_version TEXT NOT NULL DEFAULT '',
    repair_job_id TEXT NOT NULL DEFAULT '',
    checks_json TEXT NOT NULL DEFAULT '[]',
    history_json TEXT NOT NULL DEFAULT '[]'
);
-- Do not create idx_source_config_group here. On an upgrade, CREATE TABLE IF NOT
-- EXISTS leaves an older source_config table unchanged, so group_id may not exist
-- until _migrate() adds it. The migration creates the index immediately afterward.
CREATE TABLE IF NOT EXISTS source_matches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    source_manga_url TEXT NOT NULL DEFAULT '',
    source_title TEXT NOT NULL,
    confidence REAL NOT NULL DEFAULT 0,
    match_type TEXT NOT NULL DEFAULT 'auto',
    locked INTEGER NOT NULL DEFAULT 0,
    active INTEGER NOT NULL DEFAULT 1,
    initialized INTEGER NOT NULL DEFAULT 0,
    thumbnail_url TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    author TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    genre TEXT NOT NULL DEFAULT '',
    status INTEGER NOT NULL DEFAULT 0,
    real_url TEXT NOT NULL DEFAULT '',
    metadata_json TEXT NOT NULL DEFAULT '{}',
    last_checked TEXT NOT NULL,
    UNIQUE(entry_id, source_id)
);
CREATE INDEX IF NOT EXISTS idx_source_match_manga_lookup ON source_matches(source_manga_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_source_manga_unique ON source_matches(source_id, source_manga_id) WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_matches_entry ON source_matches(entry_id, active);
CREATE INDEX IF NOT EXISTS idx_matches_entry_source_active ON source_matches(entry_id,source_id,active);

CREATE INDEX IF NOT EXISTS idx_matches_source_title ON source_matches(source_id,source_title COLLATE NOCASE) WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_matches_lower_title ON source_matches(lower(source_title),entry_id) WHERE active=1;
CREATE TABLE IF NOT EXISTS source_discovery_outcomes (
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT '',
    message TEXT NOT NULL DEFAULT '',
    candidate_entry_id INTEGER,
    checked_at TEXT NOT NULL,
    PRIMARY KEY(entry_id,source_id)
);

CREATE TABLE IF NOT EXISTS source_color_profiles (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    color_mode TEXT NOT NULL DEFAULT 'unknown',
    confidence REAL NOT NULL DEFAULT 0,
    sampled_pages INTEGER NOT NULL DEFAULT 0,
    evidence TEXT NOT NULL DEFAULT '',
    colored_fraction REAL NOT NULL DEFAULT 0,
    analyzed_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE INDEX IF NOT EXISTS idx_color_profiles_mode ON source_color_profiles(color_mode, confidence);
CREATE TABLE IF NOT EXISTS source_variants (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    source_manga_url TEXT NOT NULL DEFAULT '',
    source_title TEXT NOT NULL DEFAULT '',
    confidence REAL NOT NULL DEFAULT 0,
    match_type TEXT NOT NULL DEFAULT 'auto',
    locked INTEGER NOT NULL DEFAULT 0,
    active INTEGER NOT NULL DEFAULT 1,
    initialized INTEGER NOT NULL DEFAULT 0,
    thumbnail_url TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    author TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    genre TEXT NOT NULL DEFAULT '',
    status INTEGER NOT NULL DEFAULT 0,
    real_url TEXT NOT NULL DEFAULT '',
    metadata_json TEXT NOT NULL DEFAULT '{}',
    last_checked TEXT NOT NULL,
    UNIQUE(source_id, source_manga_id)
);
CREATE INDEX IF NOT EXISTS idx_variants_source_title ON source_variants(source_id,source_title COLLATE NOCASE) WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_variants_lower_title ON source_variants(lower(source_title),entry_id) WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_source_variants_entry ON source_variants(entry_id, active);
CREATE INDEX IF NOT EXISTS idx_source_variants_entry_source_active ON source_variants(entry_id,source_id,active);
CREATE INDEX IF NOT EXISTS idx_source_variant_manga_lookup ON source_variants(source_manga_id);
CREATE TABLE IF NOT EXISTS entry_aliases (
    old_entry_id INTEGER PRIMARY KEY,
    canonical_entry_id INTEGER NOT NULL,
    reason TEXT NOT NULL DEFAULT '',
    merged_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_entry_alias_canonical ON entry_aliases(canonical_entry_id);
-- The catalogue commit owns the intent; origins are replayed and acknowledged
-- separately. Do not reference absorbed entry ids with cascading foreign keys.
CREATE TABLE IF NOT EXISTS pending_entry_merges (
    duplicate_entry_id INTEGER PRIMARY KEY,
    target_entry_id INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    last_error TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS pending_entry_splits (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    original_entry_id INTEGER NOT NULL,
    new_entry_id INTEGER NOT NULL,
    records_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    last_error TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS deleted_source_records (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    source_title TEXT NOT NULL DEFAULT '',
    deleted_entry_title TEXT NOT NULL DEFAULT '',
    deleted_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE IF NOT EXISTS deleted_entries_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    old_entry_id INTEGER NOT NULL,
    preferred_title TEXT NOT NULL DEFAULT '',
    source_records INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS match_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    old_source_manga_id INTEGER,
    new_source_manga_id INTEGER,
    change_type TEXT NOT NULL,
    changed_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_match_history_source_id ON match_history(source_id,id);
CREATE TABLE IF NOT EXISTS browse_cache (
    cache_key TEXT PRIMARY KEY,
    mode TEXT NOT NULL,
    page INTEGER NOT NULL,
    query TEXT NOT NULL DEFAULT '',
    source_ids_json TEXT NOT NULL DEFAULT '[]',
    min_matches INTEGER NOT NULL DEFAULT 1,
    exact_match_count INTEGER,
    sort TEXT NOT NULL DEFAULT 'relevance',
    entry_ids_json TEXT NOT NULL DEFAULT '[]',
    has_next_page INTEGER NOT NULL DEFAULT 0,
    source_errors_json TEXT NOT NULL DEFAULT '[]',
    updated_at TEXT NOT NULL,
    last_accessed TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_browse_cache_accessed ON browse_cache(last_accessed);
CREATE TABLE IF NOT EXISTS translation_cache (
    source_text TEXT NOT NULL,
    source_lang TEXT NOT NULL DEFAULT 'auto',
    target_lang TEXT NOT NULL,
    translated_text TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_text, target_lang)
);
CREATE INDEX IF NOT EXISTS idx_translation_target ON translation_cache(target_lang);
CREATE TABLE IF NOT EXISTS duplicate_reviews (
    left_entry_id INTEGER NOT NULL,
    right_entry_id INTEGER NOT NULL,
    decision TEXT NOT NULL,
    reviewed_at TEXT NOT NULL,
    PRIMARY KEY(left_entry_id, right_entry_id)
);
CREATE INDEX IF NOT EXISTS idx_duplicate_reviews_decision ON duplicate_reviews(decision);
CREATE TABLE IF NOT EXISTS duplicate_split_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    original_entry_id INTEGER NOT NULL,
    new_entry_id INTEGER NOT NULL,
    records_json TEXT NOT NULL DEFAULT '[]',
    split_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_duplicate_split_original ON duplicate_split_history(original_entry_id, split_at);
CREATE TABLE IF NOT EXISTS automatic_merge_lineage (
    canonical_entry_id INTEGER NOT NULL,
    member_entry_id INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL DEFAULT '{}',
    merged_at TEXT NOT NULL,
    PRIMARY KEY(canonical_entry_id, member_entry_id)
);
CREATE INDEX IF NOT EXISTS idx_auto_merge_lineage_member ON automatic_merge_lineage(member_entry_id);
CREATE TABLE IF NOT EXISTS automatic_merge_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    target_entry_id INTEGER NOT NULL,
    duplicate_entry_id INTEGER NOT NULL,
    target_snapshot_json TEXT NOT NULL DEFAULT '{}',
    duplicate_snapshot_json TEXT NOT NULL DEFAULT '{}',
    reason TEXT NOT NULL DEFAULT '',
    merged_at TEXT NOT NULL,
    repaired_at TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS matching_policies (
    scope TEXT NOT NULL CHECK(scope IN ('source','group')),
    subject TEXT NOT NULL,
    direction TEXT NOT NULL CHECK(direction IN ('internal','external')),
    rule_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(scope,subject,direction)
);
CREATE TABLE IF NOT EXISTS matching_presets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    rule_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auto_merge_events_target ON automatic_merge_events(target_entry_id, merged_at);
CREATE INDEX IF NOT EXISTS idx_auto_merge_events_pair ON automatic_merge_events(target_entry_id, duplicate_entry_id);
CREATE INDEX IF NOT EXISTS idx_auto_merge_events_compact_history ON automatic_merge_events(
    merged_at DESC,id DESC,target_entry_id,duplicate_entry_id,reason,repaired_at,
    CASE WHEN json_valid(target_snapshot_json) THEN json_extract(target_snapshot_json,'$.entry.preferred_title') END,
    CASE WHEN json_valid(duplicate_snapshot_json) THEN json_extract(duplicate_snapshot_json,'$.entry.preferred_title') END);
CREATE TABLE IF NOT EXISTS duplicate_review_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    left_entry_id INTEGER NOT NULL,
    right_entry_id INTEGER NOT NULL,
    decision TEXT NOT NULL,
    reviewed_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS mixed_entry_reviews (
    fingerprint TEXT PRIMARY KEY,
    entry_id_hint INTEGER NOT NULL,
    reviewed_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS user_ui_preferences (
    user_key TEXT PRIMARY KEY,
    values_json TEXT NOT NULL DEFAULT '{}',
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS preferred_title_overrides (
    entry_id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS manual_title_scopes (
    entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    PRIMARY KEY(entry_id, source_id, source_manga_id)
);
CREATE INDEX IF NOT EXISTS idx_manual_title_scopes_source ON manual_title_scopes(source_id,source_manga_id);
CREATE TABLE IF NOT EXISTS import_runs (
    id TEXT PRIMARY KEY,
    filename TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL DEFAULT 'running',
    total INTEGER NOT NULL DEFAULT 0,
    added INTEGER NOT NULL DEFAULT 0,
    existing INTEGER NOT NULL DEFAULT 0,
    unresolved INTEGER NOT NULL DEFAULT 0,
    failed INTEGER NOT NULL DEFAULT 0,
    skipped INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    completed_at TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_import_runs_created ON import_runs(created_at);
CREATE TABLE IF NOT EXISTS import_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    import_id TEXT NOT NULL REFERENCES import_runs(id) ON DELETE CASCADE,
    position INTEGER NOT NULL DEFAULT 0,
    title TEXT NOT NULL DEFAULT '',
    backup_source TEXT NOT NULL DEFAULT '',
    source_id TEXT NOT NULL DEFAULT '',
    source_url TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL DEFAULT 'unresolved',
    reason TEXT NOT NULL DEFAULT '',
    entry_id INTEGER,
    raw_json TEXT NOT NULL DEFAULT '{}',
    candidate_ids_json TEXT NOT NULL DEFAULT '[]',
    english_title TEXT NOT NULL DEFAULT '',
    reference_entry_id INTEGER,
    reference_title TEXT NOT NULL DEFAULT '',
    reference_description TEXT NOT NULL DEFAULT '',
    reference_thumbnail_url TEXT NOT NULL DEFAULT '',
    translation_checked INTEGER NOT NULL DEFAULT 0,
    suggestion_checked INTEGER NOT NULL DEFAULT 0,
    suggestion_state TEXT NOT NULL DEFAULT 'pending',
    matched_snapshot_json TEXT NOT NULL DEFAULT '{}',
    updated_at TEXT NOT NULL,
    UNIQUE(import_id, position)
);
CREATE INDEX IF NOT EXISTS idx_import_items_run_state ON import_items(import_id,state,position);
CREATE TABLE IF NOT EXISTS import_match_rejections (
    backup_key TEXT NOT NULL,
    signature TEXT NOT NULL,
    backup_title TEXT NOT NULL DEFAULT '',
    backup_source TEXT NOT NULL DEFAULT '',
    rejected_title TEXT NOT NULL DEFAULT '',
    rejected_at TEXT NOT NULL,
    PRIMARY KEY(backup_key, signature)
);
CREATE INDEX IF NOT EXISTS idx_import_match_rejections_key ON import_match_rejections(backup_key);
CREATE TABLE IF NOT EXISTS import_manual_autoresolve (
    backup_key TEXT PRIMARY KEY,
    backup_title TEXT NOT NULL DEFAULT '',
    backup_source TEXT NOT NULL DEFAULT '',
    backup_source_id TEXT NOT NULL DEFAULT '',
    backup_source_url TEXT NOT NULL DEFAULT '',
    target_source_id TEXT NOT NULL DEFAULT '',
    target_source_manga_id INTEGER NOT NULL DEFAULT 0,
    target_source_name TEXT NOT NULL DEFAULT '',
    target_title TEXT NOT NULL DEFAULT '',
    target_entry_id_hint INTEGER,
    target_record_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_import_manual_autoresolve_target ON import_manual_autoresolve(target_source_id,target_source_manga_id);
CREATE TABLE IF NOT EXISTS import_manual_autoresolve_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS preferred_title_override_sources (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE IF NOT EXISTS manual_match_groups (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS manual_match_bindings (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    group_id TEXT,
    grouping_suppressed INTEGER NOT NULL DEFAULT 0,
    entry_id_hint INTEGER,
    source_name TEXT NOT NULL DEFAULT '',
    source_title TEXT NOT NULL DEFAULT '',
    record_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id),
    FOREIGN KEY(group_id) REFERENCES manual_match_groups(id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS idx_manual_bindings_group ON manual_match_bindings(group_id);
CREATE INDEX IF NOT EXISTS idx_manual_bindings_entry_hint ON manual_match_bindings(entry_id_hint);
CREATE TABLE IF NOT EXISTS manual_separate_records (
    target_entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    separate_entry_id_hint INTEGER,
    source_title TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(target_entry_id, source_id, source_manga_id)
);
CREATE INDEX IF NOT EXISTS idx_manual_separate_source ON manual_separate_records(source_id, source_manga_id);

CREATE TABLE IF NOT EXISTS identity_links (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    provider TEXT NOT NULL,
    provider_name TEXT NOT NULL DEFAULT '',
    external_id TEXT NOT NULL,
    external_url TEXT NOT NULL DEFAULT '',
    identity_scope TEXT NOT NULL DEFAULT 'work',
    confidence REAL NOT NULL DEFAULT 0,
    match_type TEXT NOT NULL DEFAULT 'auto',
    locked INTEGER NOT NULL DEFAULT 0,
    title TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    thumbnail_url TEXT NOT NULL DEFAULT '',
    status INTEGER NOT NULL DEFAULT 0,
    author TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    genre TEXT NOT NULL DEFAULT '',
    media_type TEXT NOT NULL DEFAULT '',
    country TEXT NOT NULL DEFAULT '',
    year INTEGER,
    adult INTEGER NOT NULL DEFAULT 0,
    metadata_json TEXT NOT NULL DEFAULT '{}',
    last_checked TEXT NOT NULL,
    UNIQUE(entry_id, provider)
);
CREATE INDEX IF NOT EXISTS idx_identity_provider_external ON identity_links(provider, external_id);
CREATE INDEX IF NOT EXISTS idx_identity_entry ON identity_links(entry_id);
CREATE INDEX IF NOT EXISTS idx_identity_provider ON identity_links(provider);

CREATE TABLE IF NOT EXISTS thumbnail_fingerprints (
    subject_key TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL DEFAULT '',
    width INTEGER NOT NULL DEFAULT 0,
    height INTEGER NOT NULL DEFAULT 0,
    source_url TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_thumbnail_fingerprint_value ON thumbnail_fingerprints(fingerprint);

CREATE TABLE IF NOT EXISTS metadata_query_cache (
    provider TEXT NOT NULL,
    query_key TEXT NOT NULL,
    query_text TEXT NOT NULL DEFAULT '',
    results_json TEXT NOT NULL DEFAULT '[]',
    updated_at TEXT NOT NULL,
    PRIMARY KEY(provider, query_key)
);
CREATE INDEX IF NOT EXISTS idx_metadata_query_cache_updated ON metadata_query_cache(updated_at);
'''

ORIGINS_SCHEMA = r'''
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS virtual_chapters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL,
    chapter_key TEXT NOT NULL,
    chapter_number REAL NOT NULL DEFAULT -1,
    volume_number REAL NOT NULL DEFAULT -1,
    display_name TEXT NOT NULL,
    hidden INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(entry_id, chapter_key)
);
CREATE INDEX IF NOT EXISTS idx_virtual_entry ON virtual_chapters(entry_id, hidden, chapter_number);
CREATE TABLE IF NOT EXISTS chapter_candidates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    virtual_chapter_id INTEGER NOT NULL REFERENCES virtual_chapters(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    source_name TEXT NOT NULL,
    source_match_id INTEGER,
    source_manga_id INTEGER NOT NULL,
    source_chapter_id INTEGER NOT NULL,
    source_chapter_url TEXT NOT NULL DEFAULT '',
    source_order INTEGER NOT NULL DEFAULT 0,
    source_chapter_index INTEGER NOT NULL DEFAULT -1,
    name TEXT NOT NULL,
    chapter_number REAL NOT NULL DEFAULT -1,
    volume_number REAL NOT NULL DEFAULT -1,
    upload_date INTEGER NOT NULL DEFAULT 0,
    scanlator TEXT NOT NULL DEFAULT '',
    page_count INTEGER NOT NULL DEFAULT 0,
    quality_score REAL,
    active INTEGER NOT NULL DEFAULT 1,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    UNIQUE(source_id, source_chapter_id)
);
CREATE INDEX IF NOT EXISTS idx_candidate_chapter_lookup ON chapter_candidates(source_chapter_id);
CREATE INDEX IF NOT EXISTS idx_candidate_virtual ON chapter_candidates(virtual_chapter_id, active);
CREATE INDEX IF NOT EXISTS idx_candidate_source_manga_active ON chapter_candidates(source_id,source_manga_id,active);
CREATE INDEX IF NOT EXISTS idx_candidate_virtual_source_active ON chapter_candidates(virtual_chapter_id,source_id,active);
CREATE TABLE IF NOT EXISTS source_record_scans (
    entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    checked_at TEXT NOT NULL,
    chapter_count INTEGER NOT NULL DEFAULT 0,
    error TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(entry_id, source_id, source_manga_id)
);
CREATE INDEX IF NOT EXISTS idx_source_record_scans_entry ON source_record_scans(entry_id,source_id);
CREATE TABLE IF NOT EXISTS candidate_pages (
    candidate_id INTEGER NOT NULL REFERENCES chapter_candidates(id) ON DELETE CASCADE,
    page_index INTEGER NOT NULL,
    private_url TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(candidate_id, page_index)
);
CREATE TABLE IF NOT EXISTS chapter_candidate_rejections (
    source_id TEXT NOT NULL,
    source_chapter_id INTEGER NOT NULL,
    source_manga_id INTEGER NOT NULL DEFAULT 0,
    source_name TEXT NOT NULL DEFAULT '',
    name TEXT NOT NULL DEFAULT '',
    rejected_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_chapter_id)
);
CREATE INDEX IF NOT EXISTS idx_candidate_rejections_manga ON chapter_candidate_rejections(source_id,source_manga_id);
CREATE TABLE IF NOT EXISTS served_origin (
    virtual_chapter_id INTEGER PRIMARY KEY REFERENCES virtual_chapters(id) ON DELETE CASCADE,
    candidate_id INTEGER NOT NULL REFERENCES chapter_candidates(id),
    selected_at TEXT NOT NULL,
    reason TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_served_origin_candidate ON served_origin(candidate_id);
CREATE TABLE IF NOT EXISTS origin_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    virtual_chapter_id INTEGER NOT NULL,
    candidate_id INTEGER,
    source_id TEXT NOT NULL,
    source_name TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    source_chapter_id INTEGER NOT NULL,
    served_at TEXT NOT NULL,
    reason TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_origin_entry ON origin_history(virtual_chapter_id, served_at);
CREATE TABLE IF NOT EXISTS quality_samples (
    candidate_id INTEGER PRIMARY KEY REFERENCES chapter_candidates(id) ON DELETE CASCADE,
    sampled_pages INTEGER NOT NULL,
    broken_pages INTEGER NOT NULL,
    median_width INTEGER NOT NULL,
    median_height INTEGER NOT NULL,
    avg_bytes INTEGER NOT NULL,
    phash_first TEXT NOT NULL DEFAULT '',
    phash_last TEXT NOT NULL DEFAULT '',
    analyzed_at TEXT NOT NULL
);
'''


# Stamped in both SQLite files after migrations (same values in the C# SchemaMigrator).
# A newer schema is refused instead of being opened by an older build, which
# column-presence migrations alone cannot detect. Bump REVISION with any
# schema change that an older build must not open.
SCHEMA_APPLICATION_ID = 0x41534331  # "ASC1"
SCHEMA_REVISION = 1


class SchemaRevisionError(RuntimeError):
    pass


def check_schema_revision(path) -> None:
    if not os.path.isfile(str(path)):
        return
    conn = sqlite3.connect(f"file:{Path(path).resolve().as_posix()}?mode=ro", uri=True, timeout=30)
    try:
        application_id = int(conn.execute("PRAGMA application_id").fetchone()[0])
        revision = int(conn.execute("PRAGMA user_version").fetchone()[0])
    finally:
        conn.close()
    if application_id not in (0, SCHEMA_APPLICATION_ID):
        raise SchemaRevisionError(f"{path} is not an AutoSourcarr database (application_id {application_id})")
    if revision > SCHEMA_REVISION:
        raise SchemaRevisionError(
            f"{path} uses schema revision {revision}; this build supports up to {SCHEMA_REVISION}. "
            "Run the newer AutoSourcarr image or restore a matching backup; the file was not changed.")


def stamp_schema_revision(path) -> None:
    conn = sqlite3.connect(str(path), timeout=30)
    try:
        conn.execute(f"PRAGMA application_id={SCHEMA_APPLICATION_ID}")
        conn.execute(f"PRAGMA user_version={SCHEMA_REVISION}")
        conn.commit()
    finally:
        conn.close()


class Database:
    def __init__(self) -> None:
        # Most requests use many small indexed SQLite reads. Opening/configuring a
        # connection for every one of them was measurable overhead and also threw
        # away SQLite's page cache immediately. Reuse one connection per worker
        # thread; nested contexts use savepoints without committing outer work.
        self._local = threading.local()
        # Normal database access remains fully concurrent. The condition is only
        # used for the very short cut-over window of an in-app database move: new
        # contexts wait, already-running transactions drain, then every thread
        # transparently reopens its connection against the new path on next use.
        self._relocation_condition = threading.Condition()
        self._database_relocating = False
        self._active_db_contexts = 0
        check_schema_revision(settings.catalog_path)
        check_schema_revision(settings.origins_path)
        self._init_db(settings.catalog_path, CATALOG_SCHEMA + REIMAGINED_SCHEMA)
        self._init_db(settings.origins_path, ORIGINS_SCHEMA)
        self._migrate()
        for path in (settings.catalog_path, settings.origins_path):
            stamp_schema_revision(path)

    @staticmethod
    def _configure_connection(conn: sqlite3.Connection) -> sqlite3.Connection:
        # WAL + NORMAL is the best trade-off for this single-host cache/database:
        # readers do not block the background writers, while durability still covers
        # process/container crashes. A modest SQLite page cache avoids repeated disk I/O.
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute(f"PRAGMA busy_timeout={max(250, int(settings.sqlite_busy_timeout_ms))}")
        conn.execute("PRAGMA synchronous=NORMAL")
        conn.execute("PRAGMA temp_store=MEMORY")
        conn.execute(f"PRAGMA cache_size={-max(4, int(settings.sqlite_cache_mb)) * 1024}")
        conn.execute("PRAGMA mmap_size=268435456")
        return conn

    @staticmethod
    def refresh_preferred_title_index(conn: sqlite3.Connection) -> None:
        from .matcher import normalize_title
        pending = conn.execute("SELECT entry_id,raw_title FROM entry_preferred_titles WHERE normalized IS NULL").fetchall()
        if pending:
            conn.executemany("""UPDATE entry_preferred_titles SET normalized=?
                WHERE entry_id=? AND raw_title=? AND normalized IS NULL""",
                [(normalize_title(str(row["raw_title"] or "")), int(row["entry_id"]), row["raw_title"]) for row in pending])

    @classmethod
    def _connect(cls, path: str | Any) -> sqlite3.Connection:
        conn = sqlite3.connect(path, timeout=30)
        try:
            conn.row_factory = sqlite3.Row
            return cls._configure_connection(conn)
        except BaseException:
            try:
                conn.close()
            except Exception:
                pass  # Preserve the original setup failure.
            raise

    @classmethod
    def _init_db(cls, path, schema: str) -> None:
        conn = cls._connect(path)
        try:
            conn.executescript(schema)
            conn.execute("PRAGMA optimize")
            conn.commit()
        finally:
            conn.close()


    def _migrate(self) -> None:
        # Forward-compatible, idempotent migrations for early AutoSourcarr builds.
        conn = self._connect(settings.catalog_path)
        try:
            # Own one transaction per SQLite file, including ALTER and its guarded
            # backfill. A failed backfill must not leave a default-filled column
            # whose presence would cause the next constructor to skip the repair.
            conn.execute("BEGIN IMMEDIATE")
            cols = {row[1] for row in conn.execute("PRAGMA table_info(source_config)")}
            if "enabled_language" not in cols:
                conn.execute("ALTER TABLE source_config ADD COLUMN enabled_language INTEGER NOT NULL DEFAULT 1")
                # Preserve the v0.1.0 effective language selection: a language variant
                # was effectively selected when either discovery or serving was enabled.
                conn.execute(
                    "UPDATE source_config SET enabled_language=CASE WHEN enabled_search=1 OR enabled_chapters=1 THEN 1 ELSE 0 END"
                )
            if "available" not in cols:
                conn.execute("ALTER TABLE source_config ADD COLUMN available INTEGER NOT NULL DEFAULT 1")
            if "group_id" not in cols:
                conn.execute("ALTER TABLE source_config ADD COLUMN group_id INTEGER")
            if "official" not in cols:
                conn.execute("ALTER TABLE source_config ADD COLUMN official INTEGER NOT NULL DEFAULT 0")
            if "official_manual" not in cols:
                conn.execute("ALTER TABLE source_config ADD COLUMN official_manual INTEGER NOT NULL DEFAULT 0")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS source_groups (
                       id INTEGER PRIMARY KEY AUTOINCREMENT,
                       name TEXT NOT NULL UNIQUE, priority INTEGER NOT NULL DEFAULT 1000,
                       created_at TEXT NOT NULL, updated_at TEXT NOT NULL
                   )"""
            )
            group_cols = {row[1] for row in conn.execute("PRAGMA table_info(source_groups)")}
            if "priority" not in group_cols:
                conn.execute("ALTER TABLE source_groups ADD COLUMN priority INTEGER NOT NULL DEFAULT 1000")
                # Preserve the effective priority users had before group priority became
                # explicit.  The best (lowest numeric) member priority becomes the group
                # priority, then every member is synchronized to it.
                conn.execute(
                    """UPDATE source_groups SET priority=COALESCE(
                           (SELECT MIN(sc.priority) FROM source_config sc WHERE sc.group_id=source_groups.id),
                           priority, 1000)"""
                )
            conn.execute(
                """UPDATE source_config SET priority=(
                       SELECT sg.priority FROM source_groups sg WHERE sg.id=source_config.group_id)
                       WHERE group_id IS NOT NULL AND EXISTS(
                           SELECT 1 FROM source_groups sg WHERE sg.id=source_config.group_id)"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_source_config_group ON source_config(group_id)")

            entry_cols = {row[1] for row in conn.execute("PRAGMA table_info(entries)")}
            if "color_mode" not in entry_cols:
                conn.execute("ALTER TABLE entries ADD COLUMN color_mode TEXT NOT NULL DEFAULT 'unknown'")
            if "color_confidence" not in entry_cols:
                conn.execute("ALTER TABLE entries ADD COLUMN color_confidence REAL NOT NULL DEFAULT 0")
            if "media_mode" not in entry_cols:
                conn.execute("ALTER TABLE entries ADD COLUMN media_mode TEXT NOT NULL DEFAULT 'unknown'")
            # Portable SQL triggers mark edits dirty without requiring a Python
            # function in older app or external SQLite connections. Fill only
            # missing rows; CAS normalization cannot overwrite a concurrent rename.
            conn.execute("""INSERT OR IGNORE INTO entry_preferred_titles(entry_id,raw_title,normalized)
                SELECT e.id,e.preferred_title,NULL FROM entries e
                WHERE NOT EXISTS(SELECT 1 FROM entry_preferred_titles p WHERE p.entry_id=e.id)""")
            self.refresh_preferred_title_index(conn)
            conn.execute(
                """CREATE TABLE IF NOT EXISTS source_color_profiles (
                       source_id TEXT NOT NULL,
                       source_manga_id INTEGER NOT NULL,
                       color_mode TEXT NOT NULL DEFAULT 'unknown',
                       confidence REAL NOT NULL DEFAULT 0,
                       sampled_pages INTEGER NOT NULL DEFAULT 0,
                       evidence TEXT NOT NULL DEFAULT '',
                       colored_fraction REAL NOT NULL DEFAULT 0,
                       analyzed_at TEXT NOT NULL,
                       PRIMARY KEY(source_id, source_manga_id)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_color_profiles_mode ON source_color_profiles(color_mode, confidence)")

            title_cols = {row[1] for row in conn.execute("PRAGMA table_info(titles)")}
            if "source_manga_id" not in title_cols:
                conn.execute("ALTER TABLE titles ADD COLUMN source_manga_id INTEGER NOT NULL DEFAULT 0")
                # Best-effort provenance for old rows. Exact per-record provenance
                # is recorded for all new/updated titles from v0.2.15 onward.
                conn.execute(
                    """UPDATE titles SET source_manga_id=COALESCE((
                           SELECT sm.source_manga_id FROM source_matches sm
                           WHERE sm.entry_id=titles.entry_id AND sm.source_id=titles.source_id AND sm.active=1
                           LIMIT 1),0)
                       WHERE source_manga_id=0 AND source_id<>''"""
                )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS duplicate_split_history (
                       id INTEGER PRIMARY KEY AUTOINCREMENT,
                       original_entry_id INTEGER NOT NULL,
                       new_entry_id INTEGER NOT NULL,
                       records_json TEXT NOT NULL DEFAULT '[]',
                       split_at TEXT NOT NULL
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_duplicate_split_original ON duplicate_split_history(original_entry_id, split_at)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS automatic_merge_lineage (
                       canonical_entry_id INTEGER NOT NULL, member_entry_id INTEGER NOT NULL,
                       snapshot_json TEXT NOT NULL DEFAULT '{}', merged_at TEXT NOT NULL,
                       PRIMARY KEY(canonical_entry_id, member_entry_id)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_auto_merge_lineage_member ON automatic_merge_lineage(member_entry_id)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS automatic_merge_events (
                       id INTEGER PRIMARY KEY AUTOINCREMENT, target_entry_id INTEGER NOT NULL, duplicate_entry_id INTEGER NOT NULL,
                       target_snapshot_json TEXT NOT NULL DEFAULT '{}', duplicate_snapshot_json TEXT NOT NULL DEFAULT '{}',
                       reason TEXT NOT NULL DEFAULT '', merged_at TEXT NOT NULL, repaired_at TEXT NOT NULL DEFAULT ''
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_auto_merge_events_target ON automatic_merge_events(target_entry_id, merged_at)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_auto_merge_events_pair ON automatic_merge_events(target_entry_id, duplicate_entry_id)")
            conn.execute("""CREATE INDEX IF NOT EXISTS idx_auto_merge_events_compact_history ON automatic_merge_events(
                merged_at DESC,id DESC,target_entry_id,duplicate_entry_id,reason,repaired_at,
                CASE WHEN json_valid(target_snapshot_json) THEN json_extract(target_snapshot_json,'$.entry.preferred_title') END,
                CASE WHEN json_valid(duplicate_snapshot_json) THEN json_extract(duplicate_snapshot_json,'$.entry.preferred_title') END)""")
            if 'evidence_json' not in {r[1] for r in conn.execute('PRAGMA table_info(automatic_merge_events)')}:
                conn.execute("ALTER TABLE automatic_merge_events ADD COLUMN evidence_json TEXT NOT NULL DEFAULT '{}'")

            conn.execute(
                """CREATE TABLE IF NOT EXISTS preferred_title_overrides (
                       entry_id INTEGER PRIMARY KEY,
                       title TEXT NOT NULL,
                       updated_at TEXT NOT NULL
                   )"""
            )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS preferred_title_override_sources (
                       source_id TEXT NOT NULL,
                       source_manga_id INTEGER NOT NULL,
                       title TEXT NOT NULL,
                       updated_at TEXT NOT NULL,
                       PRIMARY KEY(source_id, source_manga_id)
                   )"""
            )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS manual_title_scopes (
                       entry_id INTEGER NOT NULL, source_id TEXT NOT NULL, source_manga_id INTEGER NOT NULL,
                       title TEXT NOT NULL, captured_at TEXT NOT NULL,
                       PRIMARY KEY(entry_id, source_id, source_manga_id)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_manual_title_scopes_source ON manual_title_scopes(source_id,source_manga_id)")
            # v0.2.82: migrate the old source-keyed inheritance map into a read-only
            # snapshot of the entry/matches that existed when the title was set.
            # The legacy table is then emptied so a source record can never carry a
            # manual title into a different/new entry on a later rediscovery.
            conn.execute(
                """INSERT OR IGNORE INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at)
                   SELECT sm.entry_id,p.source_id,p.source_manga_id,p.title,p.updated_at
                     FROM preferred_title_override_sources p
                     JOIN source_matches sm ON sm.source_id=p.source_id AND sm.source_manga_id=p.source_manga_id AND sm.active=1
                     JOIN preferred_title_overrides o ON o.entry_id=sm.entry_id AND o.title=p.title
                   UNION
                   SELECT sv.entry_id,p.source_id,p.source_manga_id,p.title,p.updated_at
                     FROM preferred_title_override_sources p
                     JOIN source_variants sv ON sv.source_id=p.source_id AND sv.source_manga_id=p.source_manga_id AND sv.active=1
                     JOIN preferred_title_overrides o ON o.entry_id=sv.entry_id AND o.title=p.title"""
            )
            conn.execute("DELETE FROM preferred_title_override_sources")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS manual_match_groups (
                       id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '',
                       created_at TEXT NOT NULL, updated_at TEXT NOT NULL
                   )"""
            )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS manual_match_bindings (
                       source_id TEXT NOT NULL, source_manga_id INTEGER NOT NULL, group_id TEXT,
                       entry_id_hint INTEGER, source_name TEXT NOT NULL DEFAULT '', source_title TEXT NOT NULL DEFAULT '',
                       record_json TEXT NOT NULL DEFAULT '{}', created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                       PRIMARY KEY(source_id, source_manga_id),
                       FOREIGN KEY(group_id) REFERENCES manual_match_groups(id) ON DELETE SET NULL
                   )"""
            )
            if "grouping_suppressed" not in {r[1] for r in conn.execute("PRAGMA table_info(manual_match_bindings)")}:
                conn.execute("ALTER TABLE manual_match_bindings ADD COLUMN grouping_suppressed INTEGER NOT NULL DEFAULT 0")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_manual_bindings_group ON manual_match_bindings(group_id)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_manual_bindings_entry_hint ON manual_match_bindings(entry_id_hint)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS user_ui_preferences (
                       user_key TEXT PRIMARY KEY, values_json TEXT NOT NULL DEFAULT '{}', updated_at TEXT NOT NULL
                   )"""
            )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS thumbnail_fingerprints (
                       subject_key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL DEFAULT '', width INTEGER NOT NULL DEFAULT 0,
                       height INTEGER NOT NULL DEFAULT 0, source_url TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_thumbnail_fingerprint_value ON thumbnail_fingerprints(fingerprint)")

            conn.execute(
                """CREATE TABLE IF NOT EXISTS import_runs (
                       id TEXT PRIMARY KEY, filename TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'running',
                       total INTEGER NOT NULL DEFAULT 0, added INTEGER NOT NULL DEFAULT 0, existing INTEGER NOT NULL DEFAULT 0,
                       unresolved INTEGER NOT NULL DEFAULT 0, failed INTEGER NOT NULL DEFAULT 0, skipped INTEGER NOT NULL DEFAULT 0,
                       created_at TEXT NOT NULL, completed_at TEXT NOT NULL DEFAULT ''
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_import_runs_created ON import_runs(created_at)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS import_items (
                       id INTEGER PRIMARY KEY AUTOINCREMENT, import_id TEXT NOT NULL REFERENCES import_runs(id) ON DELETE CASCADE,
                       position INTEGER NOT NULL DEFAULT 0, title TEXT NOT NULL DEFAULT '', backup_source TEXT NOT NULL DEFAULT '',
                       source_id TEXT NOT NULL DEFAULT '', source_url TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'unresolved',
                       reason TEXT NOT NULL DEFAULT '', entry_id INTEGER, raw_json TEXT NOT NULL DEFAULT '{}', candidate_ids_json TEXT NOT NULL DEFAULT '[]',
                       english_title TEXT NOT NULL DEFAULT '', reference_entry_id INTEGER, reference_title TEXT NOT NULL DEFAULT '',
                       reference_description TEXT NOT NULL DEFAULT '', reference_thumbnail_url TEXT NOT NULL DEFAULT '',
                       translation_checked INTEGER NOT NULL DEFAULT 0, suggestion_checked INTEGER NOT NULL DEFAULT 0,
                       suggestion_state TEXT NOT NULL DEFAULT 'pending', matched_snapshot_json TEXT NOT NULL DEFAULT '{}',
                       updated_at TEXT NOT NULL, UNIQUE(import_id, position)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_import_items_run_state ON import_items(import_id,state,position)")
            import_cols = {row[1] for row in conn.execute("PRAGMA table_info(import_items)")}
            if "english_title" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN english_title TEXT NOT NULL DEFAULT ''")
            if "reference_entry_id" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN reference_entry_id INTEGER")
            if "reference_title" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN reference_title TEXT NOT NULL DEFAULT ''")
            if "reference_description" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN reference_description TEXT NOT NULL DEFAULT ''")
            if "reference_thumbnail_url" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN reference_thumbnail_url TEXT NOT NULL DEFAULT ''")
            if "translation_checked" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN translation_checked INTEGER NOT NULL DEFAULT 0")
            if "suggestion_checked" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN suggestion_checked INTEGER NOT NULL DEFAULT 0")
            if "suggestion_state" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN suggestion_state TEXT NOT NULL DEFAULT 'pending'")
            # Older Added labels were inferred from a possibly missing cache.
            # They are not proof that this importer created library membership.
            if "library_add_verified" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN library_add_verified INTEGER NOT NULL DEFAULT 0")
            if "decision_revision" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN decision_revision INTEGER NOT NULL DEFAULT 0")
            if "matched_snapshot_json" not in import_cols:
                conn.execute("ALTER TABLE import_items ADD COLUMN matched_snapshot_json TEXT NOT NULL DEFAULT '{}'")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS import_match_rejections (
                       backup_key TEXT NOT NULL, signature TEXT NOT NULL,
                       backup_title TEXT NOT NULL DEFAULT '', backup_source TEXT NOT NULL DEFAULT '',
                       rejected_title TEXT NOT NULL DEFAULT '', rejected_at TEXT NOT NULL,
                       PRIMARY KEY(backup_key, signature)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_import_match_rejections_key ON import_match_rejections(backup_key)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS import_manual_autoresolve (
                       backup_key TEXT PRIMARY KEY, backup_title TEXT NOT NULL DEFAULT '', backup_source TEXT NOT NULL DEFAULT '',
                       backup_source_id TEXT NOT NULL DEFAULT '', backup_source_url TEXT NOT NULL DEFAULT '',
                       target_source_id TEXT NOT NULL DEFAULT '', target_source_manga_id INTEGER NOT NULL DEFAULT 0,
                       target_source_name TEXT NOT NULL DEFAULT '', target_title TEXT NOT NULL DEFAULT '',
                       target_entry_id_hint INTEGER, target_record_json TEXT NOT NULL DEFAULT '{}',
                       created_at TEXT NOT NULL, updated_at TEXT NOT NULL
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_import_manual_autoresolve_target ON import_manual_autoresolve(target_source_id,target_source_manga_id)")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS import_manual_autoresolve_state (
                       key TEXT PRIMARY KEY, value TEXT NOT NULL DEFAULT ''
                   )"""
            )

            conn.execute(
                """CREATE TABLE IF NOT EXISTS manual_separate_records (
                       target_entry_id INTEGER NOT NULL, source_id TEXT NOT NULL, source_manga_id INTEGER NOT NULL,
                       separate_entry_id_hint INTEGER, source_title TEXT NOT NULL DEFAULT '',
                       created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                       PRIMARY KEY(target_entry_id, source_id, source_manga_id)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_manual_separate_source ON manual_separate_records(source_id,source_manga_id)")

            variant_cols = {row[1] for row in conn.execute("PRAGMA table_info(source_variants)")}
            if "match_type" not in variant_cols:
                conn.execute("ALTER TABLE source_variants ADD COLUMN match_type TEXT NOT NULL DEFAULT 'auto'")
            if "locked" not in variant_cols:
                conn.execute("ALTER TABLE source_variants ADD COLUMN locked INTEGER NOT NULL DEFAULT 0")
            if "initialized" not in variant_cols:
                conn.execute("ALTER TABLE source_variants ADD COLUMN initialized INTEGER NOT NULL DEFAULT 0")
                conn.execute("UPDATE source_variants SET initialized=CASE WHEN length(description)>0 OR length(thumbnail_url)>0 THEN 1 ELSE 0 END")

            # Create after legacy match_type/active columns have been migrated.
            # Restart recovery reads the sparse manual subset, not every source row.
            conn.execute("CREATE INDEX IF NOT EXISTS idx_matches_manual_capture ON source_matches(entry_id,source_id,source_manga_id) WHERE active=1 AND lower(match_type)='manual'")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_variants_manual_capture ON source_variants(entry_id,source_id,source_manga_id) WHERE active=1 AND lower(match_type)='manual'")

            # v0.1.9/v0.2.0 already recorded automatic entry merges in match_history,
            # but deleted the losing entry id. Reconstruct aliases so manga already
            # added to the main Suwayomi library keep resolving after an upgrade.
            conn.execute(
                """INSERT OR IGNORE INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at)
                   SELECT old_source_manga_id,new_source_manga_id,change_type,changed_at
                   FROM match_history
                   WHERE source_id='*' AND change_type LIKE 'merge:%'
                     AND old_source_manga_id IS NOT NULL AND new_source_manga_id IS NOT NULL
                     AND old_source_manga_id<>new_source_manga_id"""
            )
            conn.commit()
        except BaseException:
            conn.rollback()
            raise
        finally:
            conn.close()

        conn = self._connect(settings.origins_path)
        try:
            # Own one transaction per SQLite file, including ALTER and its guarded
            # backfill. A failed backfill must not leave a default-filled column
            # whose presence would cause the next constructor to skip the repair.
            conn.execute("BEGIN IMMEDIATE")
            conn.execute(
                """CREATE TABLE IF NOT EXISTS chapter_candidate_rejections (
                       source_id TEXT NOT NULL, source_chapter_id INTEGER NOT NULL,
                       source_manga_id INTEGER NOT NULL DEFAULT 0, source_name TEXT NOT NULL DEFAULT '',
                       name TEXT NOT NULL DEFAULT '', rejected_at TEXT NOT NULL,
                       PRIMARY KEY(source_id, source_chapter_id)
                   )"""
            )
            conn.execute("CREATE INDEX IF NOT EXISTS idx_candidate_rejections_manga ON chapter_candidate_rejections(source_id,source_manga_id)")
            cols = {row[1] for row in conn.execute("PRAGMA table_info(chapter_candidates)")}
            if "source_chapter_index" not in cols:
                conn.execute("ALTER TABLE chapter_candidates ADD COLUMN source_chapter_index INTEGER NOT NULL DEFAULT -1")
            conn.commit()
        except BaseException:
            conn.rollback()
            raise
        finally:
            conn.close()

    def _thread_connection(self, kind: str, path) -> tuple[sqlite3.Connection, int]:
        """Return one reusable thread-local connection and the nesting depth.

        Nested helpers must not open a second writer connection while the caller
        already owns SQLite's write lock. Reuse the same connection and isolate
        nested scopes with SAVEPOINTs.
        """
        depth_name = f"{kind}_depth"
        conn_name = f"{kind}_conn"
        path_name = f"{kind}_path"
        depth = int(getattr(self._local, depth_name, 0))
        conn = getattr(self._local, conn_name, None)
        wanted_path = str(Path(path).resolve())
        current_path = str(getattr(self._local, path_name, "") or "")
        if conn is not None and depth == 0 and current_path != wanted_path:
            # DATABASE_DIR can be moved live. Idle worker-thread connections are
            # intentionally reopened lazily so the mover does not need unsafe
            # cross-thread access to sqlite3.Connection objects.
            conn.close()
            conn = None
            delattr(self._local, conn_name)
        if conn is None:
            conn = self._connect(path)
            setattr(self._local, conn_name, conn)
            setattr(self._local, path_name, wanted_path)
        setattr(self._local, depth_name, depth + 1)
        return conn, depth

    def _leave_thread_connection(self, kind: str) -> None:
        depth_name = f"{kind}_depth"
        depth = max(0, int(getattr(self._local, depth_name, 1)) - 1)
        setattr(self._local, depth_name, depth)

    @contextmanager
    def _context(self, kind: str):
        # Connections are thread-local and SQLite is in WAL mode, so serializing
        # *every* catalog/origin read behind one Python RLock only makes background
        # Tasks block the UI. SQLite itself safely coordinates concurrent readers
        # and the short write transactions. The tiny condition accounting below is
        # lock-free with respect to the actual SQL work; it only blocks during a live
        # DATABASE_DIR cut-over so no write can land in the old DB after the final
        # verified copy.
        with self._relocation_condition:
            # A writer already admitted before cut-over must be allowed to call
            # nested helpers, otherwise mover and writer wait forever on each other.
            nested = any(int(getattr(self._local, f"{name}_depth", 0)) > 0
                         for name in ("catalog", "origins"))
            while self._database_relocating and not nested:
                self._relocation_condition.wait(timeout=0.25)
            self._active_db_contexts += 1
        try:
            # Resolve the path after relocation admission. A cut-over may have
            # changed settings while this caller was waiting.
            current_path = settings.catalog_path if kind == "catalog" else settings.origins_path
            conn, depth = self._thread_connection(kind, current_path)
            savepoint = f"autosourcarr_{kind}_{depth}" if depth > 0 else ""
            opened_savepoint = False
            try:
                if savepoint:
                    # A SAVEPOINT without BEGIN is itself a top-level transaction;
                    # releasing it would commit before the outer context can fail.
                    if not conn.in_transaction:
                        conn.execute("BEGIN")
                    conn.execute(f"SAVEPOINT {savepoint}")
                    opened_savepoint = True
                yield conn
                if opened_savepoint:
                    conn.execute(f"RELEASE SAVEPOINT {savepoint}")
                elif conn.in_transaction:
                    conn.commit()
            except BaseException:
                if opened_savepoint:
                    try:
                        conn.execute(f"ROLLBACK TO SAVEPOINT {savepoint}")
                    finally:
                        conn.execute(f"RELEASE SAVEPOINT {savepoint}")
                elif not savepoint:
                    conn.rollback()
                raise
            finally:
                self._leave_thread_connection(kind)
        finally:
            with self._relocation_condition:
                self._active_db_contexts = max(0, self._active_db_contexts - 1)
                if self._active_db_contexts == 0:
                    self._relocation_condition.notify_all()

    @contextmanager
    def catalog(self):
        with self._context("catalog") as conn:
            yield conn

    @contextmanager
    def origins(self):
        with self._context("origins") as conn:
            yield conn

    def close_current_thread(self) -> None:
        """Close reusable connections owned by the calling thread."""
        for kind in ("catalog", "origins"):
            conn_name = f"{kind}_conn"
            conn = getattr(self._local, conn_name, None)
            if conn is not None:
                try:
                    conn.close()
                finally:
                    delattr(self._local, conn_name)
            path_name = f"{kind}_path"
            if hasattr(self._local, path_name):
                delattr(self._local, path_name)
            setattr(self._local, f"{kind}_depth", 0)

    def quiescent_files(self, operation, timeout=30.0):
        """Run a file snapshot only after all managed DB contexts have drained.

        The caller first pauses application writers and owns the HTTP admission
        barrier. The operation must use direct SQLite backup connections rather
        than recursively entering a managed context.
        """
        import time
        deadline=time.monotonic()+timeout
        with self._relocation_condition:
            self._database_relocating=True
            try:
                while self._active_db_contexts:
                    left=deadline-time.monotonic()
                    if left<=0: raise TimeoutError('Active database work did not drain; snapshot aborted')
                    self._relocation_condition.wait(timeout=min(.25,left))
                return operation()
            finally:
                self._database_relocating=False
                self._relocation_condition.notify_all()


    @staticmethod
    def _quick_check_path(path: Path) -> None:
        conn = sqlite3.connect(path, timeout=30)
        try:
            row = conn.execute("PRAGMA quick_check").fetchone()
            if not row or str(row[0]).casefold() != "ok":
                raise RuntimeError(f"SQLite quick_check failed for {path}: {row[0] if row else 'no result'}")
        finally:
            conn.close()

    @classmethod
    def _backup_database_file(cls, source: Path, target: Path) -> int:
        """Create a consistent live SQLite backup and atomically publish it."""
        target.parent.mkdir(parents=True, exist_ok=True)
        probe = target.parent / f".autosourcarr-write-test-{os.getpid()}"
        try:
            probe.write_bytes(b"")
            probe.unlink(missing_ok=True)
        except OSError as exc:
            raise RuntimeError(f"Database target is not writable: {target.parent}: {exc}") from exc

        fd, tmp_name = tempfile.mkstemp(prefix=f".{target.name}.", suffix=".moving", dir=target.parent)
        os.close(fd)
        tmp = Path(tmp_name)
        src_conn = dst_conn = None
        try:
            src_conn = sqlite3.connect(f"{source.as_uri()}?mode=ro", timeout=30, uri=True)
            dst_conn = sqlite3.connect(tmp, timeout=30)
            try:
                src_conn.backup(dst_conn, pages=1024, sleep=0.01)
                dst_conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                dst_conn.commit()
            finally:
                dst_conn.close()
                src_conn.close()
                src_conn = dst_conn = None
            cls._quick_check_path(tmp)
            size = int(tmp.stat().st_size)
            os.replace(tmp, target)
            return size
        finally:
            if dst_conn is not None:
                dst_conn.close()
            if src_conn is not None:
                src_conn.close()
            tmp.unlink(missing_ok=True)

    def stage_database_relocation(self, target_dir: Path, config_dir: Path) -> dict[str, Any]:
        """Copy live DBs to a new mounted directory and switch on next restart.

        The source files remain active until restart. A marker lets the next process
        verify the copied databases before deleting the old files, so a failed or
        forgotten restart can never strand AutoSourcarr without its current DB.
        """
        target_dir = Path(target_dir).expanduser()
        if not target_dir.is_absolute():
            raise ValueError("Database location must be an absolute container path")
        current_dir = Path(settings.database_base_dir).resolve()
        target_dir.mkdir(parents=True, exist_ok=True)
        target_dir = target_dir.resolve()
        # Refuse a path that only exists in the container's writable overlay. A DB
        # move is meant to survive container replacement, so the target must live
        # under an explicit Docker/TrueNAS mount (for example /data or /database).
        mount_points: list[Path] = []
        try:
            for raw in Path("/proc/self/mountinfo").read_text("utf-8", errors="replace").splitlines():
                parts = raw.split()
                if len(parts) > 5:
                    mount_points.append(Path(parts[4].replace("\\040", " ").replace("\\134", "\\")))
        except OSError:
            mount_points = []
        mounted_parent = None
        for mount in mount_points:
            try:
                if target_dir == mount or mount in target_dir.parents:
                    if mounted_parent is None or len(str(mount)) > len(str(mounted_parent)):
                        mounted_parent = mount
            except Exception:
                continue
        if mounted_parent is not None and str(mounted_parent) == "/" and target_dir != current_dir:
            raise RuntimeError(
                f"{target_dir} is only on the container root filesystem. Mount a persistent TrueNAS dataset/drive into the AutoSourcarr container (for example at /database) and select that mounted path instead."
            )
        if target_dir == current_dir:
            return {"moved": False, "already_active": True, "active_dir": str(current_dir), "target_dir": str(target_dir)}

        # A move is not a restore/overwrite operation. Reject all existing SQLite
        # files (including WAL/SHM sidecars) before copying either active database.
        for name in ("catalog.sqlite", "chapter-origins.sqlite"):
            for suffix in ("", "-wal", "-shm"):
                target = target_dir / (name + suffix)
                if target.exists() or target.is_symlink():
                    raise ValueError(f"Database target already contains {target.name}; choose an empty directory")
        marker_path = Path(config_dir) / "database-relocation.json"
        if marker_path.exists():
            raise RuntimeError("A database relocation is already pending; finish or recover it before starting another")
        copied = {}
        for source in (Path(settings.catalog_path), Path(settings.origins_path)):
            if not source.is_file():
                raise RuntimeError(f"Active database is missing: {source}")
            target = target_dir / source.name
            copied[source.name] = self._backup_database_file(source, target)

        marker = {
            "source_dir": str(current_dir),
            "target_dir": str(target_dir),
            "files": sorted(copied),
            "bytes": copied,
            "staged_at": now_iso(),
        }
        config_dir = Path(config_dir)
        config_dir.mkdir(parents=True, exist_ok=True)
        marker_path = config_dir / "database-relocation.json"
        tmp = marker_path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(marker, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.replace(tmp, marker_path)
        return {"moved": True, "restart_required": True, "active_dir": str(current_dir), "target_dir": str(target_dir), "files": copied, "marker": str(marker_path)}

    def activate_database_relocation(self, config_dir: Path) -> dict[str, Any]:
        """Perform the final lossless cut-over to a previously staged DB directory.

        Staging can take a while for a large catalog and therefore happens while the
        app remains usable. Cut-over briefly stops *new* DB contexts, waits for the
        short transactions already in flight, refreshes both staged copies from the
        now-quiescent source DBs, switches the runtime paths, and immediately lets
        requests continue. This avoids both an hours-long app pause and the stale-copy
        window that would otherwise exist between clicking Move and redeploying.
        """
        marker_path = Path(config_dir) / "database-relocation.json"
        if not marker_path.is_file():
            raise RuntimeError("No staged database relocation is available")
        try:
            marker = json.loads(marker_path.read_text("utf-8"))
        except Exception as exc:
            raise RuntimeError(f"Invalid relocation marker: {exc}") from exc
        source_dir = Path(str(marker.get("source_dir") or "")).resolve()
        target_dir = Path(str(marker.get("target_dir") or "")).resolve()
        current_dir = Path(settings.database_base_dir).resolve()
        if current_dir != source_dir:
            if current_dir == target_dir:
                return {"activated": False, "already_active": True, "active_dir": str(current_dir)}
            raise RuntimeError(f"Active database directory changed during relocation: {current_dir}")

        with self._relocation_condition:
            self._database_relocating = True
            try:
                while self._active_db_contexts > 0:
                    self._relocation_condition.wait(timeout=0.25)
                copied: dict[str, int] = {}
                # All managed DB contexts are now drained. The second backup is the
                # authoritative copy and therefore includes every write made while
                # the slower initial staging copy was running.
                for name in ("catalog.sqlite", "chapter-origins.sqlite"):
                    source = source_dir / name
                    target = target_dir / name
                    if not source.is_file():
                        raise RuntimeError(f"Active database is missing: {source}")
                    copied[name] = self._backup_database_file(source, target)
                for name in copied:
                    self._quick_check_path(target_dir / name)

                marker["bytes"] = copied
                marker["final_sync_at"] = now_iso()
                marker["activated_in_process"] = True
                tmp = marker_path.with_suffix(".json.tmp")
                tmp.write_text(json.dumps(marker, indent=2, sort_keys=True) + "\n", encoding="utf-8")
                os.replace(tmp, marker_path)
                # Persist the successful final copy BEFORE publishing live paths.
                # A failed marker write must leave this process on the source DBs.
                self.close_current_thread()
                settings.database_dir = target_dir
                return {
                    "activated": True,
                    "restart_required": False,
                    "active_dir": str(target_dir),
                    "source_dir": str(source_dir),
                    "target_dir": str(target_dir),
                    "files": copied,
                }
            finally:
                self._database_relocating = False
                self._relocation_condition.notify_all()

    def finalize_database_relocation(self, config_dir: Path) -> dict[str, Any]:
        """After restart on the target DBs, remove only the old SQLite files."""
        marker_path = Path(config_dir) / "database-relocation.json"
        if not marker_path.is_file():
            return {"finalized": False, "pending": False}
        try:
            marker = json.loads(marker_path.read_text("utf-8"))
        except Exception as exc:
            raise RuntimeError(f"Invalid database relocation marker: {exc}") from exc
        source_dir = Path(str(marker.get("source_dir") or ""))
        target_dir = Path(str(marker.get("target_dir") or ""))
        if (not source_dir.is_absolute() or not target_dir.is_absolute()
                or source_dir.resolve() == target_dir.resolve()):
            raise RuntimeError("Database relocation marker requires distinct absolute source and target directories")
        active_dir = Path(settings.database_base_dir).resolve()
        try:
            target_resolved = target_dir.resolve()
        except Exception:
            target_resolved = target_dir
        if active_dir != target_resolved:
            return {"finalized": False, "pending": True, "active_dir": str(active_dir), "target_dir": str(target_dir)}
        if not str(marker.get("final_sync_at") or ""):
            raise RuntimeError(
                "Database relocation was staged but never completed its lossless cut-over. "
                "The original database is still intact; restore DATABASE_DIR to the source path and retry the move."
            )
        for name in ("catalog.sqlite", "chapter-origins.sqlite"):
            target = target_dir / name
            if not target.is_file():
                raise RuntimeError(f"Relocated database is missing: {target}")
            self._quick_check_path(target)
        removed = []
        if source_dir and source_dir != target_dir:
            for name in ("catalog.sqlite", "chapter-origins.sqlite"):
                for suffix in ("", "-wal", "-shm"):
                    old = Path(str(source_dir / name) + suffix)
                    try:
                        if old.is_file():
                            old.unlink()
                            removed.append(str(old))
                    except OSError:
                        pass
        marker_path.unlink(missing_ok=True)
        return {"finalized": True, "pending": False, "active_dir": str(active_dir), "removed": removed}

    def database_relocation_status(self, config_dir: Path) -> dict[str, Any]:
        marker_path = Path(config_dir) / "database-relocation.json"
        marker = None
        if marker_path.is_file():
            try:
                marker = json.loads(marker_path.read_text("utf-8"))
            except Exception:
                marker = {"error": "invalid relocation marker"}
        files = []
        total = 0
        for base in (Path(settings.catalog_path), Path(settings.origins_path)):
            for suffix in ("", "-wal", "-shm"):
                candidate = Path(str(base) + suffix)
                if candidate.is_file():
                    try:
                        size = int(candidate.stat().st_size)
                    except OSError:
                        continue
                    total += size
                    files.append({"path": str(candidate), "bytes": size})
        return {
            "active_dir": str(Path(settings.database_base_dir)),
            "catalog_path": str(settings.catalog_path),
            "origins_path": str(settings.origins_path),
            "bytes": total,
            "files": files,
            "pending_relocation": marker,
        }

    @staticmethod
    def rows(rows: Iterable[sqlite3.Row]) -> list[dict[str, Any]]:
        return [dict(r) for r in rows]

    def setting(self, key: str, default: Any = None) -> Any:
        with self.catalog() as c:
            row = c.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        if not row:
            return default
        try:
            return json.loads(row["value"])
        except Exception:
            return row["value"]

    def set_setting(self, key: str, value: Any) -> None:
        raw = json.dumps(value)
        with self.catalog() as c:
            c.execute(
                "INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (key, raw),
            )

    def user_ui_preferences(self, user_key: str) -> dict[str, Any]:
        key = str(user_key or "local:default")[:512]
        with self.catalog() as c:
            row = c.execute("SELECT values_json FROM user_ui_preferences WHERE user_key=?", (key,)).fetchone()
        if not row:
            return {}
        try:
            value = json.loads(row["values_json"] or "{}")
            return value if isinstance(value, dict) else {}
        except Exception:
            return {}

    def set_user_ui_preferences(self, user_key: str, values: dict[str, Any], *, merge: bool = True) -> dict[str, Any]:
        key = str(user_key or "local:default")[:512]
        incoming = dict(values or {})
        # One short read/merge/write transaction prevents two tabs or users of the
        # same profile from silently discarding each other's unrelated settings.
        with self.catalog() as c:
            if not c.in_transaction:
                c.execute("BEGIN IMMEDIATE")
            current = {}
            if merge:
                row = c.execute("SELECT values_json FROM user_ui_preferences WHERE user_key=?", (key,)).fetchone()
                if row:
                    try:
                        decoded = json.loads(row["values_json"] or "{}")
                        current = decoded if isinstance(decoded, dict) else {}
                    except (TypeError, ValueError):
                        pass
            current.update(incoming)
            raw = json.dumps(current, ensure_ascii=False, separators=(",", ":"))
            if len(raw.encode("utf-8")) > 65536:
                raise ValueError("UI preferences exceed 64 KiB")
            c.execute(
                """INSERT INTO user_ui_preferences(user_key,values_json,updated_at) VALUES(?,?,?)
                   ON CONFLICT(user_key) DO UPDATE SET values_json=excluded.values_json,updated_at=excluded.updated_at""",
                (key, raw, now_iso()),
            )
        return current


db = Database()
