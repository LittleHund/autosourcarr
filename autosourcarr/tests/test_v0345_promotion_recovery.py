"""Actual private files/journals; no engine/container/native execution."""
import hashlib
import importlib
import json

import pytest

from test_v0345_repair_history import PKG,ref


@pytest.fixture
def context(tmp_path,monkeypatch):
    module=importlib.import_module('app.source_maintenance')
    workflow=importlib.import_module('app.repair_workflow').repair_workflow
    history=importlib.import_module('app.repair_history').RepairHistory(tmp_path/'history')
    connection=importlib.import_module('app.repair_connection')
    from bundled.source_tools.fs import atomic_write
    monkeypatch.setattr(connection,'restore_connection',lambda:None)
    monkeypatch.setattr(module.settings,'data_dir',tmp_path)
    monkeypatch.setattr(module,'atomic',atomic_write)
    native=tmp_path/'installed.jar';monkeypatch.setattr(module,'installed_path',lambda _:native)
    monkeypatch.setattr(workflow,'history',history)
    return module,history,native


def prepare(context):
    module,history,native=context;old=b'old binary'*20;new=b'new binary'*20
    old_ref=ref('1','a');old_ref['binary_sha256']=module.sha(old)
    new_ref=ref('2','b');new_ref['binary_sha256']=module.sha(new)
    state=history.admit(PKG,'a'*32,old_ref)
    plan=history.plan_install(PKG,'a'*32,new_ref,expected_revision=state['revision'])
    for value in (old,new):module.atomic(module.storage()/'backups'/module.sha(value)/'installed.jar',value)
    native.write_bytes(new)
    return old,new,plan


@pytest.mark.parametrize('committed',[False,True])
def test_interrupted_install_restores_exact_bytes_and_private_history_together(context,committed):
    module,history,native=context;old,new,plan=prepare(context)
    if committed:history.commit(plan,observed_sha256=module.sha(new))
    module.atomic(module.storage()/'promotion.json',json.dumps({'state':'installing','id':'a'*32,'extension':{},
        'baseline_sha256':module.sha(old),'candidate_sha256':module.sha(new),'history_plan':plan}).encode())
    maintenance=module.SourceMaintenance();maintenance.recover()
    state=history.snapshot(PKG)
    assert native.read_bytes()==old and state['current']['raw_id']=='a'*64
    assert state['pending'] is None and state['working'] is None
    assert maintenance.operation['state']=='rolled-back'


@pytest.mark.parametrize('committed',[False,True])
def test_interrupted_explicit_revert_finishes_the_same_target_and_undoes_fixed(context,committed):
    module,history,native=context;old,new,plan=prepare(context)
    state=history.commit(plan,observed_sha256=module.sha(new))
    state=history.confirm(PKG,'a'*32,expected_revision=state['revision'],observed_sha256=module.sha(new))
    revert=history.plan_revert(PKG,'a'*32,expected_revision=state['revision'],observed_sha256=module.sha(new))
    if committed:
        native.write_bytes(old);history.commit(revert,observed_sha256=module.sha(old))
    module.atomic(module.storage()/'promotion.json',json.dumps({'id':'a'*32,'state':'reverting','extension':{},
        'from_sha256':module.sha(new),'history_plan':revert}).encode())
    maintenance=module.SourceMaintenance();maintenance.recover()
    assert native.read_bytes()==old and history.snapshot(PKG)['working'] is None
    assert history.snapshot(PKG)['current']['raw_id']=='a'*64
    assert maintenance.operation['state']=='reverted'


def test_recovery_never_overwrites_an_independently_changed_source(context):
    module,history,native=context;old,new,plan=prepare(context)
    module.atomic(module.storage()/'promotion.json',json.dumps({'id':'a'*32,'state':'installing','extension':{},
        'baseline_sha256':module.sha(old),'candidate_sha256':module.sha(new),'history_plan':plan}).encode())
    native.write_bytes(b'Independent update')
    with pytest.raises(ValueError):module.SourceMaintenance().recover()
    assert native.read_bytes()==b'Independent update'
