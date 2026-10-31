BEGIN TRANSACTION;
CREATE TABLE automatic_merge_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    target_entry_id INTEGER NOT NULL,
    duplicate_entry_id INTEGER NOT NULL,
    target_snapshot_json TEXT NOT NULL DEFAULT '{}',
    duplicate_snapshot_json TEXT NOT NULL DEFAULT '{}',
    reason TEXT NOT NULL DEFAULT '',
    merged_at TEXT NOT NULL,
    repaired_at TEXT NOT NULL DEFAULT ''
, evidence_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE automatic_merge_lineage (
    canonical_entry_id INTEGER NOT NULL,
    member_entry_id INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL DEFAULT '{}',
    merged_at TEXT NOT NULL,
    PRIMARY KEY(canonical_entry_id, member_entry_id)
);
CREATE TABLE browse_cache (
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
CREATE TABLE catalogue_decisions (
 uid TEXT NOT NULL, digest TEXT NOT NULL, decision_json TEXT NOT NULL,
 state TEXT NOT NULL DEFAULT 'pending', message TEXT NOT NULL DEFAULT '',
 applied_revision TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE catalogue_entry_ids (
 uid TEXT NOT NULL, entry_id INTEGER NOT NULL, primary_id INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(uid,entry_id)
);
CREATE TABLE catalogue_exchange_receipts (
 digest TEXT PRIMARY KEY, result_json TEXT NOT NULL, applied_at TEXT NOT NULL
);
CREATE TABLE catalogue_exchange_stages (
 id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, revision TEXT NOT NULL,
 created_at REAL NOT NULL, result_json TEXT NOT NULL DEFAULT ''
);
CREATE TABLE catalogue_import_records (
 uid TEXT NOT NULL, digest TEXT NOT NULL, record_json TEXT NOT NULL,
 state TEXT NOT NULL, message TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE catalogue_origin (id INTEGER PRIMARY KEY CHECK(id=1), uid TEXT NOT NULL);
CREATE TABLE catalogue_public_refs (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, PRIMARY KEY(entry_id,reference)
);
CREATE TABLE catalogue_record_bindings (
 source_id TEXT NOT NULL,source_manga_id INTEGER NOT NULL,reference TEXT NOT NULL,entry_id INTEGER NOT NULL,
 PRIMARY KEY(source_id,source_manga_id,reference)
);
CREATE TABLE catalogue_record_resolutions (
 uid TEXT NOT NULL,digest TEXT NOT NULL,resolution_json TEXT NOT NULL,PRIMARY KEY(uid,digest)
);
CREATE TABLE catalogue_shared_entries (entry_id INTEGER PRIMARY KEY);
CREATE TABLE catalogue_source_facts (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, source_id TEXT NOT NULL, fact_json TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference,source_id)
);
CREATE TABLE catalogue_uid_records (
 uid TEXT NOT NULL,source_id TEXT NOT NULL,reference TEXT NOT NULL,PRIMARY KEY(uid,source_id,reference)
);
CREATE TABLE crawl_pending (
 work_key TEXT PRIMARY KEY, page INTEGER NOT NULL, payload_json TEXT NOT NULL,
 created_at TEXT NOT NULL
);
CREATE TABLE deleted_entries_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    old_entry_id INTEGER NOT NULL,
    preferred_title TEXT NOT NULL DEFAULT '',
    source_records INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT NOT NULL
);
CREATE TABLE deleted_source_records (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    source_title TEXT NOT NULL DEFAULT '',
    deleted_entry_title TEXT NOT NULL DEFAULT '',
    deleted_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE display_metadata (
 entry_id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, fields_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE display_metadata_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 fields_json TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE due_work (
 kind TEXT NOT NULL, work_key TEXT NOT NULL, next_due TEXT NOT NULL DEFAULT '',
 last_attempt TEXT NOT NULL DEFAULT '', last_success TEXT NOT NULL DEFAULT '',
 last_complete TEXT NOT NULL DEFAULT '', outcome TEXT NOT NULL DEFAULT 'unknown',
 failures INTEGER NOT NULL DEFAULT 0, reason TEXT NOT NULL DEFAULT '',
 config_revision TEXT NOT NULL DEFAULT '', complete_revision TEXT NOT NULL DEFAULT '',
 PRIMARY KEY(kind,work_key)
);
CREATE TABLE duplicate_review_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    left_entry_id INTEGER NOT NULL,
    right_entry_id INTEGER NOT NULL,
    decision TEXT NOT NULL,
    reviewed_at TEXT NOT NULL
);
CREATE TABLE duplicate_reviews (
    left_entry_id INTEGER NOT NULL,
    right_entry_id INTEGER NOT NULL,
    decision TEXT NOT NULL,
    reviewed_at TEXT NOT NULL,
    PRIMARY KEY(left_entry_id, right_entry_id)
);
CREATE TABLE duplicate_split_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    original_entry_id INTEGER NOT NULL,
    new_entry_id INTEGER NOT NULL,
    records_json TEXT NOT NULL DEFAULT '[]',
    split_at TEXT NOT NULL
);
CREATE TABLE entries (
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
INSERT INTO "entries" VALUES(1,'Alpha','','',1,'','','',0,'unknown',0.0,'unknown','2026-01-01','2026-01-01');
INSERT INTO "entries" VALUES(2,'Beta','','',2,'','','',0,'unknown',0.0,'unknown','2026-01-01','2026-01-01');
CREATE TABLE entry_aliases (
    old_entry_id INTEGER PRIMARY KEY,
    canonical_entry_id INTEGER NOT NULL,
    reason TEXT NOT NULL DEFAULT '',
    merged_at TEXT NOT NULL
);
CREATE TABLE entry_preference_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, canonical_entry_id INTEGER NOT NULL,
 old_entry_id INTEGER NOT NULL, title TEXT NOT NULL, profile_json TEXT NOT NULL,
 tracking_json TEXT NOT NULL, saved_at TEXT NOT NULL
);
CREATE TABLE entry_preferred_titles (
    entry_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
    raw_title TEXT NOT NULL,
    normalized TEXT
);
INSERT INTO "entry_preferred_titles" VALUES(1,'Alpha',NULL);
INSERT INTO "entry_preferred_titles" VALUES(2,'Beta',NULL);
CREATE TABLE gallery_layout_candidates (
 split_id INTEGER PRIMARY KEY, candidate_ids_json TEXT NOT NULL
);
CREATE TABLE gallery_policies (
 source_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, policy_json TEXT NOT NULL,
 updated_at TEXT NOT NULL, layout_revision INTEGER NOT NULL DEFAULT 0,
 layout_result_json TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE gallery_records (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    metadata_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE general_task_history (
 id TEXT PRIMARY KEY, record_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE identity_links (
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
CREATE TABLE import_items (
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
    updated_at TEXT NOT NULL, library_add_verified INTEGER NOT NULL DEFAULT 0, decision_revision INTEGER NOT NULL DEFAULT 0,
    UNIQUE(import_id, position)
);
CREATE TABLE import_manual_autoresolve (
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
CREATE TABLE import_manual_autoresolve_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL DEFAULT ''
);
CREATE TABLE import_match_rejections (
    backup_key TEXT NOT NULL,
    signature TEXT NOT NULL,
    backup_title TEXT NOT NULL DEFAULT '',
    backup_source TEXT NOT NULL DEFAULT '',
    rejected_title TEXT NOT NULL DEFAULT '',
    rejected_at TEXT NOT NULL,
    PRIMARY KEY(backup_key, signature)
);
CREATE TABLE import_runs (
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
CREATE TABLE library_complete_observations (
 entry_id INTEGER PRIMARY KEY, observation_json TEXT NOT NULL, observed_at TEXT NOT NULL
);
CREATE TABLE library_tracking (
 entry_id INTEGER PRIMARY KEY,
 monitored INTEGER NOT NULL DEFAULT 0, revision INTEGER NOT NULL DEFAULT 0,
 main_manga_id INTEGER, observation_json TEXT NOT NULL DEFAULT '{}',
 observed_at TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL DEFAULT ''
);
CREATE TABLE manual_match_bindings (
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
CREATE TABLE manual_match_groups (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE manual_separate_records (
    target_entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    separate_entry_id_hint INTEGER,
    source_title TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(target_entry_id, source_id, source_manga_id)
);
CREATE TABLE manual_title_scopes (
    entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    PRIMARY KEY(entry_id, source_id, source_manga_id)
);
CREATE TABLE match_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL,
    source_id TEXT NOT NULL,
    old_source_manga_id INTEGER,
    new_source_manga_id INTEGER,
    change_type TEXT NOT NULL,
    changed_at TEXT NOT NULL
);
CREATE TABLE matching_policies (
    scope TEXT NOT NULL CHECK(scope IN ('source','group')),
    subject TEXT NOT NULL,
    direction TEXT NOT NULL CHECK(direction IN ('internal','external')),
    rule_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(scope,subject,direction)
);
CREATE TABLE matching_presets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    rule_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE matching_review_actions (
 id TEXT PRIMARY KEY, action_json TEXT NOT NULL, created_at TEXT NOT NULL,
 undone_at TEXT NOT NULL DEFAULT ''
);
CREATE TABLE metadata_query_cache (
    provider TEXT NOT NULL,
    query_key TEXT NOT NULL,
    query_text TEXT NOT NULL DEFAULT '',
    results_json TEXT NOT NULL DEFAULT '[]',
    updated_at TEXT NOT NULL,
    PRIMARY KEY(provider, query_key)
);
CREATE TABLE mixed_entry_reviews (
    fingerprint TEXT PRIMARY KEY,
    entry_id_hint INTEGER NOT NULL,
    reviewed_at TEXT NOT NULL
);
CREATE TABLE pending_entry_merges (
    duplicate_entry_id INTEGER PRIMARY KEY,
    target_entry_id INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    last_error TEXT NOT NULL DEFAULT ''
);
CREATE TABLE pending_entry_splits (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    original_entry_id INTEGER NOT NULL,
    new_entry_id INTEGER NOT NULL,
    records_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    last_error TEXT NOT NULL DEFAULT ''
);
CREATE TABLE policy_preview_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, preview_json TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE portable_identity_review_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 reference TEXT NOT NULL, decision TEXT NOT NULL, evidence_sha256 TEXT NOT NULL,
 note TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE portable_identity_reviews (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, decision TEXT NOT NULL,
 evidence_sha256 TEXT NOT NULL, note TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference)
);
CREATE TABLE preferred_title_override_sources (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, source_manga_id)
);
CREATE TABLE preferred_title_overrides (
    entry_id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE quality_attempts (
 candidate_id INTEGER PRIMARY KEY, attempted_at TEXT NOT NULL, outcome TEXT NOT NULL,
 observed_pages INTEGER NOT NULL DEFAULT 0, unobserved_pages INTEGER NOT NULL DEFAULT 0,
 reason TEXT NOT NULL DEFAULT ''
);
CREATE TABLE serving_choices (
 virtual_chapter_id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL, candidate_id INTEGER NOT NULL,
 revision INTEGER NOT NULL, changed_at TEXT NOT NULL
);
CREATE TABLE serving_policy_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 policy_json TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE serving_profiles (
 entry_id INTEGER PRIMARY KEY,
 profile_json TEXT NOT NULL, revision INTEGER NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE serving_upgrade_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, virtual_chapter_id INTEGER NOT NULL,
 old_candidate_id INTEGER NOT NULL, candidate_id INTEGER NOT NULL, changed_at TEXT NOT NULL
);
CREATE TABLE serving_upgrade_policies (
 entry_id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, policy_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
CREATE TABLE source_color_profiles (
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
CREATE TABLE source_config (
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
CREATE TABLE source_discovery_outcomes (
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT '',
    message TEXT NOT NULL DEFAULT '',
    candidate_entry_id INTEGER,
    checked_at TEXT NOT NULL,
    PRIMARY KEY(entry_id,source_id)
);
CREATE TABLE source_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    priority INTEGER NOT NULL DEFAULT 1000,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE source_health (
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
CREATE TABLE source_matches (
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
CREATE TABLE source_record_tags (
    source_id TEXT NOT NULL,
    source_manga_id INTEGER NOT NULL,
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY(source_id, source_manga_id, tag_id)
);
CREATE TABLE source_variants (
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
CREATE TABLE tag_reviews (
    left_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    right_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    decision TEXT NOT NULL CHECK(decision IN ('same','different')),
    updated_at TEXT NOT NULL,
    PRIMARY KEY(left_id, right_id),
    CHECK(left_id < right_id)
);
CREATE TABLE tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    normalized TEXT NOT NULL,
    UNIQUE(namespace, normalized)
);
CREATE TABLE thumbnail_fingerprints (
    subject_key TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL DEFAULT '',
    width INTEGER NOT NULL DEFAULT 0,
    height INTEGER NOT NULL DEFAULT 0,
    source_url TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL
);
CREATE TABLE titles (
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
CREATE TABLE translation_cache (
    source_text TEXT NOT NULL,
    source_lang TEXT NOT NULL DEFAULT 'auto',
    target_lang TEXT NOT NULL,
    translated_text TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL,
    PRIMARY KEY(source_text, target_lang)
);
CREATE TABLE user_ui_preferences (
    user_key TEXT PRIMARY KEY,
    values_json TEXT NOT NULL DEFAULT '{}',
    updated_at TEXT NOT NULL
);
CREATE INDEX idx_entry_preferred_titles_normalized ON entry_preferred_titles(normalized);
CREATE TRIGGER entry_preferred_titles_insert AFTER INSERT ON entries
BEGIN
    INSERT OR REPLACE INTO entry_preferred_titles(entry_id,raw_title,normalized)
    VALUES(NEW.id,NEW.preferred_title,NULL);
END;
CREATE TRIGGER entry_preferred_titles_update AFTER UPDATE OF preferred_title ON entries
WHEN OLD.preferred_title IS NOT NEW.preferred_title
BEGIN
    INSERT OR REPLACE INTO entry_preferred_titles(entry_id,raw_title,normalized)
    VALUES(NEW.id,NEW.preferred_title,NULL);
END;
CREATE TRIGGER entry_preferred_titles_delete AFTER DELETE ON entries
BEGIN
    DELETE FROM entry_preferred_titles WHERE entry_id=OLD.id;
END;
CREATE INDEX idx_source_record_tags_tag ON source_record_tags(tag_id);
CREATE INDEX idx_tag_reviews_right ON tag_reviews(right_id);
CREATE INDEX idx_entries_updated_id ON entries(updated_at,id);
CREATE INDEX idx_titles_normalized ON titles(normalized);
CREATE INDEX idx_titles_entry ON titles(entry_id);
CREATE INDEX idx_titles_entry_normalized ON titles(entry_id,normalized);
CREATE INDEX idx_entries_status ON entries(status);
CREATE INDEX idx_entries_lower_title ON entries(lower(preferred_title));
CREATE INDEX idx_source_match_manga_lookup ON source_matches(source_manga_id);
CREATE UNIQUE INDEX idx_source_manga_unique ON source_matches(source_id, source_manga_id) WHERE active=1;
CREATE INDEX idx_matches_entry ON source_matches(entry_id, active);
CREATE INDEX idx_matches_entry_source_active ON source_matches(entry_id,source_id,active);
CREATE INDEX idx_matches_source_title ON source_matches(source_id,source_title COLLATE NOCASE) WHERE active=1;
CREATE INDEX idx_matches_lower_title ON source_matches(lower(source_title),entry_id) WHERE active=1;
CREATE INDEX idx_color_profiles_mode ON source_color_profiles(color_mode, confidence);
CREATE INDEX idx_variants_source_title ON source_variants(source_id,source_title COLLATE NOCASE) WHERE active=1;
CREATE INDEX idx_variants_lower_title ON source_variants(lower(source_title),entry_id) WHERE active=1;
CREATE INDEX idx_source_variants_entry ON source_variants(entry_id, active);
CREATE INDEX idx_source_variants_entry_source_active ON source_variants(entry_id,source_id,active);
CREATE INDEX idx_source_variant_manga_lookup ON source_variants(source_manga_id);
CREATE INDEX idx_entry_alias_canonical ON entry_aliases(canonical_entry_id);
CREATE INDEX idx_match_history_source_id ON match_history(source_id,id);
CREATE INDEX idx_browse_cache_accessed ON browse_cache(last_accessed);
CREATE INDEX idx_translation_target ON translation_cache(target_lang);
CREATE INDEX idx_duplicate_reviews_decision ON duplicate_reviews(decision);
CREATE INDEX idx_duplicate_split_original ON duplicate_split_history(original_entry_id, split_at);
CREATE INDEX idx_auto_merge_lineage_member ON automatic_merge_lineage(member_entry_id);
CREATE INDEX idx_auto_merge_events_target ON automatic_merge_events(target_entry_id, merged_at);
CREATE INDEX idx_auto_merge_events_pair ON automatic_merge_events(target_entry_id, duplicate_entry_id);
CREATE INDEX idx_auto_merge_events_compact_history ON automatic_merge_events(
    merged_at DESC,id DESC,target_entry_id,duplicate_entry_id,reason,repaired_at,
    CASE WHEN json_valid(target_snapshot_json) THEN json_extract(target_snapshot_json,'$.entry.preferred_title') END,
    CASE WHEN json_valid(duplicate_snapshot_json) THEN json_extract(duplicate_snapshot_json,'$.entry.preferred_title') END);
CREATE INDEX idx_manual_title_scopes_source ON manual_title_scopes(source_id,source_manga_id);
CREATE INDEX idx_import_runs_created ON import_runs(created_at);
CREATE INDEX idx_import_items_run_state ON import_items(import_id,state,position);
CREATE INDEX idx_import_match_rejections_key ON import_match_rejections(backup_key);
CREATE INDEX idx_import_manual_autoresolve_target ON import_manual_autoresolve(target_source_id,target_source_manga_id);
CREATE INDEX idx_manual_bindings_group ON manual_match_bindings(group_id);
CREATE INDEX idx_manual_bindings_entry_hint ON manual_match_bindings(entry_id_hint);
CREATE INDEX idx_manual_separate_source ON manual_separate_records(source_id, source_manga_id);
CREATE INDEX idx_identity_provider_external ON identity_links(provider, external_id);
CREATE INDEX idx_identity_entry ON identity_links(entry_id);
CREATE INDEX idx_identity_provider ON identity_links(provider);
CREATE INDEX idx_thumbnail_fingerprint_value ON thumbnail_fingerprints(fingerprint);
CREATE INDEX idx_metadata_query_cache_updated ON metadata_query_cache(updated_at);
CREATE INDEX idx_entry_preference_history ON entry_preference_history(canonical_entry_id,id);
CREATE INDEX idx_task_history_updated ON general_task_history(updated_at);
CREATE INDEX idx_entries_library_title ON entries(lower(preferred_title),id);
CREATE INDEX idx_due_work_time ON due_work(kind,next_due,work_key);
CREATE INDEX idx_display_metadata_history ON display_metadata_history(entry_id,id);
CREATE INDEX idx_portable_review_reference ON portable_identity_reviews(reference,decision,entry_id);
CREATE INDEX idx_portable_review_history ON portable_identity_review_history(entry_id,id);
CREATE UNIQUE INDEX idx_catalogue_primary ON catalogue_entry_ids(entry_id) WHERE primary_id=1;
CREATE UNIQUE INDEX idx_catalogue_primary_uid ON catalogue_entry_ids(uid) WHERE primary_id=1;
CREATE INDEX idx_catalogue_verified_reference ON catalogue_record_bindings(source_id,reference,entry_id);
CREATE INDEX idx_catalogue_public_reference ON catalogue_public_refs(reference,entry_id);
CREATE INDEX idx_source_config_group ON source_config(group_id);
CREATE INDEX idx_matches_manual_capture ON source_matches(entry_id,source_id,source_manga_id) WHERE active=1 AND lower(match_type)='manual';
CREATE INDEX idx_variants_manual_capture ON source_variants(entry_id,source_id,source_manga_id) WHERE active=1 AND lower(match_type)='manual';
DELETE FROM "sqlite_sequence";
INSERT INTO "sqlite_sequence" VALUES('entries',2);
COMMIT;
