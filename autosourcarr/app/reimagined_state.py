"""Additive state owned by the Reimagined instance, in its existing catalog."""
SCHEMA = '''
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
'''


from .identity_review import SCHEMA as IDENTITY_REVIEW_SCHEMA
SCHEMA += IDENTITY_REVIEW_SCHEMA
from .gallery_policy import SCHEMA as GALLERY_POLICY_SCHEMA
SCHEMA += GALLERY_POLICY_SCHEMA
from .catalog_identity import SCHEMA as CATALOGUE_IDENTITY_SCHEMA
SCHEMA += CATALOGUE_IDENTITY_SCHEMA


def reparent_preferences(connection,target_id,duplicate_id):
    """Preserve new per-work choices in the existing catalog merge transaction."""
    import json
    from .db import now_iso
    c=connection
    from .metadata_fields import reparent as reparent_display
    reparent_display(c,target_id,duplicate_id)
    from .identity_review import reparent as reparent_identity
    reparent_identity(c,target_id,duplicate_id)
    from .catalog_identity import reparent as reparent_catalogue
    reparent_catalogue(c,target_id,duplicate_id)
    # Keep per-work opt-in settings. Conflicting absorbed settings stay in the
    # preference history; a surviving user's policy remains authoritative.
    upgrade=c.execute('SELECT * FROM serving_upgrade_policies WHERE entry_id=?',(duplicate_id,)).fetchone()
    c.execute('UPDATE serving_policy_history SET entry_id=? WHERE entry_id=?',(target_id,duplicate_id))
    if upgrade:
        c.execute('INSERT INTO serving_policy_history(entry_id,old_entry_id,policy_json,reason,created_at) VALUES(?,?,?,?,?)',
                  (target_id,duplicate_id,upgrade['policy_json'],'absorbed policy; surviving choice remains authoritative',now_iso()))
        c.execute('INSERT OR IGNORE INTO serving_upgrade_policies VALUES(?,?,?,?)',
                  (target_id,upgrade['revision']+1,upgrade['policy_json'],now_iso()))
        c.execute('DELETE FROM serving_upgrade_policies WHERE entry_id=?',(duplicate_id,))
    c.execute('UPDATE serving_choices SET entry_id=? WHERE entry_id=?',(target_id,duplicate_id))
    c.execute('UPDATE serving_upgrade_history SET entry_id=? WHERE entry_id=?',(target_id,duplicate_id))
    c.execute("DELETE FROM due_work WHERE kind='library' AND work_key IN (?,?)",(str(target_id),str(duplicate_id)))
    c.execute('DELETE FROM library_complete_observations WHERE entry_id IN (?,?)',(target_id,duplicate_id))
    profile=c.execute('SELECT * FROM serving_profiles WHERE entry_id=?',(duplicate_id,)).fetchone()
    tracking=c.execute('SELECT * FROM library_tracking WHERE entry_id=?',(duplicate_id,)).fetchone()
    c.execute('UPDATE entry_preference_history SET canonical_entry_id=? WHERE canonical_entry_id=?',(target_id,duplicate_id))
    if not profile and not tracking:return
    title=c.execute('SELECT preferred_title FROM entries WHERE id=?',(duplicate_id,)).fetchone()[0]
    c.execute('INSERT INTO entry_preference_history(canonical_entry_id,old_entry_id,title,profile_json,tracking_json,saved_at) VALUES(?,?,?,?,?,?)',
              (target_id,duplicate_id,title,profile['profile_json'] if profile else '{}',json.dumps(dict(tracking)) if tracking else '{}',now_iso()))
    current=c.execute('SELECT * FROM serving_profiles WHERE entry_id=?',(target_id,)).fetchone()
    if profile:
        if not current:
            c.execute('UPDATE serving_profiles SET entry_id=?,revision=revision+1 WHERE entry_id=?',(target_id,duplicate_id))
        else:
            c.execute('UPDATE serving_profiles SET revision=revision+1 WHERE entry_id=?',(target_id,))
            c.execute('DELETE FROM serving_profiles WHERE entry_id=?',(duplicate_id,))
    if tracking:
        current_tracking=c.execute('SELECT * FROM library_tracking WHERE entry_id=?',(target_id,)).fetchone()
        chosen=dict(current_tracking or tracking)
        # A surviving explicit choice remains authoritative. The absorbed choice
        # is retained above for review; only an absent target inherits monitoring.
        chosen['monitored']=bool(chosen['monitored'])
        chosen['revision']=max(chosen['revision'],tracking['revision'])+1
        observation=json.loads(chosen['observation_json']);observation.update(download_state='unknown',coverage_state='verification')
        c.execute('INSERT OR REPLACE INTO library_tracking VALUES(?,?,?,?,?,?,?)',
                  (target_id,int(chosen['monitored']),chosen['revision'],chosen['main_manga_id'],json.dumps(observation),'',now_iso()))
        c.execute('DELETE FROM library_tracking WHERE entry_id=?',(duplicate_id,))
