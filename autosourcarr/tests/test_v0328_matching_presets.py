"""Presets remain ordinary, independently saved future-match requirements."""
import copy

import pytest

from app import matching_policies as policies, matching_review as review
from test_v0323_matching_review import env, entry, payload, save


def test_preset_catalog_is_valid_and_detached_from_configuration(env):
    service, database = env
    before = review.configuration(database, service)
    presets = before['presets']
    assert len({p['id'] for p in presets}) == len(presets) == 5
    for preset in presets:
        assert preset['name'] and preset['description']
        assert policies.validate(preset['rule']) == preset['rule']
        if preset['rule'] and preset['rule']['enabled']:
            assert preset['rule']['checks']['title'] >= .94
    title = next(p for p in presets if p['id'] == 'title')
    title['rule']['checks']['title'] = .01
    assert next(p for p in review.configuration(database, service)['presets'] if p['id'] == 'title')['rule']['checks']['title'] == .97
    assert not policies.rules(database), 'Reading/selecting a preset never writes saved policies'


def test_preset_saves_only_selected_direction_and_retains_existing_entries(env):
    service, database = env
    eid = entry(service, 'a')
    presets = {p['id']: p for p in review.configuration(database, service)['presets']}
    gid = service.create_source_group('My sources')['id']
    service.update_source('a', group_id=gid)
    save(env, 'group', gid, 'external', {'enabled': False, 'checks': {}})
    save(env, 'source', 'a', 'internal', presets['creators']['rule'])
    values = policies.rules(database)
    assert policies.effective(values, service._source_cache, 'a', 'external')[0]['enabled'] is False
    assert policies.effective(values, service._source_cache, 'a', 'internal')[0]['checks'] == {'title': .97, 'author': .90}
    save(env, 'source', 'a', 'internal', None)
    assert ('source', 'a', 'internal') not in policies.rules(database)
    assert service._find_existing_by_source('a', 1) == eid


def test_presets_require_selected_metadata_and_preserve_base_identity_checks(env):
    service, database = env
    entry(service, 'a')
    presets = {p['id']: p for p in review.configuration(database, service)['presets']}
    save(env, 'source', 'b', 'external', presets['creators']['rule'])
    assert service._local_match_candidates(payload('b', 2), 'b')
    assert not service._local_match_candidates(payload('b', 2, author=''), 'b')
    for preset in presets.values():
        if preset['rule'] and preset['rule']['enabled']:
            save(env, 'source', 'b', 'external', copy.deepcopy(preset['rule']))
            assert not service._local_match_candidates(payload('b', 2, title='An Unrelated Winter Festival'), 'b')
            assert not service._local_match_candidates(payload('b', 2, title=payload('b')['title'] + ' (Novel)'), 'b')


@pytest.mark.parametrize('preset_id,missing', [
    ('title', {'title': ''}),
    ('creators', {'author': '', 'artist': ''}),
    ('summary', {'description': ''}),
    ('gallery', {'genre': [], 'author': '', 'artist': ''}),
    ('strict', {'description': ''}),
])
def test_each_preset_blocks_missing_required_evidence(env, preset_id, missing):
    service, database = env
    entry(service, 'a')
    rule = next(p['rule'] for p in review.configuration(database, service)['presets'] if p['id'] == preset_id)
    save(env, 'source', 'b', 'external', rule)
    assert not service._local_match_candidates(payload('b', 2, **missing), 'b')


def test_strict_preset_requires_higher_title_similarity(env):
    service, database = env
    entry(service, 'a')
    near = payload('b', 2, title=payload('b')['title'].replace('Kingdom', 'Kingdon'))
    presets = {p['id']: p for p in review.configuration(database, service)['presets']}
    save(env, 'source', 'b', 'external', presets['title']['rule'])
    assert service._local_match_candidates(near, 'b')
    save(env, 'source', 'b', 'external', presets['strict']['rule'])
    assert not service._local_match_candidates(near, 'b')
