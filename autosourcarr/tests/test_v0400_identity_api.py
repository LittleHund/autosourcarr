import asyncio
from fastapi import FastAPI,Header,HTTPException
from unittest.mock import AsyncMock
from contextlib import asynccontextmanager
import httpx
import pytest
from test_v087_recovery import env,source,entry
from app import portable_identity as identity
from app.config import settings
from app.library_manager import LibraryManager
from app.reimagined_api import router


def test_local_resolution_returns_separate_editions_without_merging(env):
    service,database=env;source(database,'17');source(database,'18')
    first=entry(service,'17',20,'Edition one');second=entry(service,'18',55,'Edition two')
    with database.catalog() as c:
        c.execute('UPDATE source_matches SET source_manga_url=? WHERE entry_id=?',('/manga/example',first))
        for eid in (first,second):
            c.execute('INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(?,?,?,?,?)',(eid,'anilist','12','work','2026-10-18'))
    result=identity.resolve(database,service,'asc1:work:anilist:12')
    assert result['ambiguous'] and {x['id'] for x in result['items']}=={first,second}
    source_ref=identity.record_id('17','https://example.org/manga/example')
    assert [r['id'] for r in identity.resolve(database,service,source_ref)['items']]==[first]
    assert service.get_entry(first)['id']==first and service.get_entry(second)['id']==second
    assert identity.resolve(database,service,'asc1:work:anilist:999')['items']==[]


def test_gallery_lookup_matches_original_site_and_keeps_distinct_local_entries(env):
    service,database=env;source(database,'17');source(database,'18')
    first=entry(service,'17',70,'Gallery English');second=entry(service,'18',80,'Gallery Japanese')
    with database.catalog() as c:
        c.execute('UPDATE source_matches SET real_url=? WHERE entry_id=?',('https://nhentai.net/g/123/',first))
        c.execute('UPDATE source_matches SET real_url=? WHERE entry_id=?',('https://nhentai.net/g/123/',second))
    result=identity.resolve(database,service,'asc1:gallery:nhentai:123')
    assert result['ambiguous'] and {e['id'] for e in result['items']}=={first,second}
    assert identity.resolve(database,service,'asc1:gallery:hitomi:123')['items']==[]


@pytest.mark.asyncio
async def test_lookup_is_authenticated_read_only_and_invalid_ids_do_not_query_sources(env):
    service,database=env;manager=LibraryManager(database,service,None)
    async def guard(x_admin_token:str=Header('')):
        if x_admin_token!='fixture-secret':raise HTTPException(401,'Admin token required')
    @asynccontextmanager
    async def paused():yield
    app=FastAPI();app.include_router(router(database,service,manager,settings,guard,asyncio.Lock(),paused,lambda:None))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        url='/api/admin/portable-identities/resolve?reference=asc1:work:anilist:12'
        assert (await client.get(url)).status_code==401
        assert (await client.get(url,headers={'X-Admin-Token':'fixture-secret'})).json()['items']==[]
        invalid=await client.get('/api/admin/portable-identities/resolve?reference=asc1:work:unknown:12',headers={'X-Admin-Token':'fixture-secret'})
        assert invalid.status_code==409
