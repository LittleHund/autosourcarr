import asyncio

import httpx
import pytest
from fastapi import HTTPException
from starlette.responses import Response

from app import main
from app.config import settings


def test_reset_admission_blocks_new_catalogue_work_but_leaves_live_readable(monkeypatch):
    async def scenario():
        monkeypatch.setattr(main, "_catalog_reset_active", True)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://test") as client:
            response = await client.get("/api/v1/search?q=test")
            assert response.status_code == 503
            assert response.headers["retry-after"] == "5"
            assert (await client.get("/api/v1/live")).status_code == 200
        assert main._catalog_http_requests == 0
    asyncio.run(scenario())


def test_reset_drains_inflight_http_handler_before_mutation(monkeypatch):
    async def scenario():
        monkeypatch.setattr(main.app.router, "routes", list(main.app.router.routes))
        entered, finish, resetting = asyncio.Event(), asyncio.Event(), asyncio.Event()
        @main.app.get("/api/audit-slow-request")
        async def slow():
            entered.set()
            await finish.wait()
            return {"ok": True}
        async def reset():
            async with main._exclusive_catalog_reset(timeout=1):
                resetting.set()
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://test") as client:
            request = asyncio.create_task(client.get("/api/audit-slow-request"))
            await entered.wait()
            operation = asyncio.create_task(reset())
            await asyncio.sleep(0.02)
            assert main._catalog_reset_active and not resetting.is_set()
            finish.set()
            assert (await request).status_code == 200
            await operation
            assert resetting.is_set()
        assert not main._catalog_reset_active
        assert main._catalog_http_requests == 0
    asyncio.run(scenario())


def test_busy_reset_aborts_without_entering_mutation_and_reopens_admission(monkeypatch):
    async def scenario():
        monkeypatch.setattr(main, "_catalog_http_requests", 1)
        with pytest.raises(HTTPException) as error:
            async with main._exclusive_catalog_reset(timeout=0.01):
                pytest.fail("Must not mutate while a foreground request is running")
        assert error.value.status_code == 503
        assert not main._catalog_reset_active
    asyncio.run(scenario())


def test_cancelled_reset_reopens_admission(monkeypatch):
    async def scenario():
        monkeypatch.setattr(main, "_catalog_http_requests", 1)
        async def waiting():
            async with main._exclusive_catalog_reset(timeout=30):
                pytest.fail("Must not enter")
        job = asyncio.create_task(waiting())
        await asyncio.sleep(0.01)
        job.cancel()
        with pytest.raises(asyncio.CancelledError):
            await job
        assert not main._catalog_reset_active
    asyncio.run(scenario())


@pytest.mark.parametrize("public,private", [("admin_reset_database", "_admin_reset_database"), ("admin_full_reset", "_admin_full_reset")])
def test_reset_routes_run_inside_exclusive_admission(monkeypatch, public, private):
    async def scenario():
        async def operation():
            assert main._database_admin_lock.locked()
            assert main._catalog_reset_active
            return {"ok": True}
        monkeypatch.setattr(main, private, operation)
        assert await getattr(main, public)() == {"ok": True}
        assert not main._database_admin_lock.locked()
        assert not main._catalog_reset_active
    asyncio.run(scenario())


def test_unicode_admin_token_is_compared_without_typeerror(monkeypatch):
    monkeypatch.setattr(settings, "admin_token", "tökén")
    assert asyncio.run(main.admin_guard(x_admin_token="tökén")) is True
    with pytest.raises(HTTPException) as error:
        asyncio.run(main.admin_guard(x_admin_token="évil"))
    assert error.value.status_code == 401


def test_untrusted_image_response_is_sandboxed(monkeypatch):
    async def scenario():
        monkeypatch.setattr(main.app.router, "routes", list(main.app.router.routes))
        @main.app.get("/api/v1/pages/audit/image/sandbox")
        async def image():
            return Response(b"<svg/>", media_type="image/svg+xml")
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://test") as client:
            response = await client.get("/api/v1/pages/audit/image/sandbox")
            assert response.status_code == 200
            assert response.headers["x-content-type-options"] == "nosniff"
            assert "sandbox" in response.headers["content-security-policy"]
            assert response.content == b"<svg/>"
    asyncio.run(scenario())


def test_manual_consolidation_is_owned_and_cancelled_before_reset(monkeypatch):
    from app.service import AutoSourcarrService
    from app.task_registry import task_registry
    async def scenario():
        service = AutoSourcarrService()
        monkeypatch.setattr(main, "service", service)
        result = await main.admin_consolidate()
        assert len(service._auxiliary_tasks) == 1
        await service.stop_background_tasks()
        await asyncio.sleep(0)
        assert not service._auxiliary_tasks
        assert not task_registry.is_running(result["task_id"])
    asyncio.run(scenario())
