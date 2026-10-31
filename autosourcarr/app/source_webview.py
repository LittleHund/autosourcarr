"""Authenticated, fixed-path HTTP/WebSocket bridge to the engine WebView."""
from __future__ import annotations

import asyncio
import secrets
import time
from collections import OrderedDict
from urllib.parse import urlsplit

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request, WebSocket
from fastapi.responses import HTMLResponse, JSONResponse
from websockets.asyncio.client import connect

from .config import settings
from .suwayomi import source_host

PATH = '/api/admin/source-webview'
COOKIE = 'autosourcarr_webview'
LIFETIME = 3600


class WebviewSessions:
    def __init__(self):
        self.sessions = OrderedDict()

    def create(self):
        now = time.monotonic()
        self.sessions = OrderedDict((key, value) for key, value in self.sessions.items() if value[0] > now)
        token = secrets.token_urlsafe(32)
        self.sessions[token] = (now + LIFETIME, settings.admin_token)
        while len(self.sessions) > 32:
            self.sessions.popitem(last=False)
        return token

    def valid(self, token):
        entry = self.sessions.get(token or '')
        return bool(entry and entry[0] > time.monotonic() and secrets.compare_digest(entry[1].encode(), settings.admin_token.encode()))


sessions = WebviewSessions()


def origin_parts(value):
    try:
        url = urlsplit(str(value))
        scheme = {'ws': 'http', 'wss': 'https'}.get(url.scheme, url.scheme)
        if scheme not in {'http', 'https'} or not url.hostname or url.username or url.password:
            return None
        return (scheme, url.hostname, url.port or (443 if scheme == 'https' else 80))
    except ValueError:
        return None


def public_origin_for(connection):
    public = origin_parts(settings.autosourcarr_public_url)
    actual = origin_parts(connection.url)
    # Only accept the explicitly configured public origin for that same Host.
    return public if public and actual and public[1] == actual[1] else None


def secure_connection(connection):
    public = public_origin_for(connection)
    return connection.url.scheme in {'https', 'wss'} or bool(public and public[0] == 'https')


def same_origin(connection, *, required=False):
    value = connection.headers.get('origin')
    if not value:
        return not required
    try:
        given = urlsplit(value)
        if given.path or given.query or given.fragment:
            return False
    except ValueError:
        return False
    origin = origin_parts(value)
    return bool(origin and origin in {origin_parts(connection.url), public_origin_for(connection)})


def webview_router(admin_guard):
    router = APIRouter()

    @router.post(PATH + '/session', dependencies=[Depends(admin_guard)])
    async def open_session(request: Request):
        from .source_browser import source_browser
        if source_browser.busy:
            raise HTTPException(503, "Source browser maintenance is running; reopen WebView shortly")
        if not same_origin(request):
            raise HTTPException(403, 'WebView must be opened from AutoSourcarr.')
        response = JSONResponse({'url': PATH, 'expires_in': LIFETIME}, headers={'Cache-Control': 'no-store'})
        response.set_cookie(COOKIE, sessions.create(), max_age=LIFETIME, httponly=True,
                            secure=secure_connection(request), samesite='strict', path=PATH)
        return response

    @router.get(PATH)
    async def page(request: Request):
        if not sessions.valid(request.cookies.get(COOKIE)) or not same_origin(request):
            raise HTTPException(401, 'Open WebView from Sources in AutoSourcarr to start a session.')
        try:
            async with httpx.AsyncClient(timeout=10, trust_env=False) as client:
                response = await client.get(source_host.base_url + '/api/v1/webview')
                response.raise_for_status()
                if 'text/html' not in response.headers.get('content-type', '') or len(response.content) > 2_000_000:
                    raise ValueError('Invalid WebView response')
            return HTMLResponse(response.content, headers={'Cache-Control': 'no-store',
                'Referrer-Policy': 'no-referrer', 'X-Content-Type-Options': 'nosniff',
                'X-Frame-Options': 'SAMEORIGIN',
                'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'self'; base-uri 'none'"})
        except (httpx.HTTPError, ValueError):
            return HTMLResponse('<h1>Source WebView is starting or unavailable</h1><p>Return to AutoSourcarr Status to check the source engine, then reopen WebView.</p>', status_code=503, headers={'Cache-Control': 'no-store'})

    @router.websocket(PATH)
    async def socket(websocket: WebSocket):
        if not sessions.valid(websocket.cookies.get(COOKIE)) or not same_origin(websocket, required=True):
            await websocket.close(code=1008)
            return
        upstream = source_host.base_url.replace('https://', 'wss://', 1).replace('http://', 'ws://', 1) + '/api/v1/webview'
        jobs = []
        try:
            # No browser headers, admin token, cookies, paths or query strings are
            # forwarded. The engine is local; WebView alone receives the session.
            async with connect(upstream, open_timeout=10, max_size=16*1024*1024, proxy=None) as remote:
                await websocket.accept()

                async def browser_to_engine():
                    while True:
                        event = await websocket.receive()
                        if event['type'] == 'websocket.disconnect':
                            return
                        if event.get('text') is not None:
                            await remote.send(event['text'])
                        elif event.get('bytes') is not None:
                            await remote.send(event['bytes'])

                async def engine_to_browser():
                    async for message in remote:
                        if isinstance(message, bytes):
                            await websocket.send_bytes(message)
                        else:
                            await websocket.send_text(message)

                jobs = [asyncio.create_task(browser_to_engine()), asyncio.create_task(engine_to_browser()),
                        asyncio.create_task(asyncio.sleep(max(0, sessions.sessions[websocket.cookies[COOKIE]][0] - time.monotonic())))]
                await asyncio.wait(jobs, return_when=asyncio.FIRST_COMPLETED)
        except Exception:
            # Do not expose upstream addresses or credentials to the browser.
            pass
        finally:
            for job in jobs:
                job.cancel()
            await asyncio.gather(*jobs, return_exceptions=True)
            try:
                await websocket.close(code=1000)
            except RuntimeError:
                pass

    return router
