"""Per-source working/revert state using actual private journal files."""
import copy
import importlib
from pathlib import Path

import pytest

PKG='eu.kanade.tachiyomi.extension.en.example'
JOBS=['a'*32,'b'*32,'c'*32]


@pytest.fixture
def api():return importlib.import_module('app.repair_history')


def ref(binary,raw):
    return {'binary_sha256':binary*64,'raw_id':raw*64,'version':'1.4.23',
        'entrypoint':PKG+'.Example','extension_lib':'1.4','source_ids':['-9223372036854775808','1'],
        'identity_sha256':'9'*64}


def install(history,job,old,new):
    started=history.admit(PKG,job,old)
    plan=history.plan_install(PKG,job,new,expected_revision=started['revision'])
    return history.commit(plan,observed_sha256=new['binary_sha256'])


def test_fixed_promotes_only_current_code_and_revert_undoes_the_promotion(tmp_path,api):
    history=api.RepairHistory(tmp_path/'instance',clock=lambda:1000.)
    state=install(history,JOBS[0],ref('1','a'),ref('2','b'))
    assert state['working'] is None
    assert state['current']['status']=='unconfirmed'
    state=history.confirm(PKG,JOBS[0],expected_revision=state['revision'],observed_sha256='2'*64)
    assert state['working']['binary_sha256']=='2'*64 and state['working']['raw_id']=='b'*64
    assert state['working']['confirmed_at']==1000.
    plan=history.plan_revert(PKG,JOBS[0],expected_revision=state['revision'],observed_sha256='2'*64)
    assert plan['target']['binary_sha256']=='1'*64 and plan['target']['raw_id']=='a'*64
    state=history.commit(plan,observed_sha256='1'*64)
    assert state['current']['binary_sha256']=='1'*64 and state['working'] is None
    assert state['jobs'][JOBS[0]]['state']=='reverted'


def test_new_fix_uses_confirmed_working_code_as_the_new_revert_target(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    state=install(history,JOBS[0],ref('1','a'),ref('2','b'))
    state=history.confirm(PKG,JOBS[0],expected_revision=state['revision'],observed_sha256='2'*64)
    started=history.admit(PKG,JOBS[1],ref('2','b'))
    with pytest.raises(ValueError):history.plan_revert(PKG,JOBS[0],expected_revision=started['revision'],observed_sha256='2'*64)
    plan=history.plan_install(PKG,JOBS[1],ref('3','c'),expected_revision=started['revision'])
    state=history.commit(plan,observed_sha256='3'*64)
    plan=history.plan_revert(PKG,JOBS[1],expected_revision=state['revision'],observed_sha256='3'*64)
    state=history.commit(plan,observed_sha256='2'*64)
    assert state['working']['binary_sha256']=='2'*64 and state['current']['raw_id']=='b'*64


def test_does_not_work_retry_keeps_the_anchor_instead_of_a_failed_candidate(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    state=install(history,JOBS[0],ref('1','a'),ref('2','b'))
    state=history.report_failed(PKG,JOBS[0],expected_revision=state['revision'],observed_sha256='2'*64,feedback='Search still fails')
    assert state['current']['status']=='reported-not-working' and state['working'] is None
    state=history.admit(PKG,JOBS[1],ref('2','b'))
    plan=history.plan_install(PKG,JOBS[1],ref('3','c'),expected_revision=state['revision'])
    state=history.commit(plan,observed_sha256='3'*64)
    plan=history.plan_revert(PKG,JOBS[1],expected_revision=state['revision'],observed_sha256='3'*64)
    assert plan['target']['binary_sha256']=='1'*64


def test_stale_revision_wrong_binary_or_job_cannot_mark_fixed(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    state=install(history,JOBS[0],ref('1','a'),ref('2','b'))
    for job,revision,binary in [(JOBS[1],state['revision'],'2'*64),(JOBS[0],0,'2'*64),(JOBS[0],state['revision'],'3'*64)]:
        with pytest.raises(ValueError):history.confirm(PKG,job,expected_revision=revision,observed_sha256=binary)
    assert history.snapshot(PKG)==state


def test_promotion_plans_are_bound_and_recovery_restores_code_and_marker(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    before=history.admit(PKG,JOBS[0],ref('1','a'))
    plan=history.plan_install(PKG,JOBS[0],ref('2','b'),expected_revision=before['revision'])
    tampered=copy.deepcopy(plan);tampered['target']['raw_id']='c'*64
    with pytest.raises(ValueError):history.commit(tampered,observed_sha256='2'*64)
    installed=history.commit(plan,observed_sha256='2'*64)
    reopened=api.RepairHistory(tmp_path)
    assert reopened.snapshot(PKG)==installed
    restored=reopened.restore_before(plan,observed_sha256='1'*64)
    assert restored['current']['binary_sha256']=='1'*64 and restored['working'] is None
    assert restored['revision']>installed['revision']
    assert reopened.restore_before(plan,observed_sha256='1'*64)==restored,'Recovery must be idempotent'


def test_first_recovered_install_can_revert_to_not_installed(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    state=install(history,JOBS[0],None,ref('2','b'))
    plan=history.plan_revert(PKG,JOBS[0],expected_revision=state['revision'],observed_sha256='2'*64)
    assert plan['target'] is None
    state=history.commit(plan,observed_sha256=None)
    assert state['current'] is None and state['working'] is None


def test_same_request_is_idempotent_and_other_attempt_waits_for_owned_work(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    started=history.admit(PKG,JOBS[0],ref('1','a'))
    assert history.admit(PKG,JOBS[0],ref('1','a'))==started
    with pytest.raises(ValueError):history.admit(PKG,JOBS[1],ref('1','a'))
    state=history.finish(PKG,JOBS[0],'cancelled')
    assert state['pending'] is None
    assert history.admit(PKG,JOBS[1],ref('1','a'))['pending']==JOBS[1]


def test_private_instance_state_is_independent_and_corruption_is_not_reset(tmp_path,api):
    first=api.RepairHistory(tmp_path/'first');second=api.RepairHistory(tmp_path/'second')
    state=install(first,JOBS[0],ref('1','a'),ref('2','b'))
    assert second.snapshot(PKG)['current'] is None
    files=list((tmp_path/'first').glob('*.json'));assert len(files)==1
    files[0].write_text('{CORRUPTED')
    with pytest.raises(ValueError):api.RepairHistory(tmp_path/'first').snapshot(PKG)
    assert files[0].read_text()=='{CORRUPTED'


def test_reference_cannot_override_working_status_or_use_arbitrary_paths(tmp_path,api):
    history=api.RepairHistory(tmp_path)
    with pytest.raises(ValueError):history.admit(PKG,JOBS[0],{**ref('1','a'),'confirmed_at':1})
    with pytest.raises(ValueError):history.admit(PKG,JOBS[0],{**ref('1','a'),'path':'/config/secret'})


@pytest.mark.parametrize('changes',[{'extension_lib':'1.6'},{'source_ids':['2']},{'entrypoint':PKG+'.Unrelated'},{'identity_sha256':'8'*64}])
def test_changed_loading_or_source_identity_is_not_a_compatible_repair(tmp_path,api,changes):
    history=api.RepairHistory(tmp_path)
    state=history.admit(PKG,JOBS[0],ref('1','a'))
    with pytest.raises(ValueError):history.plan_install(PKG,JOBS[0],{**ref('2','b'),**changes},expected_revision=state['revision'])
    assert history.snapshot(PKG)==state
