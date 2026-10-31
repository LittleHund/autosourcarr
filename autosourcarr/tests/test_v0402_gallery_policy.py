import copy
import pytest
from test_v087_recovery import env,source
from app import gallery_policy as policy


def setup(env,name='E-Hentai'):
    service,database=env;source(database,'17')
    with database.catalog() as c:c.execute('UPDATE source_config SET name=? WHERE source_id=?',(name,'17'))
    service._reload_source_cache()
    return service,database


def payload(mid,part,language='English'):
    return dict(id=mid,sourceId='17',title=f'[Sample Artist] Quiet Garden Part {part} [{language}]',url=f'/gallery/{mid}',initialized=True,
                genre=['artist:Sample Artist','fantasy','adventure'])


def save(database,service,**changes):
    state=policy.read(database,'17');value=copy.deepcopy(state['policy']);value.update(changes)
    return policy.save(database,service,'17',value,state['revision'])


def test_per_source_off_keeps_new_gallery_records_separate(env):
    service,database=setup(env)
    save(database,service,enabled=False)
    a=service.ingest_manga('17',payload(11,1))[0];b=service.ingest_manga('17',payload(12,2))[0]
    assert a!=b
    assert not service._auto_merge_evidence(a,b)['eligible']
    with pytest.raises(ValueError,match='gallery|Gallery'):service.review_duplicate(a,b,'duplicate')


def test_enabled_uses_existing_chapter_family_and_selected_language_check(env):
    service,database=setup(env)
    save(database,service,enabled=True,criteria=['language'])
    a=service.ingest_manga('17',payload(11,1))[0]
    assert service.ingest_manga('17',payload(12,2))[0]==a
    assert service.ingest_manga('17',payload(13,3,'Japanese'))[0]!=a
    assert not service._gallery_evidence(service._clean_source_manga_payload('17',payload(14,4,'Japanese')),
                [service._clean_source_manga_payload('17',payload(11,1))])['eligible']


def test_explicit_gallery_profile_supports_other_sources(env):
    service,database=setup(env,'Other Gallery Provider')
    save(database,service,profile='gallery',enabled=False)
    assert service._clean_source_manga_payload('17',payload(11,1))['_gallery_metadata']['chapter_number']==1
    a=service.ingest_manga('17',payload(11,1))[0];b=service.ingest_manga('17',payload(12,2))[0]
    assert a!=b


def test_disable_separates_cached_gallery_records_without_losing_pages_or_served_ids(env):
    service,database=setup(env)
    a=service.ingest_manga('17',payload(11,1))[0]
    assert service.ingest_manga('17',payload(12,2))[0]==a
    for mid in (11,12):
        service._ingest_chapters(a,{'source_id':'17','source_manga_id':mid,'source_name':'E-Hentai'},service._match_id(a,'17'),
          [{'id':mid*10,'url':f'/chapter/{mid}','name':'Chapter0','chapterNumber':0,'pageCount':1}],effects={'invalidated':set()})
    with database.origins() as o:
        row=o.execute('SELECT id,virtual_chapter_id FROM chapter_candidates WHERE source_manga_id=12').fetchone()
        cid,vid=row['id'],row['virtual_chapter_id']
        o.execute('INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(?,?,?,?)',(cid,0,'https://fixture.invalid/private-page','now'))
        o.execute('INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(?,?,?,?)',(vid,cid,'now','fixture'))
    save(database,service,enabled=False)
    result=service.apply_gallery_layout('17')
    assert result['splits']==1
    first=service._find_existing_by_source('17',11);second=service._find_existing_by_source('17',12)
    assert first==a and second!=a
    with database.origins() as o:
        assert o.execute('SELECT private_url FROM candidate_pages WHERE candidate_id=?',(cid,)).fetchone()[0]=='https://fixture.invalid/private-page'
        assert o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE id=?',(cid,)).fetchone()[0]==vid
        assert o.execute('SELECT candidate_id FROM served_origin WHERE virtual_chapter_id=?',(vid,)).fetchone()[0]==cid
        assert o.execute('SELECT entry_id FROM virtual_chapters WHERE id=?',(vid,)).fetchone()[0]==second
    assert service.apply_gallery_layout('17')['splits']==0


def test_policy_revision_validation_and_hard_edition_guards(env):
    service,database=setup(env);state=policy.read(database,'17')
    saved=save(database,service,enabled=True,criteria=['artist'],title_minimum=.97)
    with pytest.raises(ValueError,match='changed'):policy.save(database,service,'17',state['policy'],state['revision'])
    with pytest.raises(ValueError):save(database,service,title_minimum=.93)
    with pytest.raises(ValueError):save(database,service,criteria=['unknown'])
    animated=payload(12,2);animated['genre'].append('other:animated')
    assert not service._gallery_evidence(service._clean_source_manga_payload('17',animated),[service._clean_source_manga_payload('17',payload(11,1))])['eligible']
    service.full_reset_database()
    assert policy.read(database,'17')['policy']==saved['policy']


@pytest.mark.parametrize('criterion,tag,other',[('artist','artist:Sample Artist','artist:Other Artist'),
    ('circle','circle:Sample Circle','circle:Other Circle'),('parody','parody:Sample World','parody:Other World'),
    ('category','category:doujinshi','category:manga'),('tags','male:fixture-one','male:fixture-two')])
def test_selected_criteria_require_present_compatible_native_values(env,criterion,tag,other):
    service,database=setup(env)
    save(database,service,criteria=[criterion],tag_overlap=1,tag_similarity=.8)
    left=payload(11,1);right=payload(12,2)
    left['genre']=[tag];right['genre']=[tag]
    def evidence():return service._gallery_evidence(service._clean_source_manga_payload('17',right),[service._clean_source_manga_payload('17',left)])
    assert evidence()['eligible']
    right['genre']=[other]
    if criterion=='artist':right['title']='[Other Artist] Quiet Garden Part 2 [English]'
    assert not evidence()['eligible']


def test_native_gallery_rules_toggle_and_split_journal_recovers_interruption(env,monkeypatch):
    service,database=setup(env)
    a=service.ingest_manga('17',payload(11,1))[0]
    assert service.ingest_manga('17',payload(12,2))[0]==a
    service._ingest_chapters(a,{'source_id':'17','source_manga_id':12,'source_name':'E-Hentai'},service._match_id(a,'17'),
        [{'id':120,'url':'/chapter/12','name':'Chapter0','chapterNumber':0,'pageCount':1}],effects={'invalidated':set()})
    with database.origins() as o:
        vid=o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE source_manga_id=12').fetchone()[0]
    save(database,service,enabled=False)
    original=service._finish_pending_entry_split
    def interrupted(split_id):raise RuntimeError('fixture interruption after catalogue commit')
    monkeypatch.setattr(service,'_finish_pending_entry_split',interrupted)
    with pytest.raises(RuntimeError,match='fixture interruption'):service.apply_gallery_layout('17')
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM pending_entry_splits').fetchone()[0]==1
        assert c.execute('SELECT COUNT(*) FROM gallery_layout_candidates').fetchone()[0]==1
    monkeypatch.setattr(service,'_finish_pending_entry_split',original)
    assert service.reconcile_pending_entry_origins()['remaining']==0
    target=service._find_existing_by_source('17',12)
    with database.origins() as o:assert o.execute('SELECT entry_id FROM virtual_chapters WHERE id=?',(vid,)).fetchone()[0]==target
    service.apply_gallery_layout('17')
    save(database,service,enabled=True)
    assert service.apply_gallery_layout('17')['merged']==1
    assert service._find_existing_by_source('17',11)==service._find_existing_by_source('17',12)


@pytest.mark.asyncio
async def test_gallery_task_is_visible_deduplicated_and_failed_rules_stay_pending(env,monkeypatch):
    from app import service as module
    service,database=setup(env);save(database,service,enabled=False)
    original=service.apply_gallery_layout
    def failure(*args,**kwargs):raise RuntimeError('fixture layout failure')
    monkeypatch.setattr(service,'apply_gallery_layout',failure)
    queued=service.schedule_gallery_layout('17')
    assert queued['queued'] and service.schedule_gallery_layout('17')['already_running']
    await service._refresh_tasks['__gallery-layout__:17']
    task=next(r for r in module.task_registry.snapshot()['items'] if r['id']==queued['task_id'])
    assert task['state']=='failed' and task['kind']=='gallery-layout' and 'fixture layout failure' in task['error']
    assert policy.read(database,'17')['layout_pending']
    assert '__gallery-layout__:17' not in service._refresh_tasks
    monkeypatch.setattr(service,'apply_gallery_layout',original)
    retry=service.schedule_gallery_layout('17')
    await service._refresh_tasks['__gallery-layout__:17']
    task=next(r for r in module.task_registry.snapshot()['items'] if r['id']==retry['task_id'])
    assert task['state']=='completed' and not policy.read(database,'17')['layout_pending']
