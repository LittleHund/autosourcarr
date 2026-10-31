import importlib,json
from pathlib import Path
import pytest
from test_v0345_raw_archive import PKG,MODULE,FILES,archive


def test_removed_module_recovers_one_original_repository_parent_as_candidate(tmp_path,monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    from source_tools.archive import RawStore
    from source_tools.repository import RepositoryCache
    calls=[];head='a'*40;deletion='b'*40;parent='c'*40
    def fetch(url,**options):
        calls.append(url)
        if url.endswith('/commits/HEAD'):return head.encode(),url
        if '/commits?path=' in url:return json.dumps([{'sha':deletion,'parents':[{'sha':parent}]}]).encode(),url
        if url.endswith('build.gradle.kts'):
            if deletion in url:raise ValueError('Repository returned HTTP 404')
            return b'extVersionCode = 23\nlibVersion = "1.4"',url
        return (archive({'README.md':b'No selected module'},prefix='repo/') if head in url else archive(FILES,prefix='repo/')),url
    cache=RepositoryCache(tmp_path/'cache',RawStore(tmp_path/'raw'),fetch)
    value=cache.obtain({'state':'known','repository':'https://github.com/keiyoushi/extensions-source','revision':'','module':MODULE,'package':PKG,'version':'1.4.23','ecosystem':'keiyoushi','basis':'known-repository'})
    assert value['origin']['revision']==parent and value['origin']['repository']=='https://github.com/keiyoushi/extensions-source'
    assert value['kind']=='provided' and value['origin']['basis']=='repository-candidate'
    assert len(calls)==6


def test_explicit_missing_commit_is_not_silently_replaced_with_history(tmp_path,monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    from source_tools.archive import RawStore
    from source_tools.repository import RepositoryCache
    calls=[]
    def fetch(url,**options):calls.append(url);return archive({'README.md':b'No module'},prefix='repo/'),url
    cache=RepositoryCache(tmp_path/'cache',RawStore(tmp_path/'raw'),fetch)
    with pytest.raises(ValueError,match='No Kotlin/Java'):
        cache.obtain({'state':'known','repository':'https://github.com/keiyoushi/extensions-source','revision':'a'*40,'module':MODULE,'package':PKG,'version':'1.4.23','ecosystem':'keiyoushi','basis':'user-provided'})
    assert len(calls)==1
