"""Local work budgets must not blacklist healthy or never-contacted sources."""
import asyncio
import pytest
from app.config import settings
from app.suwayomi import bounded_map, BatchDeadlineExceeded, SourceWorkDeferred
from app.source_errors import source_error_kind, source_error_summary
from test_v086_imports import env


def test_batch_expiry_does_not_trip_cooldown_for_queued_sources(env, monkeypatch):
    s, _, _ = env
    monkeypatch.setattr(settings, 'source_failure_threshold', 1)
    async def run():
        started=[]
        async def fetch(sid):
            started.append(sid)
            await asyncio.Event().wait()
        results=await bounded_map(['busy','never-contacted'],fetch,limit=1,overall_timeout=.02)
        assert started == ['busy']
        for sid, payload, error in results:
            s._record_source_result(sid,error)
            assert not s._source_in_cooldown(sid)
    asyncio.run(run())


@pytest.mark.parametrize('error', [BatchDeadlineExceeded('federated deadline reached'), SourceWorkDeferred('capacity busy')])
def test_local_deferral_retains_existing_health_and_explains_retry(env, error):
    s, _, _ = env
    s._source_failures['one']=(1,0.0,'real prior failure')
    s._record_source_result('one',error)
    assert s._source_failures['one']==(1,0.0,'real prior failure')
    assert source_error_kind(error)=='deferred'
    assert 'deferred' in source_error_summary(error)


def test_real_upstream_timeout_still_trips_cooldown(env, monkeypatch):
    s, _, _ = env
    monkeypatch.setattr(settings,'source_failure_threshold',1)
    s._record_source_result('one',TimeoutError('upstream timeout'))
    assert s._source_in_cooldown('one')
