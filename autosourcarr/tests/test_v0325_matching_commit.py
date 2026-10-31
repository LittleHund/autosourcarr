"""Delayed automatic commits retain current native proof and manual decisions."""
import pytest

from app import tag_store
from app.config import settings
from test_v0323_matching_review import env, entry, payload, off


@pytest.fixture
def gallery_env(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    tag_store.invalidate()
    with database.catalog() as c:
        c.execute("UPDATE source_config SET name='IMHentai' WHERE source_id IN ('a','b')")
    service._reload_source_cache()
    yield env
    tag_store.invalidate()


def candidate(service, sid='b', mid=2, **changes):
    return service._clean_source_manga_payload(sid, payload(sid, mid,
        genre=['artist:Example Person', 'science fiction'], author='', initialized=True,
        **changes))


def test_delayed_gallery_attachment_rechecks_new_manual_tag_separation(gallery_env):
    service, database = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    with database.catalog() as c:
        tag_store.sync_source_record(c, 'b', 2, manga, {'name': 'IMHentai'})
    shortlisted = service._local_match_candidates(manga, 'b')
    assert shortlisted
    ids = {x['name']: x['id'] for x in tag_store.list_tags()['items'] if x['namespace'] == 'tag'}
    tag_store.review(ids['sci-fi'], ids['science fiction'], 'different')
    assert not service._local_match_candidates(manga, 'b')
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, shortlisted[0][1], 'auto-additional')
    assert service._find_existing_by_source('b', 2) is None


def test_unchanged_gallery_evidence_still_attaches(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    assert service._source_match_evidence({'id': eid}, manga)['eligible']
    assert service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')
    assert service._find_existing_by_source('b', 2) == eid


def test_same_source_family_can_attach_numbered_gallery_variant(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', title='Quiet Garden Chapter 1', genre=['artist:Example Person'], author='', initialized=True)
    manga = service._clean_source_manga_payload('a', payload('a', 2,
        title='Quiet Garden Part 2', genre=['artist:Example Person'], author='', initialized=True))
    evidence = service._source_match_evidence({'id': eid}, manga)
    assert evidence['eligible'] and evidence['chapter_family']
    assert service._attach_automatic_record_if_current(eid, 'a', manga, 1, 'auto-additional', variant_only=True)
    assert service._find_existing_by_source('a', 2) == eid


def test_ordinary_same_source_distinctive_exact_title_variant_attaches(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    tag_store.invalidate()
    eid = entry(service, 'a')
    manga = payload('a', 2)
    assert service._source_match_evidence({'id': eid}, manga)['eligible']
    assert service._attach_automatic_record_if_current(eid, 'a', manga, 1, 'auto-additional', variant_only=True)


def test_new_manual_record_separation_blocks_commit(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    service._remember_manual_separate_record(eid, 'b', 2)
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')


def test_new_disabled_source_policy_blocks_commit(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    off(gallery_env, 'b', 'external')
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')


def test_new_source_record_owner_blocks_commit(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    other = entry(service, 'b', 2, title='A Different Garden Chronicle', genre=['artist:Other Person'], author='')
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')
    assert service._find_existing_by_source('b', 2) == other


def test_delayed_commit_rechecks_new_animation_identity(gallery_env):
    service, _ = gallery_env
    initial = payload('a', 1, genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    eid = entry(service, 'a', genre=initial['genre'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    service._upsert_match(eid, 'a', {**initial, 'genre': initial['genre'] + ['other:animated']}, 1, 'auto')
    assert not service._source_match_evidence({'id': eid}, manga)['eligible']
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')


def test_delayed_commit_rechecks_changed_native_anchor_title(gallery_env):
    service, _ = gallery_env
    initial = payload('a', 1, genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    eid = entry(service, 'a', genre=initial['genre'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    service._upsert_match(eid, 'a', {**initial, 'title': 'A Different Garden Chronicle'}, 1, 'auto')
    assert not service._source_match_evidence({'id': eid}, manga)['eligible']
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')


def test_delayed_commit_rechecks_manual_alias_target_anchor(gallery_env):
    service, _ = gallery_env
    eid = entry(service, 'a', genre=['artist:Example Person', 'sci-fi'], author='', initialized=True)
    manga = candidate(service)
    assert service._local_match_candidates(manga, 'b')
    other = entry(service, 'c', 3, title='A Different Garden Chronicle', genre=['artist:Other Person', 'fantasy'], author='', initialized=True)
    service._upsert_match(other, 'c', payload('c', 3, title='A Different Garden Chronicle',
        genre=['artist:Other Person', 'fantasy'], author='', initialized=True), 1, 'manual', True)
    merged = service.merge_entries(eid, other, 'manual-review')
    assert not service._source_match_evidence({'id': merged}, manga)['eligible']
    assert not service._attach_automatic_record_if_current(eid, 'b', manga, 1, 'auto-additional')


def test_delayed_variant_commit_respects_variant_floor_not_primary_floor(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database);tag_store.invalidate()
    monkeypatch.setattr(settings, 'auto_match_threshold', .94)
    monkeypatch.setattr(settings, 'auto_variant_min_score', .96)
    initial = payload('a', 1)
    eid = entry(service, 'a')
    manga = payload('a', 2)
    assert service._local_match_candidates(manga, 'a')
    service._upsert_match(eid, 'a', {**initial, 'title': initial['title'] + ' Sky'}, 1, 'auto')
    trusted = service._trusted_automatic_entry_evidence(eid)
    assert service._source_match_evidence({'id': eid}, manga)['eligible']  # Primary floor is weaker.
    score, _ = service._variant_candidate_score(manga,
        entry_titles=trusted['titles'], entry_descriptions=trusted['descriptions'],
        entry_author=trusted['author'], real_normalized_titles=trusted['normalized_titles'],
        trusted_payloads=trusted['gallery_payloads'], policy_payloads=trusted['policy_payloads'])
    assert score == 0
    assert not service._attach_automatic_record_if_current(eid, 'a', manga, 1, 'auto-additional', variant_only=True)


def test_delayed_commit_rechecks_new_media_edition_state(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database);tag_store.invalidate()
    initial = payload('a', 1)
    eid = entry(service, 'a')
    manga = payload('a', 2, genre=['Webtoon'])
    assert service._source_match_evidence({'id': eid}, manga)['eligible']
    assert service._automatic_media_gate(eid, manga)[0]
    service._upsert_match(eid, 'a', {**initial, 'genre': ['Manga']}, 1, 'auto')
    assert service._source_match_evidence({'id': eid}, manga)['eligible']
    assert not service._automatic_media_gate(eid, manga)[0]
    assert not service._attach_automatic_record_if_current(eid, 'a', manga, 1, 'auto-additional', variant_only=True)
