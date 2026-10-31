
CREATE TABLE IF NOT EXISTS gallery_policies (
 source_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, policy_json TEXT NOT NULL,
 updated_at TEXT NOT NULL, layout_revision INTEGER NOT NULL DEFAULT 0,
 layout_result_json TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE IF NOT EXISTS gallery_layout_candidates (
 split_id INTEGER PRIMARY KEY, candidate_ids_json TEXT NOT NULL
);
