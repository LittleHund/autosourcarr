"""Real raw workflow orchestration, isolated/native processes replaced by seams."""
import importlib
import io
from pathlib import Path
from types import SimpleNamespace
import zipfile

import pytest

from test_v0345_raw_archive import PKG,MODULE,FILES,archive,metadata
from test_v0339_baseline_manifest import XML,CLASS,CODE,package


@pytest.fixture
def api(monkeypatch):
    root=Path(__file__).resolve().parents[2];monkeypatch.syspath_prepend(str(root));monkeypatch.syspath_prepend(str(root/'repair-worker'))
    return importlib.import_module('raw_workflow')


def setup(tmp_path,api):
    baseline=package([('AndroidManifest.xml',XML),(CLASS,CODE)])
    code=archive(FILES);(tmp_path/'baseline.jar').write_bytes(baseline);(tmp_path/'raw.zip').write_bytes(code)
    job={'id':'a'*32,'pkg_name':PKG,'module':MODULE,'installed_version':'1.4.23','installed_library':'1.4','raw_first':True,
        'installed_sources':[{'id':'123','name':'Example','lang':'en','baseUrl':'https://old.example/'}],
        'raw_meta':metadata(),'raw_sha256':api.digest(code),'baseline_sha256':api.digest(baseline),'issue':'Chapter and catalog requests fail.'}
    return baseline,job


def test_acquired_code_precedes_website_checks_and_ready_preserves_original_and_candidate_code(tmp_path,api):
    baseline,job=setup(tmp_path,api);events=[]
    def inspect(job,data,*,expected,functional=True,**options):
        events.append('functional' if functional else 'cold');assert (tmp_path/'original'/MODULE/'src/Example.kt').is_file()
        return expected,[{'stage':'catalog','state':'passed'}] if functional else []
    def save(job,state,message,**fields):events.append(state);job.update(state=state,message=message,**fields)
    def websites(job,sources):events.append('website');return []
    runner=SimpleNamespace(path=lambda _:tmp_path,require_isolation=lambda:events.append('isolated'),
        inspect_artifact=inspect,save=save,check_websites=websites,build=lambda *a:baseline,
        check_cancelled=lambda _:None,edit=lambda *a:pytest.fail('Working original code does not require an AI edit'))
    api.run_raw(runner,job)
    assert job['state']=='ready' and events.index('raws-acquired')<events.index('cold')<events.index('website')
    assert (tmp_path/'baseline.jar').read_bytes()==baseline
    assert job['candidate_raw_sha256']==api.digest((tmp_path/'candidate-raw.zip').read_bytes())
    assert job['candidate_raw_meta']['kind']=='repaired' and job['candidate_raw_meta']['parent']==job['raw_id']
    assert job['candidate_raw_meta']['ecosystem']=='tachiyomi'


def test_explicit_raw_only_reconstruction_retains_code_even_without_a_loading_contract(tmp_path,api,monkeypatch):
    baseline,job=setup(tmp_path,api);job.update(reconstruct=True,raw_only=True,installed_sources=[])
    def reconstruct(job,data,original):
        assert data==baseline;src=original/'src';src.mkdir(parents=True);(src/'Actual.java').write_text('class Actual {}')
    def save(job,state,message,**fields):job.update(state=state,message=message,**fields)
    runner=SimpleNamespace(path=lambda _:tmp_path,require_isolation=lambda:None,reconstruct_code=reconstruct,save=save)
    monkeypatch.setattr(api,'cold_baseline',lambda *args:pytest.fail('Raw archival does not assert source loading/working proof'))
    api.run_raw(runner,job)
    assert job['state']=='raws-retained' and job['raw_meta']['kind']=='reconstructed'
    assert 'extension_lib' not in job['raw_meta'] and 'baseline_import_proof' not in job
    with zipfile.ZipFile(tmp_path/'raw.zip') as code:assert code.read('src/Actual.java')==b'class Actual {}'


def test_only_a_checked_literal_baseurl_changes_in_build_metadata(tmp_path,api):
    root=tmp_path/MODULE;root.mkdir(parents=True)
    source='plugins { id("original.plugin") }\nbaseUrl = "https://old.example"\nversionCode = 23\ndependencies { implementation("original:lib:1") }\n'
    path=root/'build.gradle.kts';path.write_text(source)
    target={'previous_url':'https://old.example/','new_url':'https://new.example/','basis':'original-maintainer-code'}
    assert api.patch_build_addresses(tmp_path,MODULE,[target])==[MODULE+'/build.gradle.kts']
    assert path.read_text()==source.replace('https://old.example','https://new.example/')
    path.write_text(source)
    with pytest.raises(Exception):api.patch_build_addresses(tmp_path,MODULE,[{**target,'new_url':'https://new.example/${danger}'}])
    assert path.read_text()==source


@pytest.mark.parametrize('candidate_working',[False,True])
def test_old_transport_uncertainty_still_checks_acquired_code_but_never_triggers_ai_by_itself(tmp_path,api,candidate_working):
    baseline,job=setup(tmp_path,api);calls=[]
    def inspect(job,data,*,expected,functional=True,**options):
        if not functional:return expected,[]
        calls.append('functional')
        if len(calls)==1 or not candidate_working:raise api.SourceBlocked('Source transport failed; validation is inconclusive and no code repair will be attempted')
        return expected,[{'stage':'catalog','state':'passed'}]
    def save(job,state,message,**fields):job.update(state=state,message=message,**fields)
    def build(*args):calls.append('build');return baseline
    runner=SimpleNamespace(path=lambda _:tmp_path,require_isolation=lambda:None,inspect_artifact=inspect,save=save,
        check_websites=lambda *args:[],build=build,check_cancelled=lambda _:None,
        edit=lambda *args:pytest.fail('Transport uncertainty alone cannot start an AI edit'))
    if candidate_working:
        api.run_raw(runner,job);assert job['state']=='ready'
    else:
        with pytest.raises(api.SourceBlocked):api.run_raw(runner,job)
    assert 'build' in calls and 'inconclusive' in job['baseline_failure']
