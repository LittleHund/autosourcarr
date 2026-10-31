"""Install-time raw capture and actionable, version-bound missing-code state."""
import importlib
import json

import pytest

from test_v0345_raw_archive import FILES, MODULE, PKG, archive


@pytest.fixture
def api():return importlib.import_module('app.source_raws')


def extension(**values):
    return {'pkgName':PKG,'versionName':'1.4.23','isInstalled':True,
        'storeIndexUrl':'https://github.com/keiyoushi/extensions/raw/repo/index.pb',**values}


def test_normal_install_captures_original_raws_and_persists_the_code_reference(tmp_path,api):
    calls=[]
    def fetch(url,**options):
        calls.append(url)
        return (b'{"sha":"'+b'a'*40+b'"}' if url.endswith('/commits/HEAD') else archive(FILES,prefix='repo-commit/')),url
    catalog=api.SourceRawCatalog(tmp_path,fetch=fetch,clock=lambda:1000.)
    intent=catalog.begin(extension())
    assert intent['state']=='acquiring'
    result=catalog.acquire(intent)
    assert result['state']=='available' and result['visibility']=='shared' and len(calls)==2
    store,snapshot=catalog.resolve(PKG,result['raw_id'])
    assert snapshot['kind']=='upstream' and snapshot['module']==MODULE
    assert len(store.export_zip(PKG,result['raw_id']))>100
    restarted=api.SourceRawCatalog(tmp_path,fetch=lambda *a,**k:pytest.fail('Status must not download anything'))
    assert restarted.status(PKG)['raw_id']==result['raw_id']


def test_manual_missing_code_requests_input_and_never_guesses_the_repo(tmp_path,api):
    catalog=api.SourceRawCatalog(tmp_path,fetch=lambda *a,**k:pytest.fail('Manual source must not fetch a guessed repository'))
    result=catalog.begin(extension(),manual=True,binary_sha256='1'*64)
    assert result['state']=='needs-input' and not result.get('raw_id')
    rows=catalog.pending_items()
    assert len(rows)==1 and rows[0]['activity']=='waiting' and rows[0]['raw_package']==PKG


def test_user_code_is_private_by_default_and_stale_file_dialog_cannot_replace_it(tmp_path,api):
    catalog=api.SourceRawCatalog(tmp_path)
    first=catalog.begin(extension(),manual=True,binary_sha256='1'*64)
    result=catalog.provide(extension(),archive(FILES),expected_revision=first['revision'],binary_sha256='1'*64,module=MODULE)
    assert result['state']=='available' and result['visibility']=='private'
    store,snapshot=catalog.resolve(PKG,result['raw_id'])
    assert snapshot['kind']=='provided'
    assert not list((tmp_path/'sources/raw').glob('versions/*/*.json'))
    with pytest.raises(ValueError):
        catalog.provide(extension(),archive(FILES),expected_revision=first['revision'],binary_sha256='1'*64,module=MODULE)
    assert catalog.status(PKG)==result


def test_source_update_invalidates_old_raw_input_without_losing_archived_code(tmp_path,api):
    catalog=api.SourceRawCatalog(tmp_path)
    old=catalog.begin(extension(),manual=True,binary_sha256='1'*64)
    latest=catalog.begin(extension(versionName='1.4.24'),manual=True,binary_sha256='2'*64)
    with pytest.raises(ValueError):catalog.provide(extension(),archive(FILES),expected_revision=old['revision'],binary_sha256='1'*64,module=MODULE)
    assert catalog.status(PKG)['revision']==latest['revision'] and catalog.status(PKG)['version']=='1.4.24'


def test_cached_task_projection_does_not_read_files_or_contact_sources(tmp_path,api,monkeypatch):
    catalog=api.SourceRawCatalog(tmp_path)
    catalog.begin(extension(),manual=True,binary_sha256='1'*64)
    monkeypatch.setattr(catalog,'_read',lambda *a:pytest.fail('Task rendering must use the bounded observed cache'))
    assert catalog.pending_items()[0]['kind']=='source-raw-input'
    assert catalog.status(PKG)['state']=='needs-input'


def test_exported_reconstructed_recipe_survives_explicit_cross_instance_upload_as_private_provided_code(tmp_path,api):
    from bundled.source_tools.archive import RawStore
    first=RawStore(tmp_path/'instance-one')
    info={'package':PKG,'version':'1.4.23','module':'.','ecosystem':'tachiyomi','recipe':'jvm','kind':'repaired',
          'origin':{'basis':'binary-reconstruction'},'overlay_sources':['src/Example.java']}
    saved=first.put_zip(archive({'src/Example.java':b'class Example {}'}),info)
    exported=first.export_zip(PKG,saved['id'],portable=True)
    catalog=api.SourceRawCatalog(tmp_path/'instance-two')
    request=catalog.begin(extension(),manual=True,binary_sha256='1'*64)
    result=catalog.provide(extension(),exported,expected_revision=request['revision'],binary_sha256='1'*64,module=MODULE)
    _,retained=catalog.resolve(PKG,result['raw_id'])
    assert retained['recipe']=='jvm' and retained['module']=='.' and retained['overlay_sources']==['src/Example.java']
    assert retained['kind']=='provided' and retained['origin']['basis']=='user-provided' and result['visibility']=='private'
