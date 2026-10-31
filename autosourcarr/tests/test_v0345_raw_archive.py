"""Immutable raw code, shared object deduplication and unsafe-input rejection."""
import importlib
import io
import json
from pathlib import Path
import stat
import zipfile

import pytest

PKG='eu.kanade.tachiyomi.extension.en.example'
MODULE='src/en/example'
FILES={'LICENSE':b'PUBLIC_LICENSE','gradlew':b'#!/bin/sh\nexec gradle "$@"\n',
       'settings.gradle.kts':b'include(":src:en:example")',
       MODULE+'/build.gradle.kts':b'versionCode = 23',
       MODULE+'/src/Example.kt':b'package eu.kanade.tachiyomi.extension.en.example\nclass Example'}


def archive(files,prefix=''):
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as stream:
        for name,data in files.items():stream.writestr(prefix+name,data)
    return output.getvalue()


def metadata(**changes):
    return {'package':PKG,'version':'1.4.23','module':MODULE,'ecosystem':'tachiyomi',
        'recipe':'gradle','kind':'upstream','origin':{'repository':'https://github.com/keiyoushi/extensions-source','revision':'a'*40},**changes}


@pytest.fixture
def api(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]))
    return importlib.import_module('source_tools.archive')


def test_two_instances_share_only_raw_code_and_deduplicate_common_files(tmp_path,api):
    first=api.RawStore(tmp_path/'shared');second=api.RawStore(tmp_path/'shared')
    old=first.put_zip(archive(FILES,prefix='extensions-source-commit/'),metadata())
    same=second.put_zip(archive(FILES),metadata())
    assert old['id']==same['id']
    assert len(list((tmp_path/'shared/objects').glob('*/*')))==len(FILES)
    changed={**FILES,MODULE+'/src/Example.kt':FILES[MODULE+'/src/Example.kt']+b'\n// repair'}
    new=second.put_zip(archive(changed),metadata(kind='repaired',parent=old['id']))
    assert new['id']!=old['id']
    assert len(list((tmp_path/'shared/objects').glob('*/*')))==len(FILES)+1
    with zipfile.ZipFile(io.BytesIO(first.export_zip(PKG,old['id']))) as stream:
        assert set(stream.namelist())==set(FILES)
        assert stream.read(MODULE+'/src/Example.kt')==FILES[MODULE+'/src/Example.kt']
    with zipfile.ZipFile(io.BytesIO(first.export_zip(PKG,new['id']))) as stream:
        assert stream.read(MODULE+'/src/Example.kt').endswith(b'// repair')
    assert not any(path.suffix in {'.apk','.class'} for path in (tmp_path/'shared').rglob('*'))


def test_project_agent_configuration_is_not_materialized_into_an_editor_or_shared_archive(tmp_path,api):
    store=api.RawStore(tmp_path/'raws')
    value=store.put_zip(archive({**FILES,'.codex/config.toml':b'untrusted project agent configuration',
        '.agents/private.txt':b'synthetic private settings'}),metadata())
    with zipfile.ZipFile(io.BytesIO(store.export_zip(PKG,value['id']))) as files:
        assert not any(name.startswith(('.codex/','.agents/')) for name in files.namelist())


def test_portable_export_preserves_the_jvm_recipe_without_putting_metadata_in_editable_code(tmp_path,api):
    store=api.RawStore(tmp_path/'raw')
    info=metadata(module='.',recipe='jvm',kind='reconstructed',overlay_sources=['src/Example.java'])
    value=store.put_zip(archive({'src/Example.java':b'class Example {}'}),info)
    exported=store.export_zip(PKG,value['id'],portable=True)
    assert api.portable_metadata(exported)['overlay_sources']==['src/Example.java']
    restored=store.put_zip(exported,info)
    assert restored['id']==value['id']
    target=tmp_path/'edit';store.materialize(PKG,value['id'],target)
    assert not (target/api.PORTABLE).exists()


@pytest.mark.parametrize('name',['../escape.kt','/absolute.kt','C:/secret.kt','dir\\escape.kt','nul\x00hidden.kt'])
def test_unsafe_archive_paths_are_rejected_before_publishing(tmp_path,api,name):
    # Construct the actual wire name: Windows ZipInfo normalizes backslashes and
    # truncates NUL before writing, so passing a malformed Python name is not enough.
    placeholder=name.replace('\\','/').replace('\x00','X')
    payload=archive({placeholder:b'bad',**FILES}).replace(placeholder.encode(),name.encode())
    with pytest.raises(ValueError):api.RawStore(tmp_path/'raw').put_zip(payload,metadata())
    assert not (tmp_path/'escape.kt').exists()
    assert not list((tmp_path/'raw').glob('versions/*/*.json'))


@pytest.mark.parametrize('name',['.env','local.properties','auth.json','cookies.json','signingkey.jks'])
def test_private_files_are_never_accepted_as_shareable_raws(tmp_path,api,name):
    with pytest.raises(ValueError):
        api.RawStore(tmp_path/'raw').put_zip(archive({**FILES,name:b'PRIVATE_CANARY'}),metadata())
    assert not list((tmp_path/'raw').glob('versions/*/*.json'))


def test_build_outputs_are_not_raw_inputs_and_link_entries_fail_closed(tmp_path,api):
    store=api.RawStore(tmp_path/'raw')
    value=store.put_zip(archive({**FILES,MODULE+'/build/outputs/source.apk':b'INSTALLED_BINARY'}),metadata())
    with zipfile.ZipFile(io.BytesIO(store.export_zip(PKG,value['id']))) as stream:
        assert not any(name.endswith('.apk') for name in stream.namelist())
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w') as stream:
        for name,data in FILES.items():stream.writestr(name,data)
        info=zipfile.ZipInfo(MODULE+'/src/linked.kt');info.external_attr=(stat.S_IFLNK|0o777)<<16
        stream.writestr(info,'/config/private')
    with pytest.raises(ValueError):store.put_zip(output.getvalue(),metadata())


def test_metadata_cannot_publish_account_feedback_or_credential_urls(tmp_path,api):
    store=api.RawStore(tmp_path/'raw')
    with pytest.raises(ValueError):store.put_zip(archive(FILES),metadata(feedback='PRIVATE_FEEDBACK'))
    with pytest.raises(ValueError):store.put_zip(archive(FILES),metadata(origin={'repository':'https://user:secret@github.com/a/b','revision':'a'*40}))
    with pytest.raises(ValueError):store.put_zip(archive(FILES),metadata(origin={'repository':'https://github.com/a/b?token=PRIVATE','revision':'a'*40}))


@pytest.mark.parametrize('changes',[{'ecosystem':{}},{'recipe':[]},{'kind':[]},{'origin':[]}])
def test_malformed_metadata_shapes_are_rejected_as_invalid_input(tmp_path,api,changes):
    with pytest.raises(ValueError):api.RawStore(tmp_path/'raw').put_zip(archive(FILES),metadata(**changes))


def test_corrupt_objects_are_refused_without_overwriting_previous_code(tmp_path,api):
    store=api.RawStore(tmp_path/'raw');value=store.put_zip(archive(FILES),metadata())
    ref=value['files'][MODULE+'/src/Example.kt']['sha256']
    target=tmp_path/'raw/objects'/ref[:2]/ref[2:]
    target.write_bytes(b'TAMPERED')
    with pytest.raises(ValueError):store.export_zip(PKG,value['id'])
    assert target.read_bytes()==b'TAMPERED'


def test_restore_requires_empty_owned_directory_and_preserves_raw_layout(tmp_path,api):
    store=api.RawStore(tmp_path/'raw');value=store.put_zip(archive(FILES),metadata())
    work=tmp_path/'job';work.mkdir()
    store.materialize(PKG,value['id'],work)
    assert (work/MODULE/'src/Example.kt').read_bytes()==FILES[MODULE+'/src/Example.kt']
    with pytest.raises(ValueError):store.materialize(PKG,value['id'],work)
    saved=store.get(PKG,value['id'])
    assert saved['id']==value['id'] and 'PRIVATE_' not in json.dumps(saved)


def test_large_repository_download_does_not_widen_user_uploads_or_publish_builds(tmp_path,api):
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w',zipfile.ZIP_STORED) as stream:
        for name,data in FILES.items():stream.writestr('repo-commit/'+name,data)
        stream.writestr('repo-commit/src/en/other/build/outputs/installed.apk',b'x'*(33*1024*1024))
    payload=output.getvalue();store=api.RawStore(tmp_path/'raw')
    with pytest.raises(ValueError):store.put_zip(payload,metadata())
    downloaded=tmp_path/'public-repository.zip';downloaded.write_bytes(payload)
    saved=store.put_repository_file(downloaded,metadata())
    assert set(saved['files'])==set(FILES)
    assert len(store.export_zip(PKG,saved['id']))<1024*1024
