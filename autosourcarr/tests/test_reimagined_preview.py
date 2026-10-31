"""Behavior checks for the optional synthetic preview, independent of app services."""
import importlib.util
import json
from pathlib import Path
import sys
import threading
from urllib.error import HTTPError
from urllib.request import Request, urlopen

import pytest

PROJECT = Path(__file__).resolve().parents[2]
SCRIPTS = PROJECT / 'scripts'
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location('reimagined_preview_test', SCRIPTS / 'preview-reimagined.py')
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)


@pytest.fixture
def sample():
    server = preview.create_server(0)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield f'http://127.0.0.1:{server.server_port}', server.preview_data
    server.shutdown()
    thread.join(timeout=2)
    server.server_close()
    assert not thread.is_alive()


def request(base, path, payload=None, method=None, headers=None):
    body = None if payload is None else json.dumps(payload).encode()
    req = Request(base + path, data=body, method=method, headers={'Content-Type':'application/json', **(headers or {})})
    try:
        result = urlopen(req, timeout=3)
    except HTTPError as error:
        result = error
    with result:
        return result.status, result.headers, result.read()


def test_preview_serves_real_frontend_and_cover_assets(sample):
    base, _ = sample
    status, headers, body = request(base, '/')
    assert status == 200
    assert b'data-preview="sample"' in body
    status, headers, body = request(base, '/api/v1/covers/1')
    assert status == 200
    assert headers['Content-Type'] == 'image/svg+xml'
    assert b'SAMPLE ARTWORK' in body


def test_preview_never_exposes_configuration_or_source_tree(sample):
    base, _ = sample
    for path in ('/config/autosourcarr.env', '/static/../../config/autosourcarr.env', '/static/%2e%2e/%2e%2e/config/autosourcarr.env', '/scripts/preview_data.py'):
        status, _, body = request(base, path)
        assert status == 404
        assert b'ADMIN_TOKEN=' not in body


def test_preview_rejects_operational_mutations_without_changing_data(sample):
    base, data = sample
    before = data.matrix()
    for path in ('/api/admin/maintenance/reset-database', '/api/admin/source-maintenance/repair', '/api/admin/import/mihon-backup'):
        status, _, body = request(base, path, {}, 'POST')
        assert status == 409
        assert b'no library, source, account or configuration was changed' in body
    assert data.matrix() == before


def test_preview_appearance_saves_are_memory_only_and_allowlisted(sample):
    base, data = sample
    status, _, body = request(base, '/api/admin/ui-preferences', {'values':{'themeStyle':'paper','ADMIN_TOKEN':'ignored'}}, 'PUT')
    assert status == 200
    assert data.preferences == {'themeStyle':'paper'}
    assert b'ignored' not in body
    status, _, _ = request(base, '/api/admin/ui-preferences', {'values':{'themeStyle':'neutral'}}, 'PUT', {'Origin':'https://unrelated.invalid'})
    assert status == 403
    assert data.preferences['themeStyle'] == 'paper'


def test_sample_library_filters_and_confirmed_gaps_are_consistent(sample):
    base, _ = sample
    status, _, body = request(base, '/api/admin/matrix?subbed=true&search=Frieren')
    snapshot = json.loads(body)
    assert status == 200
    assert [entry['id'] for entry in snapshot['entries']] == [3]
    status, _, body = request(base, '/api/admin/matrix?subbed=true')
    snapshot = json.loads(body)
    assert len(snapshot['entries']) == 12
    gaps = {record['entry_id'] for record in snapshot['matches'] if record['chapters_checked'] and record['missing_chapter_count'] > 0}
    assert gaps == {5,9}


def test_sample_activity_history_is_not_shadowed_by_entry_history(sample):
    base,_=sample
    status,_,body=request(base,'/api/admin/activity/history?offset=0&limit=50')
    result=json.loads(body)
    assert status==200 and result['total']==2 and len(result['items'])==2
    assert result['note'].startswith('Synthetic retained Activity')


def test_sample_health_and_chapter_provenance_are_explicit(sample):
    base, _ = sample
    _, _, body = request(base, '/api/admin/source-health')
    health = json.loads(body)
    assert len(health['items']) == 4
    assert all(row['state'] == 'working' and isinstance(row['checked_at'],int) for row in health['items'])
    _, _, body = request(base, '/api/admin/chapters/10012')
    chapter = json.loads(body)
    assert chapter['would_serve_candidate_id'] == chapter['candidates'][0]['id']
    assert chapter['chapter']['display_name'] == 'Chapter 12'
