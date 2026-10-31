"""Admin-only wizard endpoints; local disk work uses FastAPI's bounded thread pool."""
import asyncio
import os
import signal
from pathlib import Path
from urllib.parse import urlsplit
from fastapi import APIRouter, BackgroundTasks, Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from . import setup_store
from .setup_cleanup import cleanup_after_setup

class SetupFinish(BaseModel):
    revision: str = Field(max_length=64)
    transaction: str = Field(max_length=36)
    changes: dict = Field(default_factory=dict, max_length=len(setup_store.FIELDS))


class SetupReaderPreflight(BaseModel):
    revision: str = Field(max_length=64)
    changes: dict = Field(default_factory=dict, max_length=len(setup_store.FIELDS))


def same_origin(request):
    origin = request.headers.get('origin')
    if request.headers.get('sec-fetch-site') == 'cross-site' or (origin and urlsplit(origin).netloc != request.url.netloc):
        raise HTTPException(403, 'Open setup from this AutoSourcarr instance.')


def router(settings, guard, version):
    routes = APIRouter(prefix='/api/admin/setup', dependencies=[Depends(guard)])
    restarting = False

    def can_restart():
        return Path('/opt/autosourcarr-image-version').is_file() and settings.app_services_mode == 'integrated'

    @routes.get('')
    def get_setup(background: BackgroundTasks):
        try:
            result = setup_store.snapshot(settings.settings_config_dir, settings)
            if (result.get('cleanup') or {}).get('status') == 'pending':
                state = setup_store.state_at(settings.settings_config_dir)
                background.add_task(cleanup_after_setup, settings.settings_config_dir, state.get('transaction'), version)
            return JSONResponse({**result, 'can_restart': can_restart()}, headers={'Cache-Control': 'no-store'})
        except (OSError, ValueError):
            raise HTTPException(409, 'Cannot read setup configuration. Check that /config is mounted and writable, with valid env files; no settings were changed.')

    @routes.post('/reader-preflight')
    def reader_preflight(body: SetupReaderPreflight, request: Request):
        same_origin(request)
        try:
            result = setup_store.reader_preflight(settings.settings_config_dir, settings, body.changes, body.revision)
        except setup_store.SetupConflict as exc: raise HTTPException(409, str(exc))
        except ValueError as exc: raise HTTPException(400, str(exc))
        except OSError: raise HTTPException(409, 'Cannot read saved reader settings. Check the /config mount; no settings were changed.')
        return JSONResponse(result, headers={'Cache-Control': 'no-store'})

    @routes.post('/finish')
    def finish_setup(body: SetupFinish, request: Request, background: BackgroundTasks):
        same_origin(request)
        try:
            result = setup_store.finish(settings.settings_config_dir, settings, body.changes, body.revision, body.transaction)
        except setup_store.SetupConflict as exc: raise HTTPException(409, str(exc))
        except ValueError as exc: raise HTTPException(400, str(exc))
        except OSError: raise HTTPException(409, 'Could not save setup. Check the /config mount and write permissions; retry after correcting them.')
        background.add_task(cleanup_after_setup, settings.settings_config_dir, body.transaction, version)
        return JSONResponse({**result, 'can_restart': can_restart()}, headers={'Cache-Control': 'no-store'})

    @routes.post('/restart')
    async def restart(request: Request):
        nonlocal restarting
        same_origin(request)
        if not can_restart(): raise HTTPException(409, 'Restart this AutoSourcarr service from your container manager to apply settings.')
        state = await asyncio.to_thread(setup_store.state_at, settings.settings_config_dir)
        if not state.get('completed'): raise HTTPException(409, 'Finish setup before restarting.')
        if not restarting:
            restarting = True
            # Normal shutdown runs lifespan cleanup. Docker's unless-stopped policy restarts the service.
            asyncio.get_running_loop().call_later(1.5, os.kill, os.getpid(), signal.SIGTERM)
        return JSONResponse({'restarting': True}, headers={'Cache-Control': 'no-store'})
    return routes
