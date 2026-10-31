"""Real DB/API contracts for fast metadata and separately bounded coverage reads."""
import asyncio
import json
import threading
from unittest.mock import AsyncMock

import pytest
from fastapi import HTTPException
from app import main
from app.read_lane import ReadLane
from test_v087_recovery import env
from test_v091_pages import chapter_fixture


class Request:
    def __init__(self, payload): self.payload = payload
    async def json(self): return self.payload


def test_progressive_metadata_matches_complete_view_and_never_queries_stats(env, monkeypatch):
    service, database, _ = chapter_fixture(env, monkeypatch)
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(main, 'db', database)
    monkeypatch.setattr(service, 'schedule_status_aux_refresh', lambda **kwargs: False)
    with database.catalog() as c: eid = c.execute('SELECT id FROM entries').fetchone()['id']
    monkeypatch.setattr(service, 'cached_subbed_payload', lambda: {'ok': True, 'entry_ids': [eid]})
    async def run():
        full=json.loads((await main.admin_matrix(limit=500, subbed=True)).body)
        response=json.loads((await main.admin_matrix_chapter_stats(Request({'ids':[eid]}))).body)
        def forbidden(*args): raise AssertionError('metadata waited for coverage')
        monkeypatch.setattr(service,'entry_chapter_stats',forbidden)
        monkeypatch.setattr(service,'source_record_chapter_stats',forbidden)
        thin=json.loads((await main.admin_matrix(limit=500,subbed=True,include_chapter_stats=False)).body)
        assert thin['chapter_stats_pending']
        for entry in thin['entries']:
            assert entry.pop('chapter_stats_pending') is True
            stat=response['entry_stats'][str(entry['id'])]
            for key in ('served_chapter_count','served_latest_chapter','chapter_records_checked'): entry[key]=stat[key]
        record_stats={(s['entry_id'],s['source_id'],s['source_manga_id']):s for s in response['record_stats']}
        for record in thin['matches']+thin['variants']:
            record.pop('chapter_stats_pending')
            stat=record_stats[(record['entry_id'],record['source_id'],record['source_manga_id'])]
            for key in ('chapter_count','latest_chapter','missing_chapter_count','chapters_checked','chapter_checked_at','chapter_check_error'): record[key]=stat.get(key,'' if key=='chapter_check_error' else None)
        thin.pop('chapter_stats_pending')
        assert thin==full
    asyncio.run(run())


def test_initial_subbed_metadata_does_not_wait_for_remote_membership(env, monkeypatch):
    service,database=env
    monkeypatch.setattr(main,'service',service);monkeypatch.setattr(main,'db',database)
    monkeypatch.setattr(service,'cached_subbed_payload',lambda:{'ok':None,'entry_ids':[],'refreshing':True})
    monkeypatch.setattr(service,'schedule_status_aux_refresh',lambda **kwargs:False)
    remote=AsyncMock(side_effect=AssertionError('remote membership blocked initial metadata'))
    monkeypatch.setattr(service,'subbed_entry_ids',remote)
    result=json.loads(asyncio.run(main.admin_matrix(limit=500,subbed=True,include_chapter_stats=False)).body)
    assert result['entries']==[] and result['subscription']['refreshing']
    remote.assert_not_awaited()


@pytest.mark.parametrize('payload',[None,[],{}, {'ids':'1'}, {'ids':list(range(501))}])
def test_stats_rejects_unbounded_or_invalid_request_shape(payload):
    with pytest.raises(HTTPException) as exc: asyncio.run(main.admin_matrix_chapter_stats(Request(payload)))
    assert exc.value.status_code==400


def test_coverage_saturation_leaves_metadata_workers_available():
    lane=ReadLane(workers=2);started=[threading.Event(),threading.Event()];release=threading.Event()
    def busy(index):
        started[index].set();assert release.wait(3);return {'done':True}
    async def run():
        tasks=[asyncio.create_task(main.catalogue_response(busy,i,lane=lane)) for i in range(2)]
        try:
            while not all(e.is_set() for e in started): await asyncio.sleep(.001)
            result=await asyncio.wait_for(main.catalogue_response(lambda:{'metadata':True}),.5)
            assert json.loads(result.body)=={'metadata':True}
            assert all(not task.done() for task in tasks)
        finally:
            release.set();await asyncio.gather(*tasks);await lane.close()
    asyncio.run(run())
