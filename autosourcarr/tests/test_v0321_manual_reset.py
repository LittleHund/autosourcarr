import asyncio
from unittest.mock import AsyncMock

import httpx
import pytest
from app import manual_reset, tag_store
from app.db import now_iso
from test_v087_recovery import env, entry, source


def seed(env,monkeypatch):
    s,d=env;monkeypatch.setattr(tag_store,'db',d);source(d,'source')
    eid=entry(s,'source',1,'Original title');other=entry(s,'source',2,'Other work')
    stamp=now_iso()
    s.set_preferred_title(eid,'Manual title')
    with d.catalog() as c:
        c.execute("UPDATE source_matches SET locked=1,match_type='manual' WHERE entry_id=?",(eid,))
        c.execute("INSERT INTO manual_match_groups VALUES('g','group',?,?)",(stamp,stamp))
        c.execute("INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,created_at,updated_at) VALUES('source',1,'g',?,?,?)",(eid,stamp,stamp))
        c.execute("INSERT INTO duplicate_reviews VALUES(?,?,'different',?)",(eid,other,stamp))
        c.execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(?,?,'different',?)",(eid,other,stamp))
        c.execute("INSERT INTO mixed_entry_reviews VALUES('fingerprint',?,?)",(eid,stamp))
        c.execute("INSERT INTO tags(id,namespace,name,normalized) VALUES(991,'tag','a','a'),(992,'tag','b','b')")
        c.execute("INSERT INTO tag_reviews VALUES(991,992,'same',?)",(stamp,))
        c.execute("INSERT INTO import_runs(id,state,total,added,skipped,created_at) VALUES('run','completed',2,1,1,?)",(stamp,))
        for position,state in [(0,'added'),(1,'skipped')]:
            c.execute('INSERT INTO import_items(import_id,position,state,entry_id,updated_at) VALUES(?,?,?,?,?)',('run',position,state,eid if state=='added' else None,stamp))
        c.execute("INSERT INTO import_manual_autoresolve(backup_key,created_at,updated_at) VALUES('backup',?,?)",(stamp,stamp))
        c.execute("INSERT INTO import_match_rejections(backup_key,signature,rejected_at) VALUES('backup','sig',?)",(stamp,))
        c.execute("INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,created_at,updated_at) VALUES(?,'source',2,?,?)",(eid,stamp,stamp))
    with d.origins() as o:
        vc=o.execute("INSERT INTO virtual_chapters(entry_id,chapter_key,display_name,created_at,updated_at) VALUES(?,'1','Chapter 1',?,?)",(eid,stamp,stamp)).lastrowid
        for mid,cid in [(1,11),(2,22)]:
            o.execute("INSERT INTO chapter_candidates(virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,active,first_seen,last_seen) VALUES(?,'source','Source',?,?,'chapter',0,?,?)",(vc,mid,cid,stamp,stamp))
            o.execute("INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES('source',?,?)",(cid,stamp))
        candidate=o.execute('SELECT id FROM chapter_candidates WHERE source_chapter_id=11').fetchone()[0]
        o.execute("INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES(?,?,'source','Source',1,11,?,'history')",(vc,candidate,stamp))
    return s,d,eid


def test_reset_all_preserves_library_and_history_and_restores_only_owned_chapters(env,monkeypatch):
    s,d,eid=seed(env,monkeypatch)
    before=manual_reset.preview(s,d)
    assert before['total_records']>10
    assert s.entry_preferred_title(eid)=='Manual title'  # preview has no writes
    result=manual_reset.reset(s,d,before['revision']);assert result['reset']
    assert manual_reset.preview(s,d)['total_records']==0
    with d.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0]==2
        assert c.execute('SELECT preferred_title FROM entries WHERE id=?',(eid,)).fetchone()[0]=='Original title'
        assert c.execute('SELECT COUNT(*) FROM duplicate_review_history').fetchone()[0]==1
        assert c.execute('SELECT COUNT(*) FROM tags WHERE id IN (991,992)').fetchone()[0]==2
        assert c.execute("SELECT value FROM import_manual_autoresolve_state WHERE key='legacy_history_learning_disabled'").fetchone()[0]=='1'
        assert [tuple(r) for r in c.execute('SELECT state,decision_revision FROM import_items ORDER BY position')]==[('added',0),('unresolved',1)]
        assert tuple(c.execute('SELECT added,unresolved,skipped FROM import_runs').fetchone())==(1,1,0)
        assert tuple(c.execute('SELECT locked,match_type FROM source_matches WHERE entry_id=?',(eid,)).fetchone())==(0,'auto')
    with d.origins() as o:
        assert [r[0] for r in o.execute('SELECT active FROM chapter_candidates ORDER BY source_chapter_id')]==[1,0]
        assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0]==1


def test_stale_preview_and_cross_database_failure_roll_back(env,monkeypatch):
    s,d,eid=seed(env,monkeypatch);before=manual_reset.preview(s,d)
    with d.catalog() as c:c.execute("UPDATE duplicate_reviews SET decision='same'")
    with pytest.raises(manual_reset.StaleManualPreview):manual_reset.reset(s,d,before['revision'])
    fresh=manual_reset.preview(s,d)
    def broken(*args):raise RuntimeError('injected local title failure')
    monkeypatch.setattr(s,'_choose_metadata',broken)
    with pytest.raises(RuntimeError,match='injected'):manual_reset.reset(s,d,fresh['revision'])
    assert manual_reset.preview(s,d)==fresh
    with d.origins() as o:assert all(not r[0] for r in o.execute('SELECT active FROM chapter_candidates'))


def test_reset_api_admission_confirmation_and_drain(env,monkeypatch):
    from app import main
    s,d,eid=seed(env,monkeypatch);monkeypatch.setattr(main,'service',s);monkeypatch.setattr(main,'db',d)
    monkeypatch.setattr(main.source_attention,'start',lambda *a:None)
    monkeypatch.setattr(s,'schedule_continuous_catalog_discovery',lambda:False)
    monkeypatch.setattr(s,'end_automatic_duplicate_pause',lambda *a,**k:None)
    drained=[]
    async def drain():drained.append(True)
    monkeypatch.setattr(s,'cancel_background_tasks',drain)
    async def scenario():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://test') as client:
            route='/api/admin/maintenance/reset-all-manual-actions'
            p=(await client.get(route)).json()
            bad=await client.post(route,json={'revision':p['revision'],'confirmation':'no'})
            assert bad.status_code==400 and not drained
            # If middleware counts the reset itself, this times out/deadlocks.
            response=await asyncio.wait_for(client.post(route,json={'revision':p['revision'],'confirmation':'RESET ALL MANUAL ACTIONS'}),3)
            assert response.status_code==200,response.text
            assert drained==[True] and main._catalog_http_requests==0 and not main._catalog_reset_active
    asyncio.run(scenario())


def test_reset_reopens_import_suggestions_and_preserves_fast_feeds(env,monkeypatch):
    s,d,eid=seed(env,monkeypatch)
    with d.catalog() as c:
        for pos,state in [(2,'unresolved'),(3,'failed')]:
            c.execute("INSERT INTO import_items(import_id,position,state,candidate_ids_json,suggestion_checked,suggestion_state,updated_at) VALUES('run',?,?,'[1]',1,'done',?)",(pos,state,now_iso()))
        c.execute("INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('old',9,?)",(now_iso(),))
        c.execute("INSERT INTO preferred_title_override_sources VALUES('source',1,'Legacy manual',?)",(now_iso(),))
        c.execute("INSERT INTO browse_cache(cache_key,mode,page,updated_at,last_accessed) VALUES('feed','POPULAR',1,?,?)",(now_iso(),now_iso()))
    manual_reset.reset(s,d,manual_reset.preview(s,d)['revision'])
    with d.catalog() as c:
        assert [tuple(r) for r in c.execute('SELECT state,candidate_ids_json,suggestion_checked,suggestion_state,decision_revision FROM import_items WHERE position>=2 ORDER BY position')]==[('unresolved','[]',0,'queued',1),('failed','[]',0,'queued',1)]
        assert c.execute('SELECT COUNT(*) FROM deleted_source_records').fetchone()[0]==0
        assert c.execute('SELECT COUNT(*) FROM preferred_title_override_sources').fetchone()[0]==0
        assert c.execute("SELECT updated_at FROM browse_cache WHERE cache_key='feed'").fetchone()[0].startswith('1970')
