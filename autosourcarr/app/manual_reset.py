"""Explicit reset of user decisions, retaining current library identities and history."""
import hashlib
import json
from contextlib import contextmanager

from .config import settings
from .db import now_iso

TABLES = {
    'manual_match_bindings':'Manual source matches', 'manual_match_groups':'Manual match groups',
    'preferred_title_overrides':'Preferred titles', 'manual_title_scopes':'Title scopes',
    'preferred_title_override_sources':'Legacy title overrides', 'manual_separate_records':'Separate-entry rules',
    'duplicate_reviews':'Duplicate decisions', 'mixed_entry_reviews':'Mixed-entry reviews',
    'tag_reviews':'Tag matches and separations', 'import_match_rejections':'Rejected import matches',
    'import_manual_autoresolve':'Learned import matches', 'deleted_source_records':'Deleted-source suppression',
    'reset_origins.chapter_candidate_rejections':'Rejected chapters',
}
EXTRA = {
    'manual_aliases':("SELECT * FROM titles WHERE title_type='manual'",'Manual aliases'),
    'title_flags':('SELECT id,preferred_title FROM entries WHERE manual_preferred_title=1','Manually named entries'),
    'skipped_imports':("SELECT id,import_id,decision_revision FROM import_items WHERE state='skipped'",'Skipped imports returned to unresolved'),
    'locked_records':("SELECT 'match' AS kind,entry_id,source_id,source_manga_id,match_type,locked FROM source_matches WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate') UNION ALL SELECT 'variant',entry_id,source_id,source_manga_id,match_type,locked FROM source_variants WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate')",'Manual source locks and splits'),
}


class StaleManualPreview(ValueError): pass


@contextmanager
def transaction(database, write=False):
    with database.catalog() as c:
        if c.in_transaction: raise RuntimeError('Manual reset requires its own transaction.')
        c.execute('ATTACH DATABASE ? AS reset_origins',(str(settings.origins_path),))
        try:
            c.execute('BEGIN IMMEDIATE' if write else 'BEGIN')
            try:
                yield c
                c.commit()
            except BaseException:
                c.rollback();raise
        finally: c.execute('DETACH DATABASE reset_origins')


def snapshot(c):
    digest=hashlib.sha256();counts=[]
    queries={key:(f'SELECT * FROM {key}',label) for key,label in TABLES.items()} | EXTRA
    for key,(query,label) in queries.items():
        count=0;digest.update(key.encode())
        # Order independently of row layout/index choice without keeping large
        # descriptions or source JSON in memory. Per-row digests are fixed size.
        rows=[]
        for row in c.execute(query):
            rows.append(hashlib.sha256(json.dumps(list(row),ensure_ascii=True).encode()).digest());count+=1
        for row in sorted(rows): digest.update(row)
        counts.append({'key':key,'label':label,'count':count})
    return {'revision':digest.hexdigest(),'counts':counts,'total_records':sum(x['count'] for x in counts),
        'retained':'Library subscriptions, current entries and source ownership, completed merges/splits, added/existing imports, source settings, downloads and history stay intact. This forgets decisions; it does not undo completed library operations.',
        'effect':'Automatic matching can reconsider these records. Skipped imports return to Unresolved; they are not automatically added. Deleted source records may be rediscovered.'}


def preview(service, database):
    with service._catalog_maintenance_lock, transaction(database) as c: return snapshot(c)


def reset(service, database, revision):
    with service._catalog_maintenance_lock, transaction(database,True) as c:
        before=snapshot(c)
        if not revision or revision!=before['revision']: raise StaleManualPreview('Manual decisions changed. Review a new reset preview before confirming.')
        title_ids={int(r[0]) for r in c.execute("SELECT id FROM entries WHERE manual_preferred_title=1 UNION SELECT entry_id FROM preferred_title_overrides UNION SELECT entry_id FROM manual_title_scopes UNION SELECT entry_id FROM titles WHERE title_type='manual'")}
        runs=[r[0] for r in c.execute("SELECT DISTINCT import_id FROM import_items WHERE state='skipped'")]
        stamp=now_iso()
        for table in ('source_matches','source_variants'):
            c.execute(f"""UPDATE {table} SET match_type='auto',locked=0 WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate')
                OR (match_type='separate-anchor' AND EXISTS(SELECT 1 FROM manual_separate_records r WHERE r.source_id={table}.source_id AND r.source_manga_id={table}.source_manga_id))""")
        chapter_entries=[int(r[0]) for r in c.execute('SELECT DISTINCT vc.entry_id FROM reset_origins.virtual_chapters vc JOIN reset_origins.chapter_candidates cc ON cc.virtual_chapter_id=vc.id JOIN reset_origins.chapter_candidate_rejections r ON r.source_id=cc.source_id AND r.source_chapter_id=cc.source_chapter_id')]
        # Restore only rejected candidates still owned by this chapter's entry.
        c.execute("""UPDATE reset_origins.chapter_candidates AS cc SET active=1
          WHERE EXISTS(SELECT 1 FROM reset_origins.chapter_candidate_rejections r WHERE r.source_id=cc.source_id AND r.source_chapter_id=cc.source_chapter_id)
          AND EXISTS(SELECT 1 FROM reset_origins.virtual_chapters vc WHERE vc.id=cc.virtual_chapter_id AND
            (EXISTS(SELECT 1 FROM source_matches sm WHERE sm.entry_id=vc.entry_id AND sm.source_id=cc.source_id AND sm.source_manga_id=cc.source_manga_id AND sm.active=1)
            OR EXISTS(SELECT 1 FROM source_variants sv WHERE sv.entry_id=vc.entry_id AND sv.source_id=cc.source_id AND sv.source_manga_id=cc.source_manga_id AND sv.active=1)))""")
        for table in TABLES: c.execute(f'DELETE FROM {table}')
        c.execute("DELETE FROM titles WHERE title_type='manual'")
        c.execute('UPDATE entries SET manual_preferred_title=0 WHERE manual_preferred_title=1')
        c.execute("INSERT INTO import_manual_autoresolve_state(key,value) VALUES('legacy_history_learning_disabled','1') ON CONFLICT(key) DO UPDATE SET value='1'")
        c.execute("UPDATE import_items SET candidate_ids_json='[]',suggestion_checked=0,suggestion_state='queued',decision_revision=decision_revision+1,updated_at=? WHERE state IN ('unresolved','failed')",(stamp,))
        c.execute("""UPDATE import_items SET state='unresolved', reason='Manual skip reset; review this entry',entry_id=NULL,
          candidate_ids_json='[]',matched_snapshot_json='{}',suggestion_checked=0,suggestion_state='queued',
          decision_revision=decision_revision+1,updated_at=? WHERE state='skipped'""",(stamp,))
        for entry_id in title_ids:
            service._sync_preferred_title_from_bound_records(entry_id)
            service._choose_metadata(entry_id)
        for run in runs: service._recount_import_run(run)
        for entry_id in chapter_entries: service._reconcile_split_chapters(entry_id,origins=c)
        service._invalidate_discovery_caches(hard=False)
    # Neither live upstream calls nor library mutations are part of this reset.
    from . import tag_store
    tag_store.invalidate()
    service._invalidate_duplicate_snapshot()
    return {'reset':True,'cleared':before['counts'],'retained':before['retained']}
