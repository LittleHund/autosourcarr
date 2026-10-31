"""Research-backed public contract regressions, using disposable databases only."""
import json
from types import SimpleNamespace
import pytest

from test_v087_recovery import env, source, entry
from app.library_projection import page
from app.suwayomi import SuwayomiClient, SuwayomiError


@pytest.mark.parametrize('scenario,expected_full,expected_gaps',[
    ('disabled',False,True),('languages',True,False),('chapter_only',False,True),
    ('none',False,False),('complete',True,False),('custom_group',True,False),
    ('distinct_urls',False,True),
])
def test_coverage_uses_membership_in_shown_groups(env,scenario,expected_full,expected_gaps):
    service,database=env
    source(database,'one');source(database,'two');eid=entry(service,'one',47001)
    with database.catalog() as c:
        c.execute("UPDATE source_config SET lang='en',available=1,enabled_language=1,enabled_search=1,enabled_chapters=1")
        if scenario=='disabled':c.execute("UPDATE source_config SET enabled_language=0 WHERE source_id='one'")
        elif scenario=='languages':c.execute("UPDATE source_config SET name='One family',base_url='https://example.invalid'")
        elif scenario=='chapter_only':
            c.execute("UPDATE source_config SET enabled_language=0 WHERE source_id='one'")
            c.execute("UPDATE source_config SET enabled_search=0 WHERE source_id='two'")
        elif scenario=='none':c.execute('UPDATE source_config SET enabled_language=0')
        elif scenario=='complete':
            c.execute('INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(?,?,?,?,?)',(eid,'two',47002,'Same work','now'))
        elif scenario=='custom_group':
            c.execute("INSERT INTO source_groups(id,name,priority,created_at,updated_at) VALUES(47,'Shown together',10,'now','now')")
            c.execute('UPDATE source_config SET group_id=47')
        elif scenario=='distinct_urls':
            c.execute("UPDATE source_config SET name='Same label',base_url='https://one.invalid'")
            c.execute("UPDATE source_config SET base_url='https://two.invalid' WHERE source_id='two'")
    snapshot={'ok':True,'entry_ids':[eid]}
    assert bool(page(database,snapshot,match_status='full')['total']) is expected_full
    assert bool(page(database,snapshot,match_status='gaps')['total']) is expected_gaps


def complete(coroutine):
    try:coroutine.send(None)
    except StopIteration as done:return done.value
    finally:coroutine.close()
    raise AssertionError('Immediate fake unexpectedly suspended')


@pytest.mark.parametrize('body',[[],None,{}, {'data':None}, {'data':['wrong']},
    {'data':'wrong'}, {'errors':['wrong']}, {'errors':[]}, {'errors':{}},
    {'errors':[{'message':123}]}, {'data':{},'errors':[{'message':'partial'}]}])
def test_graphql_invalid_or_error_results_raise_typed_error(body):
    class Response:
        def raise_for_status(self):pass
        def json(self):return body
    class Client:
        async def post(self,*args,**kwargs):return Response()
    owner=SimpleNamespace(client=Client(),graphql_url='https://fixture.invalid/graphql')
    with pytest.raises(SuwayomiError):complete(SuwayomiClient.graphql(owner,'query Fixture{sources{nodes{id}}}'))


def test_graphql_explicit_empty_and_long_ids_preserved():
    value={'sources':{'nodes':[]},'source':{'id':'-9223372036854775808'}}
    class Response:
        def raise_for_status(self):pass
        def json(self):return {'data':value}
    class Client:
        async def post(self,*args,**kwargs):return Response()
    assert complete(SuwayomiClient.graphql(SimpleNamespace(client=Client(),graphql_url='fixture'),'query Fixture'))==value


@pytest.mark.asyncio
@pytest.mark.parametrize('node',[None,{}, {'nodes':None,'pageInfo':{'hasNextPage':False}},
    {'nodes':[],'pageInfo':{}},{'nodes':[],'pageInfo':{'hasNextPage':'false'}},
    {'nodes':[{'id':1}],'pageInfo':{'hasNextPage':False}}])
async def test_incomplete_library_connections_never_become_empty_success(node,monkeypatch):
    client=SuwayomiClient('http://fixture.invalid')
    async def response(*args,**kwargs):return {'mangas':node}
    monkeypatch.setattr(client,'graphql',response)
    try:
        with pytest.raises(SuwayomiError):await client.library_mangas()
    finally:await client.close()


@pytest.mark.asyncio
async def test_source_identity_signed_64bit_string_survives_real_sources_method(monkeypatch):
    client=SuwayomiClient('http://fixture.invalid')
    async def response(*args,**kwargs):return {'sources':{'nodes':[{'id':'9223372036854775807','name':'Fixture','lang':'en'}]}}
    monkeypatch.setattr(client,'graphql',response)
    try:assert (await client.sources())[0]['id']=='9223372036854775807'
    finally:await client.close()
