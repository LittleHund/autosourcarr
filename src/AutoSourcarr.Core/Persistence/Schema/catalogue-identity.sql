
CREATE TABLE IF NOT EXISTS catalogue_origin (id INTEGER PRIMARY KEY CHECK(id=1), uid TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS catalogue_entry_ids (
 uid TEXT NOT NULL, entry_id INTEGER NOT NULL, primary_id INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(uid,entry_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalogue_primary ON catalogue_entry_ids(entry_id) WHERE primary_id=1;
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalogue_primary_uid ON catalogue_entry_ids(uid) WHERE primary_id=1;
CREATE TABLE IF NOT EXISTS catalogue_uid_records (
 uid TEXT NOT NULL,source_id TEXT NOT NULL,reference TEXT NOT NULL,PRIMARY KEY(uid,source_id,reference)
);
CREATE TABLE IF NOT EXISTS catalogue_record_bindings (
 source_id TEXT NOT NULL,source_manga_id INTEGER NOT NULL,reference TEXT NOT NULL,entry_id INTEGER NOT NULL,
 PRIMARY KEY(source_id,source_manga_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_verified_reference ON catalogue_record_bindings(source_id,reference,entry_id);
CREATE TABLE IF NOT EXISTS catalogue_shared_entries (entry_id INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS catalogue_public_refs (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, PRIMARY KEY(entry_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_public_reference ON catalogue_public_refs(reference,entry_id);
CREATE TABLE IF NOT EXISTS catalogue_source_facts (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, source_id TEXT NOT NULL, fact_json TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference,source_id)
);
CREATE TABLE IF NOT EXISTS catalogue_import_records (
 uid TEXT NOT NULL, digest TEXT NOT NULL, record_json TEXT NOT NULL,
 state TEXT NOT NULL, message TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_record_resolutions (
 uid TEXT NOT NULL,digest TEXT NOT NULL,resolution_json TEXT NOT NULL,PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_decisions (
 uid TEXT NOT NULL, digest TEXT NOT NULL, decision_json TEXT NOT NULL,
 state TEXT NOT NULL DEFAULT 'pending', message TEXT NOT NULL DEFAULT '',
 applied_revision TEXT NOT NULL DEFAULT '', PRIMARY KEY(uid,digest)
);
CREATE TABLE IF NOT EXISTS catalogue_exchange_stages (
 id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, revision TEXT NOT NULL,
 created_at REAL NOT NULL, result_json TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS catalogue_exchange_receipts (
 digest TEXT PRIMARY KEY, result_json TEXT NOT NULL, applied_at TEXT NOT NULL
);
