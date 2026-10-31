import asyncio
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from test_v087_recovery import env, source, entry
from app.library_projection import page
from app.library_manager import LibraryManager
from app import serving_profiles
from app.task_registry import TaskRegistry
from app.task_journal import TaskJournal


def test_pages_visit_all_615_library_works_and_filter_before_paging(env):
    service,database=env
    with database.catalog() as c:
        c.executemany('INSERT INTO entries(preferred_title,created_at,updated_at) VALUES(?,?,?)',
            [(f'Work {i:04}', 'now','same') for i in range(615)])
        ids=[r[0] for r in c.execute('SELECT id FROM entries')]
    subscription={'ok':True,'entry_ids':ids}
    cursor=''; visited=[]
    while True:
        result=page(database,subscription,limit=60,sort='title',cursor=cursor)
        assert result['total']==615
        visited.extend(r['id'] for r in result['entries'])
        cursor=result['next_cursor']
        if not cursor: break
    assert len(visited)==len(set(visited))==615
    filtered=page(database,subscription,search='Work 0614',sort='title')
    assert filtered['total']==1 and filtered['entries'][0]['id']==ids[-1]
    with pytest.raises(ValueError,match='Library changed'):
        page(database,subscription,search='changed',cursor=result.get('next_cursor') or page(database,subscription,limit=1)['next_cursor'])


def test_wanted_never_uses_aggregate_count_difference_as_missing_proof(env):
    service,database=env; source(database,'one'); eid=entry(service,'one',1818)
    manager=LibraryManager(database,service,None)
    snapshot={'ok':True,'entry_ids':[eid],'stats_by_entry':{str(eid):{'chapter_count':20,'downloaded_chapter_count':2}}}
    assert page(database,snapshot,scope='missing_downloads')['total']==0
    assert page(database,snapshot,scope='verification')['total']==1
    manager.save_tracking(eid,observation={'download_state':'checked','missing_downloads':0,'coverage_state':'checked','source_gaps':3})
    assert page(database,snapshot,scope='source_gaps')['total']==1
    assert page(database,snapshot,scope='missing_downloads')['total']==0


def test_publication_filters_preserve_the_existing_suwayomi_status_codes(env):
    service,database=env
    with database.catalog() as c:
        c.executemany('INSERT INTO entries(preferred_title,status,created_at,updated_at) VALUES(?,?,?,?)',
                      [('Status '+str(code),code,'now','now') for code in range(7)])
        ids=[r[0] for r in c.execute('SELECT id FROM entries')]
    snapshot={'ok':True,'entry_ids':ids}
    assert {row['status'] for row in page(database,snapshot,publication='finished')['entries']}=={2,4}
    assert {row['status'] for row in page(database,snapshot,publication='hiatus')['entries']}=={6}
    assert {row['status'] for row in page(database,snapshot,publication='licensed')['entries']}=={3}
    from app.service import MANGA_STATUS_LABELS
    for row in page(database,snapshot)['entries']:assert row['status_label']==MANGA_STATUS_LABELS[row['status']]


def test_library_source_count_is_distinct_from_additional_record_count(env):
    service,database=env;source(database,'one');eid=entry(service,'one',7010)
    with database.catalog() as c:
        c.execute('INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(?,?,?,?,?)',
                  (eid,'one',7011,'Same work additional record','now'))
    result=page(database,{'ok':True,'entry_ids':[eid]},source_count='1',record_count='2')
    assert result['total']==1
    assert result['entries'][0]['match_count']==result['entries'][0]['source_count']==1
    assert result['entries'][0]['entry_match_count']==2


def test_profiles_are_revision_checked_and_default_ranking_unchanged(env):
    service,database=env; source(database,'one'); source(database,'two'); eid=entry(service,'one',1919)
    rows=[{'source_id':'one','resolver_score':4},{'source_id':'two','resolver_score':3}]
    serving_profiles.rank(database,eid,rows)
    assert [r['resolver_score'] for r in rows]==[4,3]
    serving_profiles.save(database,service,eid,{'sources':['two']},0)
    serving_profiles.rank(database,eid,rows)
    assert rows[1]['resolver_score']>rows[0]['resolver_score']
    with pytest.raises(ValueError,match='changed'): serving_profiles.save(database,service,eid,{},0)


@pytest.mark.asyncio
async def test_membership_reconciles_lost_response_and_never_deletes_downloads(env):
    service,database=env; source(database,'one'); eid=entry(service,'one',2020)
    state={'id':44,'sourceId':'auto','url':str(eid),'inLibrary':True}
    async def mutation(mid,value):
        state['inLibrary']=value
        raise TimeoutError('response lost after commit')
    client=SimpleNamespace(graphql=AsyncMock(side_effect=lambda *a,**k:{'manga':dict(state)}),set_manga_in_library=mutation)
    service._main_autosourcarr_source_id=AsyncMock(return_value='auto')
    service._main_manga_for_autosourcarr_entry=AsyncMock(return_value={'id':44})
    service.subbed_entry_ids=AsyncMock(return_value={'ok':True})
    manager=LibraryManager(database,service,client)
    result=await manager.membership(eid,False,False,0)
    assert result['reconciled'] and result['in_library'] is False
    assert not result['monitored']
    with pytest.raises(ValueError,match='changed'): await manager.membership(eid,False,False,0)


@pytest.mark.asyncio
async def test_explicit_removal_reconciles_same_work_aliases_and_keeps_other_sources(env):
    service,database=env;source(database,'one');eid=entry(service,'one',2021)
    states={mid:{'id':mid,'sourceId':'auto' if mid<12 else 'original-source','url':str(eid),'inLibrary':True} for mid in (10,11,12)}
    async def graphql(query,variables=None):return {'manga':dict(states[variables['id']])}
    async def mutate(mid,value):states[mid]['inLibrary']=value;return dict(states[mid])
    async def library(**kwargs):return {'ok':True,'items':[dict(row) for row in states.values()],
          'entry_ids':[eid] if any(row['sourceId']=='auto' and row['inLibrary'] for row in states.values()) else []}
    service._main_autosourcarr_source_id=AsyncMock(return_value='auto')
    service._main_manga_for_autosourcarr_entry=AsyncMock(return_value={'id':10})
    service.subbed_entry_ids=library
    manager=LibraryManager(database,service,SimpleNamespace(graphql=graphql,set_manga_in_library=mutate))
    result=await manager.membership(eid,False,False,0)
    assert result['in_library'] is False
    assert not states[10]['inLibrary'] and not states[11]['inLibrary']
    assert states[12]['inLibrary']


@pytest.mark.asyncio
async def test_download_request_skips_downloaded_and_existing_queue(env):
    service,database=env; source(database,'one'); eid=entry(service,'one',2121)
    client=SimpleNamespace(graphql=AsyncMock())
    manager=LibraryManager(database,service,client)
    manager.exact_manga=AsyncMock(return_value={'id':10,'inLibrary':True})
    manager.chapters=AsyncMock(return_value=[{'id':1,'isDownloaded':True},{'id':2,'isDownloaded':False},{'id':3,'isDownloaded':False}])
    manager.queue=AsyncMock(side_effect=[{'queue':[{'chapter':{'id':2}}]}, {'queue':[{'chapter':{'id':2}},{'chapter':{'id':3}}]}])
    assert (await manager.enqueue_missing(eid))['queued']==1
    assert client.graphql.call_args.args[1]['input']['ids']==[3]


@pytest.mark.asyncio
async def test_queue_control_reconciles_a_lost_mutation_response_once(env):
    service,database=env
    state={'state':'STOPPED','queue':[{'chapter':{'id':2},'manga':{'id':1},'position':0,'progress':0,'state':'QUEUED'}]}
    mutations=[]
    async def graphql(query,variables=None):
        if query.startswith('mutation'):
            mutations.append(variables);state['state']='STARTED';raise TimeoutError('Reply lost after enqueue controller accepted')
        return {'downloadStatus':state}
    manager=LibraryManager(database,service,SimpleNamespace(graphql=graphql))
    before=await manager.queue();result=await manager.queue_action('start',before['revision'])
    assert result['state']=='STARTED' and result['reconciled'] and len(mutations)==1


@pytest.mark.asyncio
async def test_malformed_queue_identity_is_not_an_empty_success(env):
    service,database=env
    manager=LibraryManager(database,service,SimpleNamespace(graphql=AsyncMock(return_value={'downloadStatus':{'state':'STOPPED','queue':[{'chapter':None}]}})))
    with pytest.raises(ValueError,match='identity'):await manager.queue()


@pytest.mark.asyncio
async def test_durable_activity_recovers_interrupted_and_keeps_cancelled(env):
    service,database=env
    first=TaskRegistry(); journal=TaskJournal(database,first); await journal.start()
    running=first.create('Safe read refresh',kind='library-monitor',context='1')
    cancelled=first.create('Cancelled'); first.cancel(cancelled)
    await journal.stop()
    second=TaskRegistry(); recovered=TaskJournal(database,second); await recovered.start()
    rows={r['id']:r for r in second.snapshot()['items']}
    assert rows[running]['interrupted'] and rows[running]['state']=='failed'
    assert rows[cancelled]['cancel_requested'] and not rows[cancelled]['interrupted']
    assert not any(r['cancellable'] for r in rows.values())
    second.clear_history(); await recovered.stop()
    third=TaskRegistry(); empty=TaskJournal(database,third); await empty.start()
    assert not third.snapshot()['items']
    await empty.stop()


def test_general_history_is_bounded_and_pageable():
    registry=TaskRegistry()
    running=registry.create('Owned running task')
    for i in range(350):
        task=registry.create('History '+str(i));registry.complete(task)
    assert registry.is_running(running)
    assert registry.history()['total']==300
    assert len(registry.history(offset=50)['items'])==50


@pytest.mark.asyncio
async def test_corrupt_history_is_retained_without_preventing_other_work(env):
    service,database=env
    with database.catalog() as c:c.execute('INSERT INTO general_task_history VALUES(?,?,?)',('bad','invalid JSON','now'))
    registry=TaskRegistry();journal=TaskJournal(database,registry);await journal.start()
    assert registry.snapshot()['history_error'] and not registry.snapshot()['durable']
    registry.create('Library remains available');await journal.stop()
    with database.catalog() as c:assert c.execute('SELECT record_json FROM general_task_history WHERE id=\'bad\'').fetchone()[0]=='invalid JSON'


def test_catalog_merge_preserves_profiles_monitoring_and_conflicting_choices(env):
    service,database=env;source(database,'one');source(database,'two')
    a=entry(service,'one',7001,'One work');b=entry(service,'two',7002,'One work')
    serving_profiles.save(database,service,a,{'languages':['en']},0)
    serving_profiles.save(database,service,b,{'languages':['ja']},0)
    manager=LibraryManager(database,service,None);manager.save_tracking(b,monitored=True)
    canonical=service.review_duplicate(a,b,'duplicate')['canonical_entry_id']
    saved=serving_profiles.read(database,canonical)
    languages={tuple(saved['profile']['languages']),*(tuple(r['profile']['languages']) for r in saved['retained'])}
    assert languages=={('en',),('ja',)}
    assert manager.tracking(canonical)['monitored']
    assert manager.tracking(canonical)['observation']['download_state']=='unknown'


def test_ordinary_rebuild_retains_preferences_and_full_reset_explicitly_clears_them(env):
    service,database=env;source(database,'one');eid=entry(service,'one',7003)
    serving_profiles.save(database,service,eid,{'languages':['en']},0)
    service.reset_database()
    with database.catalog() as c:assert c.execute('SELECT entry_id FROM serving_profiles').fetchone()[0]==eid
    service.full_reset_database()
    with database.catalog() as c:assert c.execute('SELECT COUNT(*) FROM serving_profiles').fetchone()[0]==0
