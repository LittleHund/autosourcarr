"""The new explicit encoded filename header preserves UTF-8 and legacy percent names."""
from urllib.parse import quote

import httpx
import pytest

from app import main
from app.config import settings
from test_v0326_upload_receipts import upload_app
from test_v087_recovery import env


@pytest.mark.asyncio
@pytest.mark.parametrize('filename', ['日本語 100%.tachibk', '100%27literal.tachibk', 'spaced name.proto.gz'])
async def test_encoded_backup_filename_is_decoded_once(upload_app, filename):
    _, _, accepted = upload_app
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        result = await client.post('/api/admin/import/mihon-backup', content=b'backup',
                                   headers={'X-Filename-UTF8': quote(filename, safe='')})
    assert result.status_code == 200
    assert accepted[0][1] == filename
    assert result.json()['filename'] == filename


@pytest.mark.asyncio
async def test_legacy_percent_filename_is_retained_verbatim(upload_app):
    _, _, accepted = upload_app
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        result = await client.post('/api/admin/import/mihon-backup', content=b'backup',
                                   headers={'X-Filename': '100%27literal.tachibk'})
    assert result.status_code == 200
    assert accepted[0][1] == '100%27literal.tachibk'


@pytest.mark.asyncio
@pytest.mark.parametrize('encoded', ['%.tachibk', '%1.tachibk', '%GG.tachibk', '%C3%28.tachibk',
                                      'name%00.tachibk', 'name%0A.tachibk', 'name%7F.tachibk',
                                      quote('界' * 80 + '.tachibk', safe='')])
async def test_invalid_or_long_encoded_filename_creates_no_import_files(upload_app, encoded):
    _, _, accepted = upload_app
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        result = await client.post('/api/admin/import/mihon-backup', content=b'backup',
                                   headers={'X-Filename-UTF8': encoded})
    assert result.status_code == 400
    assert accepted == []
    assert not (settings.data_dir / 'imports').exists()
