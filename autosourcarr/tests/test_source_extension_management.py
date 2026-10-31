import asyncio
import json
from pathlib import Path
import subprocess

import httpx
import pytest

from app.suwayomi import ExtensionInstallUnavailable, SuwayomiClient, SuwayomiError


def run(coro):
    return asyncio.run(coro)


def test_extension_inventory_uses_local_query_without_refresh():
    client = SuwayomiClient("http://source-host.invalid")
    calls = []

    async def fake_graphql(query, variables=None, **_kwargs):
        calls.append((query, variables))
        return {
            "extensions": {
                "nodes": [
                    {
                        "pkgName": "eu.kanade.tachiyomi.extension.en.example",
                        "name": "Example",
                        "lang": "en",
                        "versionName": "1.2.3",
                        "versionCodeLong": 123,
                        "isInstalled": True,
                        "hasUpdate": False,
                        "isObsolete": False,
                        "iconUrl": "",
                        "contentWarning": "SAFE",
                        "storeIndexUrl": "https://store.invalid/index.min.json",
                        "apkUrl": "https://store.invalid/example.apk",
                    }
                ]
            }
        }

    client.graphql = fake_graphql
    try:
        result = run(client.extensions(refresh=False))
    finally:
        run(client.close())

    assert result["extensions"][0]["name"] == "Example"
    assert result["stores"] == []
    assert len(calls) == 1
    assert "extensions { nodes" in calls[0][0]
    assert "fetchExtensions" not in calls[0][0]
    assert "extensionStores" not in calls[0][0]


def test_refresh_catalog_returns_extensions_and_store_metadata():
    client = SuwayomiClient("http://source-host.invalid")
    calls = []

    async def fake_graphql(query, variables=None, **_kwargs):
        calls.append((query, variables))
        return {
            "fetchExtensions": {
                "extensions": [
                    {
                        "pkgName": "pkg.example",
                        "name": "Example",
                        "lang": "all",
                        "isInstalled": False,
                        "hasUpdate": False,
                        "isObsolete": False,
                    }
                ],
                "extensionStores": [
                    {
                        "name": "Test Store",
                        "indexUrl": "https://store.invalid/index.min.json",
                        "badgeLabel": "TEST",
                        "isLegacy": False,
                        "extensionListUrl": "https://store.invalid/list.json",
                    }
                ],
            }
        }

    client.graphql = fake_graphql
    try:
        result = run(client.extensions(refresh=True))
    finally:
        run(client.close())

    assert result["extensions"][0]["pkgName"] == "pkg.example"
    assert result["stores"][0]["name"] == "Test Store"
    assert calls[0][1] == {"input": {}}
    assert "fetchExtensions(input: $input)" in calls[0][0]


def test_single_extension_action_maps_to_suwayomi_patch():
    client = SuwayomiClient("http://source-host.invalid")
    seen = {}

    async def fake_graphql(query, variables=None, **_kwargs):
        seen["query"] = query
        seen["variables"] = variables
        return {"updateExtension": {"extension": {"pkgName": "pkg.example", "isInstalled": True}}}

    client.graphql = fake_graphql
    try:
        result = run(client.update_extension("pkg.example", "install"))
    finally:
        run(client.close())

    assert result["pkgName"] == "pkg.example"
    assert seen["variables"] == {"input": {"id": "pkg.example", "patch": {"install": True}}}
    assert "updateExtension(input: $input)" in seen["query"]


def test_update_all_only_updates_installed_extensions_with_updates():
    client = SuwayomiClient("http://source-host.invalid")
    bulk = {}

    async def fake_extensions(*, refresh=False):
        assert refresh is True
        return {
            "extensions": [
                {"pkgName": "pkg.update", "isInstalled": True, "hasUpdate": True},
                {"pkgName": "pkg.current", "isInstalled": True, "hasUpdate": False},
                {"pkgName": "pkg.available", "isInstalled": False, "hasUpdate": True},
                {"pkgName": "pkg.obsolete", "isInstalled": True, "hasUpdate": True, "isObsolete": True},
            ],
            "stores": [{"name": "Store", "indexUrl": "https://store.invalid"}],
        }

    async def fake_graphql(query, variables=None, **_kwargs):
        bulk["query"] = query
        bulk["variables"] = variables
        return {"updateExtensions": {"extensions": [{"pkgName": "pkg.update", "isInstalled": True, "hasUpdate": False}]}}

    client.extensions = fake_extensions
    client.graphql = fake_graphql
    try:
        result = run(client.update_all_extensions())
    finally:
        run(client.close())

    assert result["updated"] == 1
    assert bulk["variables"] == {"input": {"ids": ["pkg.update"], "patch": {"update": True}}}
    assert "updateExtensions(input: $input)" in bulk["query"]


def test_fetch_asset_rejects_external_absolute_urls():
    client = SuwayomiClient("http://source-host.invalid")
    try:
        try:
            run(client.fetch_asset("https://elsewhere.invalid/icon.png"))
            assert False, "external assets must not be proxied"
        except Exception as exc:
            assert "external asset" in str(exc).lower()
    finally:
        run(client.close())


# A 404 from updateExtension is a missing package download, not a source CAPTCHA.
PACKAGE = 'eu.kanade.tachiyomi.extension.en.witchscans'
DOWNLOAD_404 = ('Exception while fetching data (/updateExtension) : HTTP error 404\r\n'
                'java.lang.Exception: HTTP error 404\n at okhttp3.RealCall.run(RealCall.kt:582)')
AVAILABLE = {'pkgName': PACKAGE, 'isInstalled': False, 'isObsolete': False,
             'apkUrl': 'https://repo.invalid/new.apk', 'versionName': '1.6.34'}
INSTALLED = {**AVAILABLE, 'isInstalled': True}


async def extension_scenario(responses, action='install'):
    calls = []
    def handle(request):
        calls.append(json.loads(request.content))
        assert responses, 'Unexpected extra source-engine request'
        response = responses.pop(0)
        if isinstance(response, BaseException):
            raise response
        if isinstance(response, int):
            return httpx.Response(response)
        return httpx.Response(200, json=response)
    client = SuwayomiClient('http://source-host.invalid')
    await client.client.aclose()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(handle))
    try:
        try:
            result = await client.update_extension(PACKAGE, action)
        except Exception as exc:
            result = exc
        return result, calls
    finally:
        await client.close()


def catalog(items):
    return {'data': {'fetchExtensions': {'extensions': items, 'extensionStores': []}}}


def changed(item=INSTALLED):
    return {'data': {'updateExtension': {'extension': item}}}


def test_install_recovers_stale_download_with_one_catalog_refresh():
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, catalog([AVAILABLE]), changed(),
    ]))
    assert result == INSTALLED
    assert len(calls) == 3
    assert 'fetchExtensions' in calls[1]['query']
    assert calls[0]['variables'] == calls[2]['variables'] == {
        'input': {'id': PACKAGE, 'patch': {'install': True}},
    }


def test_install_stops_after_second_download_404():
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, catalog([AVAILABLE]),
        {'errors': [{'message': DOWNLOAD_404}]},
    ]))
    assert isinstance(result, SuwayomiError)
    assert 'after refreshing the catalog' in str(result)
    assert '404' in str(result) and PACKAGE in str(result)
    assert 'No further automatic retries' in str(result)
    assert len(calls) == 3


@pytest.mark.parametrize('items', [[], [{**AVAILABLE, 'isObsolete': True}]])
def test_install_does_not_retry_missing_or_obsolete_extension(items):
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, catalog(items),
    ]))
    assert isinstance(result, SuwayomiError)
    assert 'not available in the refreshed catalog' in str(result)
    assert len(calls) == 2


def test_install_accepts_already_installed_after_catalog_refresh():
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, catalog([INSTALLED]),
    ]))
    assert result == INSTALLED
    assert len(calls) == 2


@pytest.mark.parametrize('failure', [
    {'errors': [{'message': 'Repository fetch failed'}]}, {'data': {}},
])
def test_install_does_not_retry_when_refresh_fails_or_is_incomplete(failure):
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, failure,
    ]))
    assert isinstance(result, SuwayomiError)
    assert 'could not refresh the extension catalog' in str(result)
    assert 'Refresh catalog' in str(result)
    assert len(calls) == 2


@pytest.mark.parametrize('message', [
    'Exception while fetching data (/updateExtension) : HTTP error 403',
    'Exception while fetching data (/updateExtension) : HTTP error 429',
    'Exception while fetching data (/updateExtension) : HTTP error 500',
    'Exception while fetching data (/fetchSourceManga) : HTTP error 404',
    'Invalid extension signature',
])
def test_install_does_not_retry_other_failures(message):
    result, calls = run(extension_scenario([{'errors': [{'message': message}]}]))
    assert isinstance(result, SuwayomiError) and str(result) == message
    assert len(calls) == 1


def test_install_does_not_mistake_engine_http_404_for_package_404():
    result, calls = run(extension_scenario([404]))
    assert isinstance(result, httpx.HTTPStatusError)
    assert len(calls) == 1


@pytest.mark.parametrize('action', ['update', 'uninstall'])
def test_non_install_actions_do_not_replay_after_404(action):
    result, calls = run(extension_scenario([{'errors': [{'message': DOWNLOAD_404}]}], action))
    assert isinstance(result, SuwayomiError) and str(result) == DOWNLOAD_404
    assert len(calls) == 1


@pytest.mark.parametrize('item', [None, AVAILABLE, {**INSTALLED, 'pkgName': 'different.package'}])
def test_install_requires_confirmed_installed_package(item):
    result, calls = run(extension_scenario([changed(item)]))
    assert isinstance(result, SuwayomiError)
    assert 'did not confirm installation' in str(result)
    assert len(calls) == 1


def test_recovered_install_does_not_retry_new_non_404_failure():
    result, calls = run(extension_scenario([
        {'errors': [{'message': DOWNLOAD_404}]}, catalog([AVAILABLE]),
        {'errors': [{'message': 'HTTP error 429'}]},
    ]))
    assert isinstance(result, SuwayomiError) and str(result) == 'HTTP error 429'
    assert len(calls) == 3


def test_install_cancellation_during_refresh_never_sends_retry():
    async def scenario():
        client = SuwayomiClient('http://source-host.invalid')
        calls = []
        async def graphql(query, variables=None):
            calls.append(query)
            if len(calls) == 1:
                raise SuwayomiError(DOWNLOAD_404)
            raise asyncio.CancelledError()
        client.graphql = graphql
        try:
            with pytest.raises(asyncio.CancelledError):
                await client.update_extension(PACKAGE, 'install')
            assert len(calls) == 2
        finally:
            await client.close()
    run(scenario())


def test_install_catalog_timeout_stops_recovery(monkeypatch):
    async def scenario():
        client = SuwayomiClient('http://source-host.invalid')
        events = []
        async def graphql(query, variables=None):
            events.append('install')
            raise SuwayomiError(DOWNLOAD_404)
        async def extensions(**kwargs):
            events.append('refresh')
            raise asyncio.TimeoutError()
        client.graphql = graphql
        client.extensions = extensions
        try:
            with pytest.raises(ExtensionInstallUnavailable, match='could not refresh'):
                await client.update_extension(PACKAGE, 'install')
            assert events == ['install', 'refresh']
        finally:
            await client.close()
    run(scenario())


def test_install_api_reports_repository_failure_without_scheduling_sync(monkeypatch):
    from app import main
    from fastapi import HTTPException
    from unittest.mock import Mock
    async def unavailable(*args):
        raise ExtensionInstallUnavailable('Repository package is missing after catalog refresh')
    schedule = Mock()
    monkeypatch.setattr(main.source_host, 'update_extension', unavailable)
    monkeypatch.setattr(main.service, 'schedule_source_reconciliation', schedule)
    with pytest.raises(HTTPException) as caught:
        run(main.admin_source_extension_action(PACKAGE, main.ExtensionActionRequest(action='install')))
    assert caught.value.status_code == 502
    assert 'Repository package' in caught.value.detail
    schedule.assert_not_called()


def test_install_api_keeps_success_reconciliation_in_background(monkeypatch):
    from app import main
    from unittest.mock import Mock
    async def installed(*args):
        return INSTALLED
    schedule = Mock(return_value=True)
    monkeypatch.setattr(main.source_host, 'update_extension', installed)
    monkeypatch.setattr(main.service, 'schedule_source_reconciliation', schedule)
    result = run(main.admin_source_extension_action(PACKAGE, main.ExtensionActionRequest(action='install')))
    assert result['source_sync_pending'] is True
    assert {key: result['extension'][key] for key in INSTALLED} == INSTALLED
    assert result['extension']['is_orphaned'] is False
    assert result['extension']['can_unmark_orphan'] is False
    schedule.assert_called_once_with(delay=0.25)


def test_install_failure_updates_browser_catalog_without_remote_refresh():
    script = r"""
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const text=fs.readFileSync(process.argv[1],'utf8');
const start=text.indexOf('window.manageSourceExtension=');
const end=text.indexOf('\nwindow.updateAllManagedSourceExtensions=',start);
assert(start>=0&&end>start);
const calls=[],notices=[];
const ctx={window:{},confirm:()=>true,toast:m=>notices.push(m),
 api:async()=>{throw new Error('Repository package unavailable after catalog refresh');},
 loadSourceExtensions:async refresh=>{calls.push(refresh);}};
vm.createContext(ctx);vm.runInContext(text.slice(start,end),ctx);
(async()=>{
 const button={disabled:false};
 await ctx.window.manageSourceExtension('pkg','install',button);
 assert.equal(button.disabled,false);
 assert.deepEqual(calls,[false]);
 assert(notices.some(m=>m.includes('Repository package unavailable')));
 await ctx.window.manageSourceExtension('pkg','uninstall',button);
 assert.deepEqual(calls,[false]);
 console.log('PASS install failure refreshes only local inventory');
})().catch(e=>{console.error(e);process.exitCode=1;});
"""
    result = subprocess.run(['node', '-e', script, str(Path(__file__).parents[1] / 'app/static/app.js')],
                            capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stdout + result.stderr
