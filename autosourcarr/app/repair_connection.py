"""Current-instance repair controls; legacy deployment helpers are CLI-only."""
import hashlib
import json
from pathlib import Path
import re
import secrets
from urllib.parse import urlsplit

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import JSONResponse, Response
import yaml

from .config import settings
from .repair_setup import transform, environment_mapping

HEADERS = {'Cache-Control': 'no-store'}


def connection_path():
    return settings.settings_config_dir/'repair-connection.json'


def restore_connection():
    if settings.app_services_mode == "integrated": return
    path = connection_path()
    if path.is_file():
        value = json.loads(path.read_text())
        url, token = validated_connection(value['url'], value['token'])
        settings.repair_worker_url, settings.repair_worker_token = url, token


def validated_connection(url, token):
    if not isinstance(url, str) or not isinstance(token, str): raise ValueError('Enter a worker URL and token')
    url = url.strip().rstrip('/')
    try: parsed = urlsplit(url); port = parsed.port
    except ValueError: raise ValueError('Invalid worker URL')
    if parsed.scheme not in {'http', 'https'} or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path or any(c.isspace() for c in url):
        raise ValueError('Use an HTTP(S) worker address without a path, credentials or query string')
    if not re.fullmatch(r'[A-Za-z0-9_-]{32,256}', token): raise ValueError('Worker token must contain 32–256 letters, numbers, underscores or hyphens')
    return url, token


def connection_snapshot():
    url, token = settings.repair_worker_url, settings.repair_worker_token
    revision = hashlib.sha256((url+'\0'+token).encode()).hexdigest()
    return {'mode': settings.app_services_mode, 'url': url, 'token_saved': bool(token), 'revision': revision, 'saved_in_ui': connection_path().is_file()}


class ComposeLoader(yaml.SafeLoader):
    def compose_node(self, parent, index):
        if self.check_event(yaml.AliasEvent): raise ValueError('Expand YAML aliases before generating the setup preview')
        self.depth = getattr(self, 'depth', 0)+1
        if self.depth > 35: raise ValueError('Compose nesting exceeds the setup limit')
        try: return super().compose_node(parent, index)
        finally: self.depth -= 1

    def construct_mapping(self, node, deep=False):
        result = {}
        for key, value in node.value:
            name = self.construct_object(key, deep=deep)
            if not isinstance(name, str) or name in result: raise ValueError('Compose contains duplicate or non-text keys')
            result[name] = self.construct_object(value, deep=deep)
        return result


def preview(body):
    if not isinstance(body, dict): raise ValueError('Expected setup fields')
    raw = body.get('compose', '')
    if not isinstance(raw, str) or len(raw) > 1024*1024: raise ValueError('Compose preview is limited to 1 MiB')
    try: original = yaml.load(raw, Loader=ComposeLoader)
    except yaml.YAMLError: raise ValueError('Compose could not be parsed. Check its indentation; no changes were made.')
    if not isinstance(original, dict) or not isinstance(original.get('services'), dict): raise ValueError('Paste the complete Compose including services')
    project, data = body.get('project_path', ''), body.get('data_path', '')
    for path in (project, data):
        if not isinstance(path, str) or not re.fullmatch(r'/[A-Za-z0-9_./-]+', path) or '..' in Path(path).parts or Path(path) == Path('/'):
            raise ValueError('Use absolute host paths without spaces or Compose variables')
    project, data = Path(project), Path(data)
    if project == data or project in data.parents or data in project.parents: raise ValueError('Repair data must be separate from the project folder')
    selected = body.get('services')
    if not isinstance(selected, list) or not selected or any(not isinstance(s, str) for s in selected): raise ValueError('Select at least one AutoSourcarr service')
    existing = original['services'].get('autosourcarr-repair', {})
    env = environment_mapping(existing.get('environment', {}))
    if env.get('REPAIR_ROOT') and env['REPAIR_ROOT'] != str(data): raise ValueError('Keep the existing repair data path; this screen does not move account data or jobs')
    token = env.get('REPAIR_TOKEN') or secrets.token_hex(32)
    if not re.fullmatch(r'[A-Za-z0-9_-]{32,256}', token): raise ValueError('Existing repair token uses substitution. Keep your current deployment and use Connect existing worker instead.')
    network = env.get('REPAIR_JOB_NETWORK', 'autosourcarr-repair-jobs').removesuffix('-jobs')
    if not re.fullmatch('[a-zA-Z0-9_-]+', network): raise ValueError('Existing repair network needs manual review')
    values = {'REPAIR_ROOT': str(data), 'REPAIR_TOKEN': token, 'REPAIR_NETWORK': network}
    result = transform(original, project, values, selected)
    return {'compose': yaml.safe_dump(result, sort_keys=False), 'services': selected,
            'message': 'Preview only. Replace the saved Compose of this same Custom App and Save/Redeploy once. All existing app volumes and ports are retained. This file contains your existing secrets and the private worker token.'}


async def read_body(request, limit=1100000):
    raw = bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw) > limit: raise HTTPException(413, 'Setup request is too large')
    try: return json.loads(raw)
    except ValueError: raise HTTPException(400, 'Expected JSON setup fields')


def connection_router(worker, lock):
    async def same_site(request: Request):
        if request.method in {'GET', 'HEAD', 'OPTIONS'}: return
        origin = request.headers.get('origin')
        hosts = {request.headers.get('host', ''), request.headers.get('x-forwarded-host', '').split(',')[0].strip()} - {''}
        if request.headers.get('sec-fetch-site') == 'cross-site' or (origin and urlsplit(origin).netloc not in hosts):
            raise HTTPException(403, 'Open this action from AutoSourcarr itself')

    from fastapi import Depends
    router = APIRouter(dependencies=[Depends(same_site)])

    @router.get('/connection')
    async def connection():
        return JSONResponse(connection_snapshot(), headers=HEADERS)

    @router.put('/connection')
    @router.post('/setup-preview')
    async def retired_external_setup():
        # Reject old cached clients before reading Compose, paths or credentials.
        raise HTTPException(410, 'Repair runs inside this AutoSourcarr instance. Refresh the page and use Configure repair AI; external worker setup has been removed from the web UI.', headers=HEADERS)

    @router.get('/control')
    async def control():
        return JSONResponse(await worker('GET', '/control'), headers=HEADERS)

    @router.get('/provider')
    async def provider():
        return JSONResponse(await worker('GET', '/provider'), headers=HEADERS)

    @router.get('/host-setup')
    def host_setup():
        from .repair_host_setup import export_host_setup
        try: return Response(export_host_setup(),media_type='application/zip',headers={**HEADERS,'Content-Disposition':'attachment; filename="autosourcarr-repair-host-setup.zip"'})
        except (ValueError,OSError): raise HTTPException(503,'Bundled repair host policies are unavailable. Rebuild the current image.',headers=HEADERS)

    @router.post('/provider/test')
    async def provider_test():
        return JSONResponse(await worker('POST', '/provider/test'), headers=HEADERS)

    @router.post('/control/{action}', status_code=202)
    async def control_action(action: str, request: Request):
        if action not in {'prepare', 'login', 'login-status', 'logout', 'cancel-login', 'retry-cleanup'}: raise HTTPException(404, 'Unknown worker action')
        raw=await request.body()
        if len(raw)>512: raise HTTPException(413,'Account request is too large')
        payload=None
        if raw:
            try:
                body=json.loads(raw)
                if body is not None:
                    import uuid
                    payload={'operation_id':str(uuid.UUID(body['operation_id']))}
            except (ValueError,KeyError,TypeError,AttributeError): raise HTTPException(400,'Invalid account operation identifier')
        result=await worker('POST','/control/'+action,**({'payload':payload} if payload else {}))
        return JSONResponse(result,status_code=202,headers=HEADERS)

    return router
