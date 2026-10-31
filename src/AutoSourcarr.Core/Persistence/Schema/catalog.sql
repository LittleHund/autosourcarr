
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
