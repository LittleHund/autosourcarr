"""Keep the original source ecosystem instead of guessing a Keiyoushi origin."""
import importlib
from pathlib import Path

import pytest

PKG='eu.kanade.tachiyomi.extension.en.example'


@pytest.fixture
def api(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    return importlib.import_module('source_tools.ecosystem')


def extension(**values):
    return {'pkgName':PKG,'versionName':'1.4.23','storeIndexUrl':'https://github.com/keiyoushi/extensions/raw/repo/index.pb',**values}


def test_verified_index_mapping_selects_its_actual_source_repository(api):
    value=api.source_origin(extension())
    assert value['state']=='known' and value['repository']=='https://github.com/keiyoushi/extensions-source'
    assert value['module']=='src/en/example' and value['ecosystem']=='keiyoushi'
    assert 'entrypoint' not in value,'A build-module hint must not fabricate loading identity'


def test_manual_or_unknown_origins_request_code_instead_of_using_a_similar_name(api):
    for row in [extension(orphan_reason='manual-upload'),extension(storeIndexUrl=None),
                extension(storeIndexUrl='https://github.com/other/extensions/raw/main/index.pb')]:
        value=api.source_origin(row)
        assert value['state']=='needs-input' and not value.get('repository')


def test_original_third_party_repository_and_module_are_preserved(api):
    value=api.source_origin(extension(storeIndexUrl='https://github.com/other/binaries/raw/repo/index.pb'),
        supplied={'repository':'https://github.com/other/original-code.git','module':'extensions/example','revision':'b'*40})
    assert value['repository']=='https://github.com/other/original-code'
    assert value['module']=='extensions/example' and value['revision']=='b'*40
    assert value['basis']=='user-provided'


@pytest.mark.parametrize('repo',['https://user:secret@github.com/a/b','http://github.com/a/b',
                               'https://github.com/a/b?token=secret','https://127.0.0.1/a/b','https://github.com/a/../b'])
def test_raw_origin_rejects_credentials_private_addresses_and_ambiguous_paths(api,repo):
    with pytest.raises(ValueError):api.repository_plan(repo)


def test_repository_download_is_bound_to_resolved_immutable_commit(api):
    plan=api.repository_plan('https://github.com/owner/source')
    assert plan['resolve_url']=='https://api.github.com/repos/owner/source/commits/HEAD'
    assert api.repository_archive(plan,'c'*40)=='https://codeload.github.com/owner/source/zip/'+'c'*40
    with pytest.raises(ValueError):api.repository_archive(plan,'main')
    plan=api.repository_plan('https://gitlab.com/group/subgroup/source')
    assert '%2F' in plan['resolve_url']
    assert api.repository_archive(plan,'d'*40).endswith('/-/archive/'+'d'*40+'/source-'+'d'*40+'.zip')
