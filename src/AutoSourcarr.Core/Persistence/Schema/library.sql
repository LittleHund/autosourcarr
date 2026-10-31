
CREATE TABLE IF NOT EXISTS library_tracking (
 entry_id INTEGER PRIMARY KEY,
 monitored INTEGER NOT NULL DEFAULT 0, revision INTEGER NOT NULL DEFAULT 0,
 main_manga_id INTEGER, observation_json TEXT NOT NULL DEFAULT '{}',
 observed_at TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS serving_profiles (
 entry_id INTEGER PRIMARY KEY,
 profile_json TEXT NOT NULL, revision INTEGER NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS general_task_history (
 id TEXT PRIMARY KEY, record_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS entry_preference_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, canonical_entry_id INTEGER NOT NULL,
 old_entry_id INTEGER NOT NULL, title TEXT NOT NULL, profile_json TEXT NOT NULL,
 tracking_json TEXT NOT NULL, saved_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_entry_preference_history ON entry_preference_history(canonical_entry_id,id);
CREATE INDEX IF NOT EXISTS idx_task_history_updated ON general_task_history(updated_at);
CREATE TABLE IF NOT EXISTS matching_review_actions (
 id TEXT PRIMARY KEY, action_json TEXT NOT NULL, created_at TEXT NOT NULL,
 undone_at TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_entries_library_title ON entries(lower(preferred_title),id);
CREATE TABLE IF NOT EXISTS quality_attempts (
 candidate_id INTEGER PRIMARY KEY, attempted_at TEXT NOT NULL, outcome TEXT NOT NULL,
 observed_pages INTEGER NOT NULL DEFAULT 0, unobserved_pages INTEGER NOT NULL DEFAULT 0,
 reason TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS due_work (
 kind TEXT NOT NULL, work_key TEXT NOT NULL, next_due TEXT NOT NULL DEFAULT '',
 last_attempt TEXT NOT NULL DEFAULT '', last_success TEXT NOT NULL DEFAULT '',
 last_complete TEXT NOT NULL DEFAULT '', outcome TEXT NOT NULL DEFAULT 'unknown',
 failures INTEGER NOT NULL DEFAULT 0, reason TEXT NOT NULL DEFAULT '',
 config_revision TEXT NOT NULL DEFAULT '', complete_revision TEXT NOT NULL DEFAULT '',
 PRIMARY KEY(kind,work_key)
);
CREATE INDEX IF NOT EXISTS idx_due_work_time ON due_work(kind,next_due,work_key);
CREATE TABLE IF NOT EXISTS crawl_pending (
 work_key TEXT PRIMARY KEY, page INTEGER NOT NULL, payload_json TEXT NOT NULL,
 created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS library_complete_observations (
 entry_id INTEGER PRIMARY KEY, observation_json TEXT NOT NULL, observed_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS display_metadata (
 entry_id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, fields_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS display_metadata_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 fields_json TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_display_metadata_history ON display_metadata_history(entry_id,id);
CREATE TABLE IF NOT EXISTS serving_upgrade_policies (
 entry_id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, policy_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS serving_choices (
 virtual_chapter_id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL, candidate_id INTEGER NOT NULL,
 revision INTEGER NOT NULL, changed_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS serving_upgrade_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, virtual_chapter_id INTEGER NOT NULL,
 old_candidate_id INTEGER NOT NULL, candidate_id INTEGER NOT NULL, changed_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS policy_preview_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, preview_json TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS serving_policy_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 policy_json TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
INSERT OR IGNORE INTO library_complete_observations(entry_id,observation_json,observed_at)
 SELECT entry_id,observation_json,observed_at FROM library_tracking
 WHERE observed_at<>'' AND json_valid(observation_json)
 AND json_extract(observation_json,'$.download_state')='checked'
 AND json_extract(observation_json,'$.coverage_state')='checked';
