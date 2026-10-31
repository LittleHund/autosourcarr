
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
