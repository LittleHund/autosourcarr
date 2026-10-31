
CREATE TABLE IF NOT EXISTS portable_identity_reviews (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, decision TEXT NOT NULL,
 evidence_sha256 TEXT NOT NULL, note TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_portable_review_reference ON portable_identity_reviews(reference,decision,entry_id);
CREATE TABLE IF NOT EXISTS portable_identity_review_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 reference TEXT NOT NULL, decision TEXT NOT NULL, evidence_sha256 TEXT NOT NULL,
 note TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_portable_review_history ON portable_identity_review_history(entry_id,id);
