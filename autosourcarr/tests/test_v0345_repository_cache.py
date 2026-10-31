"""Archive source modules efficiently without sharing private/native state."""
import importlib
import json
from pathlib import Path

import pytest

from test_v0345_raw_archive import FILES, MODULE, PKG, archive


@pytest.fixture
def modules(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    return importlib.import_module('source_tools.repository'),importlib.import_module('source_tools.archive')


def origin(**changes):
    return {'state':'known','package':PKG,'version':'1.4.23','module':MODULE,'ecosystem':'keiyoushi',
        'repository':'https://github.com/keiyoushi/extensions-source','revision':'','basis':'known-repository',**changes}


def test_two_source_modules_reuse_one_pinned_repository_download(tmp_path,modules):
    repo,raw=modules;calls=[]
    files={**FILES,'src/en/other/build.gradle.kts':b'versionCode = 1','src/en/other/src/Other.kt':b'class Other'}
    payload=archive(files,prefix='repository-commit/')
    def fetch(url,**options):
        calls.append((url,options))
        return (json.dumps({'sha':'a'*40}).encode() if url.endswith('/commits/HEAD') else payload),url
    store=raw.RawStore(tmp_path/'shared',public=True)
    cache=repo.RepositoryCache(tmp_path/'private',store,fetch,clock=lambda:1000.)
    first=cache.obtain(origin())
    second=cache.obtain(origin(package='eu.kanade.tachiyomi.extension.en.other',module='src/en/other',version='1.4.1'))
    assert len(calls)==2,'Commit resolution and one ZIP are shared across module downloads'
    assert first['origin']['revision']==second['origin']['revision']=='a'*40
    assert MODULE+'/src/Example.kt' in first['files'] and MODULE+'/src/Example.kt' not in second['files']
    assert 'src/en/other/src/Other.kt' in second['files']
    assert not list((tmp_path/'shared').rglob('*.zip')),'Download caches are not shared installed/raw packages'
    assert len(list((tmp_path/'private').rglob('*.zip')))==1
    restarted=repo.RepositoryCache(tmp_path/'private',store,lambda *a,**k:pytest.fail('Restart must reuse immutable cached bytes'),clock=lambda:1001.)
    assert restarted.obtain(origin())['id']==first['id']


def test_branch_name_is_never_accepted_as_a_resolved_commit(tmp_path,modules):
    repo,raw=modules;calls=[]
    def fetch(url,**options):calls.append(url);return b'{"sha":"main"}',url
    cache=repo.RepositoryCache(tmp_path/'private',raw.RawStore(tmp_path/'raw'),fetch)
    with pytest.raises(ValueError):cache.obtain(origin())
    assert len(calls)==1 and not list((tmp_path/'raw').glob('versions/*/*.json'))


def test_explicit_original_commit_avoids_a_head_lookup(tmp_path,modules):
    repo,raw=modules;calls=[]
    def fetch(url,**options):calls.append(url);return archive(FILES,prefix='repo-commit/'),url
    result=repo.RepositoryCache(tmp_path/'private',raw.RawStore(tmp_path/'raw'),fetch).obtain(origin(revision='b'*40))
    assert result['origin']['revision']=='b'*40 and len(calls)==1 and '/zip/' in calls[0]


def test_missing_code_is_an_input_requirement_and_does_not_fetch_another_repo(tmp_path,modules):
    repo,raw=modules
    cache=repo.RepositoryCache(tmp_path/'private',raw.RawStore(tmp_path/'raw'),lambda *a,**k:pytest.fail('No original source repository was supplied'))
    with pytest.raises(ValueError):cache.obtain(origin(state='needs-input',repository=None))
