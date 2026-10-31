import importlib
import json
import pytest
from test_v0345_raw_archive import PKG,FILES,archive


@pytest.fixture
def api(monkeypatch):
    from pathlib import Path
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    return importlib.import_module('source_tools.discovery')


def local(**extra):return {'pkgName':PKG,'versionName':'1.4.23','orphan_reason':'manual-upload',**extra}


def test_official_code_mapping_can_locate_a_manual_installs_declared_family(api):
    row=api.discover(local(storeIndexUrl='https://github.com/keiyoushi/extensions/raw/repo/index.pb'),[],lambda *a,**k:pytest.fail('Known declaration needs no web search'))
    assert row['repository']=='https://github.com/keiyoushi/extensions-source'
    assert row['basis']=='repository-candidate' and row['state']=='known'


def test_configured_repository_must_contain_the_exact_package_before_nomination(api):
    calls=[]
    def fetch(url,**options):
        calls.append(url);return json.dumps([{'pkg':PKG}]).encode(),url
    row=api.discover(local(),['https://github.com/keiyoushi/extensions/raw/repo/index.pb'],fetch)
    assert row['state']=='known' and calls==['https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.min.json']
    missing=api.discover(local(pkgName='different.package.name'),['https://github.com/keiyoushi/extensions/raw/repo/index.pb'],fetch)
    assert missing['state']=='needs-input' and 'repository' not in missing


def test_generic_index_follows_only_one_explicit_same_publisher_code_section(api):
    index='https://github.com/community/extensions/raw/repo/index.pb'
    def fetch(url,**options):return b'# Project\n## Source Code\nhttps://github.com/community/source-code\n## Other links\nhttps://github.com/unrelated/clone\n',url
    row=api.declared_source(index,fetch)
    assert row['repository']=='https://github.com/community/source-code'
    for text in [b'## Source Code\nhttps://github.com/other/source',b'## Source Code\nhttps://github.com/community/a\nhttps://github.com/community/b',b'Use https://github.com/community/code']:
        assert api.declared_source(index,lambda url,**k:(text,url)) is None


def test_readme_redirect_cannot_change_the_code_publisher(api):
    assert api.declared_source('https://github.com/a/extensions/raw/repo/index.pb',lambda url,**k:(b'## Source Code\nhttps://github.com/a/code','https://raw.githubusercontent.com/evil/extensions/main/README.md')) is None


@pytest.mark.parametrize('url',['http://github.com/a/extensions/raw/repo/index.pb','https://user:pass@github.com/a/b/raw/main/index.pb','https://github.com/a/b/raw/refs/heads','https://127.0.0.1/index.json'])
def test_invalid_index_does_not_start_an_unscoped_fetch(api,url):
    result=api.discover(local(storeIndexUrl=url),[],lambda *a,**k:pytest.fail('No unscoped public request'))
    assert result['state']=='needs-input'


def test_catalog_discovery_preserves_binary_and_labels_candidate_not_exact_original(tmp_path):
    from app.source_raws import SourceRawCatalog
    def fetch(url,**options):return (b'a'*40 if '/commits/' in url else archive(FILES,prefix='repo/')),url
    catalog=SourceRawCatalog(tmp_path,fetch=fetch)
    row=local(storeIndexUrl='https://github.com/keiyoushi/extensions/raw/repo/index.pb')
    waiting=catalog.begin(row,binary_sha256='1'*64)
    assert waiting['state']=='needs-input'
    found=catalog.discover(waiting,row)
    retained=catalog.acquire(found)
    assert retained['state']=='available' and retained['binary_sha256']=='1'*64
    _,saved=catalog.resolve(PKG,retained['raw_id'])
    assert saved['kind']=='provided' and saved['origin']['basis']=='repository-candidate'
    assert 'not proven' in retained['message']


def test_late_discovery_cannot_replace_newly_supplied_code(tmp_path):
    from app.source_raws import SourceRawCatalog
    row=local(storeIndexUrl='https://github.com/keiyoushi/extensions/raw/repo/index.pb')
    catalog=SourceRawCatalog(tmp_path);old=catalog.begin(row,binary_sha256='1'*64)
    latest=catalog.provide(row,archive(FILES),expected_revision=old['revision'],binary_sha256='1'*64)
    with pytest.raises(ValueError):catalog.discover(old,row)
    assert catalog.status(PKG)==latest


def test_reopening_a_checked_request_preserves_the_actual_lookup_failure(tmp_path):
    from app.source_raws import SourceRawCatalog
    row=local(storeIndexUrl='https://github.com/keiyoushi/extensions/raw/repo/index.pb')
    def unavailable(*args,**kwargs):raise ValueError('Original module no longer exists')
    catalog=SourceRawCatalog(tmp_path,fetch=unavailable)
    waiting=catalog.begin(row,binary_sha256='1'*64)
    found=catalog.discover(waiting,row);failed=catalog.acquire(found)
    assert failed['state']=='needs-input' and 'module no longer exists' in failed['message']
    assert catalog.begin(row,binary_sha256='1'*64)==failed
