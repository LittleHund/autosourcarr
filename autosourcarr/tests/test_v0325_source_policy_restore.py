"""Stable source identity takes precedence over ambiguous display metadata."""
from app import matching_policies as policies
from test_v0323_matching_review import env, save


def test_same_named_sources_keep_distinct_groups_and_directions_after_reset(env):
    service, database = env
    first = service.create_source_group('Reviewed source')['id']
    second = service.create_source_group('Other source')['id']
    with database.catalog() as c:
        c.execute("UPDATE source_config SET name='Shared',base_url='https://example.invalid',lang='en' WHERE source_id IN ('a','b')")
    service.update_source('a', group_id=first, enabled_search=False)
    service.update_source('b', group_id=second, enabled_search=True)
    save(env, 'group', first, 'internal', {'enabled': False, 'checks': {}})
    save(env, 'source', 'a', 'external', {'enabled': True, 'checks': {'title': 1}})
    save(env, 'source', 'b', 'external', {'enabled': False, 'checks': {}})
    snapshot = service.capture_source_preferences()
    service.reset_database()
    service.restore_source_preferences(snapshot)
    assert service.get_source('a')['group_id'] == first
    assert not service.get_source('a')['enabled_search']
    assert service.get_source('b')['group_id'] == second
    assert service.get_source('b')['enabled_search']
    rules = policies.rules(database)
    assert not policies.effective(rules, service._source_cache, 'a', 'internal')[0]['enabled']
    assert rules[('source', 'a', 'external')]['checks']['title'] == 1
    assert not rules[('source', 'b', 'external')]['enabled']


def test_stable_source_id_keeps_rules_when_its_domain_changes(env):
    service, database = env
    save(env, 'source', 'a', 'external', {'enabled': False, 'checks': {}})
    snapshot = service.capture_source_preferences()
    with database.catalog() as c:
        c.execute('DELETE FROM matching_policies')
        c.execute("UPDATE source_config SET base_url='https://new.example.invalid' WHERE source_id='a'")
    policies.invalidate(database)
    service.restore_source_preferences(snapshot)
    assert not policies.rules(database)[('source', 'a', 'external')]['enabled']


def test_ambiguous_old_identity_cannot_assign_a_replacement_sources_policy(env):
    service, database = env
    with database.catalog() as c:
        c.execute("UPDATE source_config SET name='Shared',base_url='https://example.invalid',lang='en' WHERE source_id IN ('a','b')")
    save(env, 'source', 'a', 'external', {'enabled': False, 'checks': {}})
    snapshot = service.capture_source_preferences()
    with database.catalog() as c:
        c.execute('DELETE FROM matching_policies')
        c.execute("DELETE FROM source_config WHERE source_id IN ('a','b')")
        c.execute("INSERT INTO source_config(source_id,name,lang,base_url,last_seen) VALUES('replacement','Shared','en','https://example.invalid','now')")
    policies.invalidate(database)
    service.restore_source_preferences(snapshot)
    assert ('source', 'replacement', 'external') not in policies.rules(database)


def test_one_old_identity_cannot_assign_two_ambiguous_replacement_sources(env):
    service, database = env
    save(env, 'source', 'a', 'internal', {'enabled': False, 'checks': {}})
    snapshot = service.capture_source_preferences()
    with database.catalog() as c:
        c.execute('DELETE FROM matching_policies')
        c.execute("DELETE FROM source_config WHERE source_id='a'")
        for sid in ['replacement-one', 'replacement-two']:
            c.execute("INSERT INTO source_config(source_id,name,lang,base_url,last_seen) VALUES(?,'a','en','https://example.invalid','now')", (sid,))
    policies.invalidate(database)
    service.restore_source_preferences(snapshot)
    assert not any(key[1].startswith('replacement') for key in policies.rules(database))
