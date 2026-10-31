import asyncio
import json

import httpx
import pytest
from fastapi import FastAPI

from app.config import settings
from app.db import Database
from app.service import AutoSourcarrService
import app.service as module
from app import matching_policies as policies, matching_review as review

TITLE = 'The Last Explorer of the Silver Kingdom'
DESCRIPTION = 'A young explorer leaves the silver kingdom to seek a lost friend in distant mountains. Together they uncover the ancient history of the kingdom and protect its people.'


@pytest.fixture
def env(tmp_path, monkeypatch):
    monkeypatch.setattr(settings,'data_dir',tmp_path)
    monkeypatch.setattr(settings,'database_dir',None)
    monkeypatch.setattr(settings,'metadata_identity_enabled',False)
    monkeypatch.setattr(settings,'automatic_duplicate_matching',False)
    monkeypatch.setattr(settings,'exact_thumbnail_matching',False)
    database=Database();monkeypatch.setattr(module,'db',database)
    service=AutoSourcarrService()
    with database.catalog() as c:
        for sid in ['a','b','c']:
            c.execute('INSERT INTO source_config(source_id,name,lang,base_url,last_seen) VALUES(?,?,?,?,?)',(sid,sid,'en','https://example.invalid','now'))
    service._reload_source_cache()
    yield service,database
    database.close_current_thread()


def payload(sid,mid=1,**changes):
    return {'id':mid,'sourceId':sid,'title':TITLE,'description':DESCRIPTION,'author':'Author One','genre':['Action','Adventure'],'url':f'/manga/{mid}',**changes}


def entry(service,sid,mid=1,**changes):
    p=payload(sid,mid,**changes);eid=service._create_entry(p,sid);service._upsert_match(eid,sid,p,1)
    return eid


def save(env,scope,sid,direction,rule):
    service,database=env
    return review.save(database,service,scope,str(sid),direction,review.PolicyUpdate(revision=review.configuration(database,service)['revision'],rule=rule))


def off(env,sid,direction):
    save(env,'source',sid,direction,{'enabled':False,'checks':{}})


def test_internal_and_external_rules_are_independent_and_two_sided(env):
    service,database=env;eid=entry(service,'a')
    assert service._local_match_candidates(payload('a',2),'a')
    off(env,'a','internal')
    assert not service._local_match_candidates(payload('a',2),'a')
    assert service._local_match_candidates(payload('b',2),'b')
    off(env,'a','external')
    assert not service._local_match_candidates(payload('b',2),'b')
    assert service._apply_source_detail_if_current(eid,'a',1,payload('a',1))  # existing identity refresh


def test_source_overrides_group_and_restore_inheritance(env):
    service,database=env;gid=service.create_source_group('Group')['id']
    service.update_source('a',group_id=gid);service.update_source('b',group_id=gid)
    entry(service,'a')
    save(env,'group',gid,'external',{'enabled':False,'checks':{}})
    save(env,'source','a','external',{'enabled':True,'checks':{'title':1}})
    assert not service._local_match_candidates(payload('b',2),'b')  # b still inherits disabled
    save(env,'source','b','external',{'enabled':True,'checks':{'title':1}})
    assert service._local_match_candidates(payload('b',2),'b')
    save(env,'source','b','external',None)
    assert not service._local_match_candidates(payload('b',2),'b')


def test_required_evidence_missing_and_threshold_failures(env):
    service,database=env;entry(service,'a')
    save(env,'source','b','external',{'enabled':True,'checks':{'title':1,'author':.95,'tags':1}})
    assert service._local_match_candidates(payload('b',2),'b')
    assert not service._local_match_candidates(payload('b',2,author=''),'b')
    assert not service._local_match_candidates(payload('b',2,genre=['Action']),'b')


@pytest.mark.parametrize('rule',[{'enabled':True,'checks':{'title':.93}}, {'enabled':True,'checks':{}}, {'enabled':True,'checks':{'unknown':1}}, {'enabled':True,'checks':{'summary':float('nan')}}])
def test_invalid_rules_rejected_without_changing_saved_rules(env,rule):
    with pytest.raises(ValueError):save(env,'source','a','external',rule)
    assert not policies.rules(env[1])


def test_rule_edit_conflict_preserves_newer_choice(env):
    service,database=env;old=review.configuration(database,service)['revision'];off(env,'a','internal')
    with pytest.raises(Exception) as error:
        review.save(database,service,'source','a','internal',review.PolicyUpdate(revision=old,rule=None))
    assert error.value.status_code==409
    assert policies.effective(policies.rules(database),service._source_cache,'a','internal')[0]['enabled'] is False


def test_delayed_variant_commit_rechecks_all_sources_not_only_anchor(env):
    service,database=env;eid=entry(service,'a')
    service._upsert_match(eid,'b',payload('b',2),1)
    off(env,'b','internal')
    assert not service._attach_automatic_record_if_current(eid,'b',payload('b',3),1,'auto-variant',variant_only=True)
    assert service._find_existing_by_source('b',3) is None


def test_duplicate_and_import_paths_obey_policies(env):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2)
    assert service._auto_merge_group_evidence(left,right)['eligible']
    off(env,'b','external')
    assert not service._auto_merge_group_evidence(left,right)['eligible']
    assert not service._import_candidate_evidence({**payload('b',2),'source':'b'},left)['eligible']


def test_summary_template_cannot_merge_unrelated_titles(env):
    service,database=env
    left=entry(service,'a',title='Colorful Garden')
    right=entry(service,'b',2,title='A Festival in the Snow')
    save(env,'source','a','external',{'enabled':True,'checks':{'summary':.99}})
    result=service._auto_merge_evidence(left,right)
    assert not result['eligible'] and result['summary_similarity']==1


def test_history_records_correct_survivor_and_immutable_evidence(env,monkeypatch):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2)
    monkeypatch.setattr(service,'_entry_reliability',lambda eid:(1 if eid==right else 0,))
    assert service._merge_entries_automatic(left,right,'test direct proof')==right
    rows=review.history(database,service);assert rows['total']==1
    event=rows['items'][0];detail=review.detail(database,service,event['id'])
    assert detail['before'][0]['entry']['id']==right
    assert detail['before'][1]['entry']['id']==left
    before=detail['evidence'];off(env,'a','external')
    assert review.detail(database,service,event['id'])['evidence']==before
    assert event['current_entry_id']==right and event['state']=='Recorded merge'


def test_history_survives_failure_after_core_merge_commit(env,monkeypatch):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2)
    monkeypatch.setattr(service,'_merge_entry_origins',lambda *a:(_ for _ in ()).throw(RuntimeError('secondary write failed')))
    with pytest.raises(RuntimeError):service._merge_entries_automatic(left,right,'saved before failure')
    assert review.history(database,service)['total']==1
    with database.catalog() as c:assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0]==1


def test_history_reads_reversed_legacy_snapshots_and_follows_later_merges(env):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2)
    service._merge_entries_automatic(left,right,'first merge')
    with database.catalog() as c:
        c.execute('UPDATE automatic_merge_events SET target_snapshot_json=duplicate_snapshot_json,duplicate_snapshot_json=target_snapshot_json')
    current=service.canonical_entry_id(left);third=entry(service,'c',3)
    new=service.merge_entries(current,third,'manual-review')
    page=review.history(database,service,query=TITLE)
    assert page['total']==1 and page['items'][0]['current_entry_id']==new
    assert review.detail(database,service,page['items'][0]['id'])['before'][0]['entry']['id']==current
    assert review.history(database,service,offset=1)['items']==[]


def test_history_api_contract_and_auth(env):
    service,database=env
    async def admin():return True
    app=FastAPI();app.include_router(review.router(database,service,admin))
    async def run():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
            assert (await client.get('/api/admin/duplicates/history')).json()['total']==0
            assert (await client.get('/api/admin/duplicates/history?limit=500')).status_code==422
            assert (await client.get('/api/admin/duplicates/history/123')).status_code==404
            config=(await client.get('/api/admin/matching-policies')).json()
            response=await client.put('/api/admin/matching-policies/source/a/internal',json={'revision':config['revision'],'rule':{'enabled':False,'checks':{}}})
            assert response.status_code==200
    asyncio.run(run())


@pytest.mark.parametrize('bad', [{'entry':['bad']}, {'entry':{'id':1},'titles':None}, {'entry':{'id':1},'matches':None,'variants':42}])
def test_history_tolerates_malformed_legacy_nested_fields(env,bad):
    service,database=env
    with database.catalog() as c:
        event=c.execute("INSERT INTO automatic_merge_events(target_entry_id,duplicate_entry_id,reason,target_snapshot_json,duplicate_snapshot_json,merged_at) VALUES(1,2,'legacy',?,'{}','now')",(json.dumps(bad),)).lastrowid
    result=review.detail(database,service,event)
    assert result['current_entry_id'] is None
    assert all(isinstance(snapshot['records'],list) for snapshot in result['before'])
    assert result['before'][1]['role']=='Original entry'


def test_policy_survives_preference_restore_with_new_group_and_source_ids(env):
    service,database=env;gid=service.create_source_group('My group')['id']
    service.update_source('a',group_id=gid)
    save(env,'group',gid,'external',{'enabled':False,'checks':{}})
    save(env,'source','a','internal',{'enabled':True,'checks':{'title':1}})
    snapshot=service.capture_source_preferences()
    with database.catalog() as c:
        c.execute('DELETE FROM matching_policies');c.execute('DELETE FROM source_groups')
        c.execute("UPDATE source_config SET source_id='new-a',group_id=NULL WHERE source_id='a'")
        c.execute("INSERT INTO source_groups(id,name,created_at,updated_at) VALUES(100,'New first group','now','now')")
    policies.invalidate(database)
    service.restore_source_preferences(snapshot)
    rules=policies.rules(database);new_gid=service.get_source('new-a')['group_id']
    assert new_gid!=gid
    assert rules[('source','new-a','internal')]['checks']['title']==1
    assert rules[('group',str(new_gid),'external')]['enabled'] is False
    service.delete_source_group(new_gid)
    assert ('group',str(new_gid),'external') not in policies.rules(database)


def test_gallery_same_source_policy_does_not_retroactively_split_existing_parts(env):
    service,database=env
    with database.catalog() as c:c.execute("UPDATE source_config SET name='IMHentai' WHERE source_id='a'")
    service._reload_source_cache()
    ids=[service.ingest_manga('a',payload('a',n,title=f'Quiet Garden Adventures Chapter {n}',genre=['artist:Sample Artist','fantasy','adventure']))[0] for n in range(1,5)]
    assert len(set(ids))==1
    off(env,'a','internal')
    result=service._repair_unsafe_automatic_source_attachments()
    assert result['split_records']==0
    extra=service.ingest_manga('a',payload('a',5,title='Quiet Garden Adventures Chapter 5',genre=['artist:Sample Artist','fantasy','adventure']))[0]
    assert extra!=ids[0]


def test_import_policy_precedes_generic_existing_source_and_metadata_write(env,monkeypatch):
    service,database=env;eid=entry(service,'a');off(env,'a','internal')
    monkeypatch.setattr(service,'_current_source_id_for_backup',lambda *a:'a')
    applied=[];monkeypatch.setattr(service,'_apply_import_metadata_to_entry',lambda *a:applied.append(a))
    item={**payload('a',2),'source':'a'}
    with pytest.raises(policies.PolicyBlocked):asyncio.run(service._bind_backup_source_to_entry(eid,item))
    assert not applied
    # Explicit manual resolve and proven exact source URL continue to work.
    assert asyncio.run(service._bind_backup_source_to_entry(eid,item,manual=True))['bound']
    assert asyncio.run(service._bind_backup_source_to_entry(eid,{**payload('a',1),'source':'a'}))['bound']
    assert len(applied)==2


def test_import_merge_cannot_bring_other_owner_sources_past_rules(env,monkeypatch):
    from unittest.mock import AsyncMock
    service,database=env;target=entry(service,'a');owner=entry(service,'b',2)
    service._upsert_match(owner,'c',payload('c',3),1)
    off(env,'c','external')
    monkeypatch.setattr(service,'_current_source_id_for_backup',lambda *a:'b')
    monkeypatch.setattr(module.source_host,'fetch_source_manga',AsyncMock(return_value={'mangas':[payload('b',2)]}))
    monkeypatch.setattr(module.source_host,'fetch_manga_and_chapters',AsyncMock(return_value={'manga':payload('b',2)}))
    with pytest.raises(policies.PolicyBlocked):asyncio.run(service._bind_backup_source_to_entry(target,{**payload('b',2),'source':'b'}))
    assert service.canonical_entry_id(target)!=service.canonical_entry_id(owner)
    assert review.history(database,service)['total']==0


def test_current_rules_cover_sources_absent_from_old_frozen_lineage(env):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2);third=entry(service,'c',3)
    service._merge_entries_automatic(left,right,'first')
    merged=service.canonical_entry_id(left)
    with database.catalog() as c:
        for r in c.execute('SELECT rowid,snapshot_json FROM automatic_merge_lineage').fetchall():
            value=json.loads(r['snapshot_json']);value.pop('source_ids',None);value.pop('policy_payloads',None)
            c.execute('UPDATE automatic_merge_lineage SET snapshot_json=? WHERE rowid=?',(json.dumps(value),r['rowid']))
    off(env,'b','external')
    assert not service._auto_merge_group_evidence(merged,third)['eligible']


def test_recorded_proof_is_the_actual_multi_generation_decision(env):
    service,database=env;left=entry(service,'a');right=entry(service,'b',2);third=entry(service,'c',3)
    merged=service._merge_entries_automatic(left,right,'first')
    decision=service._auto_merge_group_evidence(merged,third)
    assert decision['eligible'] and decision['direct_pairs']==2
    service._merge_entries_automatic(merged,third,decision['reason'],decision=decision)
    event=review.history(database,service)['items'][0]
    evidence=review.detail(database,service,event['id'])['evidence']
    assert evidence['pair_evidence']==decision['pair_evidence']
    assert evidence['direct_pairs']==2


def test_admin_guard_protects_new_read_and_write_routes(env):
    from fastapi import HTTPException
    service,database=env
    def denied():raise HTTPException(401,'Unauthorized')
    app=FastAPI();app.include_router(review.router(database,service,denied))
    async def run():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
            for path in ['/duplicates/history','/duplicates/history/1','/matching-policies']:
                assert (await client.get('/api/admin'+path)).status_code==401
            assert (await client.put('/api/admin/matching-policies/source/a/internal',json={'revision':'bad','rule':None})).status_code==401
    asyncio.run(run())


def test_required_checks_use_later_source_native_metadata_without_weakening_anchor(env):
    service,database=env;eid=entry(service,'a')
    service._upsert_match(eid,'b',payload('b',2),1,'auto')
    save(env,'source','b','internal',{'enabled':True,'checks':{'title':1,'author':1}})
    assert service._local_match_candidates(payload('b',3),'b')[0][0]==eid
    assert service.ingest_manga('b',payload('b',3))[0]==eid
    # Absent B author cannot be borrowed from source A's aggregate evidence.
    service._upsert_match(eid,'b',payload('b',2,author='',artist=''),1,'auto')
    with database.catalog() as c:c.execute("UPDATE source_variants SET author='',artist='',metadata_json='{}' WHERE source_id='b'")
    assert not service._local_match_candidates(payload('b',4),'b')
    assert not policies.apply(database,service._source_cache,{'eligible':True,'author_similarity':1},[payload('b')],[],right_ids={'b'})['eligible']


def test_policy_edit_cannot_race_synchronous_ingest_commit(env,monkeypatch):
    import threading
    from concurrent.futures import ThreadPoolExecutor
    service,database=env;eid=entry(service,'a')
    shortlisted=threading.Event();release=threading.Event();edit_started=threading.Event()
    original=service._local_match_candidates
    def delayed(*args):
        result=original(*args);shortlisted.set();assert release.wait(3);return result
    monkeypatch.setattr(service,'_local_match_candidates',delayed)
    def edit():edit_started.set();off(env,'a','external')
    with ThreadPoolExecutor(max_workers=2) as pool:
        ingest=pool.submit(service.ingest_manga,'b',payload('b',2))
        assert shortlisted.wait(3)
        editing=pool.submit(edit);assert edit_started.wait(3)
        assert not editing.done()  # save cannot return while pre-edit match is uncommitted
        release.set();assert ingest.result(3)[0]==eid;editing.result(3)
    monkeypatch.setattr(service,'_local_match_candidates',original)
    assert service.ingest_manga('c',payload('c',3))[0]!=eid


@pytest.mark.parametrize('outcome',['error','empty','invalid'])
def test_import_rechecks_policy_after_deferred_source_lookup(env,monkeypatch,outcome):
    service,database=env;eid=entry(service,'a')
    monkeypatch.setattr(service,'_current_source_id_for_backup',lambda *a:'b')
    async def lookup(*args):
        off(env,'b','external')
        if outcome=='error':raise RuntimeError('Source temporarily unavailable')
        return {'mangas':[] if outcome=='empty' else [payload('b',0)]}
    monkeypatch.setattr(module.source_host,'fetch_source_manga',lookup)
    with pytest.raises(policies.PolicyBlocked):asyncio.run(service._bind_backup_source_to_entry(eid,{**payload('b',2),'source':'b'}))
    assert service._find_existing_by_source('b',2) is None
