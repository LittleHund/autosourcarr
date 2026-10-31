"""Saved templates have their own revisions and never implicitly change rules."""
import copy

import httpx
import pytest
from fastapi import FastAPI

from app import matching_policies as policies, matching_review as review
from app.db import Database
from test_v0323_matching_review import env, entry, save


def client_for(env):
    service, database = env
    app = FastAPI()
    app.include_router(review.router(database, service, lambda: None))
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://app')


@pytest.mark.asyncio
async def test_create_edit_reuse_and_restart_preserve_applied_rules(env):
    service, database = env
    eid = entry(service, 'a')
    async with client_for(env) as client:
        initial = (await client.get('/api/admin/matching-policies')).json()
        rule = {'enabled': True, 'checks': {'title': .98, 'author': .91}}
        body = {'revision': initial.get('preset_revision', ''), 'name': ' My creators ',
                'description': 'Use when creators are available.', 'rule': rule}
        response = await client.post('/api/admin/matching-presets', json=body)
        assert response.status_code == 201, response.text
        result = response.json()
        preset = next(p for p in result['presets'] if p['name'] == 'My creators')
        assert not preset['builtin'] and preset['rule'] == rule
        assert not policies.rules(database)
        assert review.configuration(database, service)['revision'] == initial['revision']
        save(env, 'source', 'a', 'internal', copy.deepcopy(preset['rule']))
        rule_revision = review.configuration(database, service)['revision']
        body.update(revision=result['preset_revision'], name='Edited creators',
                    rule={'enabled': True, 'checks': {'title': .99}})
        response = await client.put('/api/admin/matching-presets/'+preset['id'], json=body)
        assert response.status_code == 200, response.text
        edited = response.json()
        assert edited['preset_revision'] != result['preset_revision']
        assert policies.rules(database)[('source', 'a', 'internal')] == rule
        assert review.configuration(database, service)['revision'] == rule_revision
    database.close_current_thread()
    reopened = Database()
    try:
        restored = review.configuration(reopened, service)
        assert next(p for p in restored['presets'] if p['id'] == preset['id'])['name'] == 'Edited creators'
        assert policies.rules(reopened)[('source', 'a', 'internal')] == rule
        assert service._find_existing_by_source('a', 1) == eid
    finally:
        reopened.close_current_thread()


@pytest.mark.asyncio
async def test_conflicting_preset_edits_and_deletion_leave_rules_intact(env):
    service, database = env
    async with client_for(env) as client:
        initial = (await client.get('/api/admin/matching-policies')).json()
        body = {'revision': initial.get('preset_revision', ''), 'name': 'Original',
                'description': '', 'rule': {'enabled': True, 'checks': {'title': .97}}}
        created = await client.post('/api/admin/matching-presets', json=body)
        assert created.status_code == 201, created.text
        state = created.json()
        preset = next(p for p in state['presets'] if p['name'] == 'Original')
        body.update(revision=state['preset_revision'], name='Newer')
        newer = await client.put('/api/admin/matching-presets/'+preset['id'], json=body)
        assert newer.status_code == 200
        stale = await client.put('/api/admin/matching-presets/'+preset['id'], json={**body, 'name': 'Stale'})
        assert stale.status_code == 409
        save(env, 'source', 'a', 'external', preset['rule'])
        deleted = await client.request('DELETE', '/api/admin/matching-presets/'+preset['id'],
                                       json={'revision': newer.json()['preset_revision']})
        assert deleted.status_code == 200
        assert all(p['id'] != preset['id'] for p in deleted.json()['presets'])
        assert policies.rules(database)[('source', 'a', 'external')] == preset['rule']
        builtin = await client.request('DELETE', '/api/admin/matching-presets/title',
                                       json={'revision': deleted.json()['preset_revision']})
        assert builtin.status_code == 400


@pytest.mark.asyncio
@pytest.mark.parametrize('changes', [
    {'name': '   '}, {'name': 'bad\nname'}, {'name': 'x'*81},
    {'description': 'x'*501}, {'rule': {'enabled': True, 'checks': {'title': .93}}},
    {'rule': {'enabled': True, 'checks': {}}},
    {'rule': {'enabled': True, 'checks': {'unknown': 1}}},
    {'rule': {'enabled': True, 'checks': {'title': True}}},
])
async def test_invalid_templates_do_not_mutate_catalog(env, changes):
    async with client_for(env) as client:
        initial = (await client.get('/api/admin/matching-policies')).json()
        body = {'revision': initial.get('preset_revision', ''), 'name': 'Template',
                'description': '', 'rule': {'enabled': True, 'checks': {'title': .97}}, **changes}
        response = await client.post('/api/admin/matching-presets', json=body)
        assert response.status_code in {400, 422}, response.text
        assert (await client.get('/api/admin/matching-policies')).json()['presets'] == initial['presets']
