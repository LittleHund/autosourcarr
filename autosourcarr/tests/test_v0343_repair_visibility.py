"""Exact APK proof remains closed while failures carry useful bounded evidence."""
import ast
import importlib
import io
import json
from pathlib import Path
import zipfile

import pytest

ROOT=Path(__file__).resolve().parents[2]


def package(entries):
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w') as archive:
        for name,value in entries: archive.writestr(name,value)
    return stream.getvalue()


def test_conversion_mismatch_reports_counts_without_relaxing_exact_equality(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT/'repair-worker'))
    module=importlib.import_module('baseline_manifest')
    installed=package([('A.class',b'old'),('B.class',b'removed'),('asset.txt',b'same')])
    converted=package([('A.class',b'new'),('C.class',b'added'),('asset.txt',b'same')])
    with pytest.raises(module.ConversionMismatch) as caught:
        module.restore_manifest(installed,b'unused APK because proof failed',converted)
    comparison=caught.value.comparison
    assert comparison['identical_entries']==1
    assert comparison['changed_entries']==comparison['missing_entries']==comparison['added_entries']==1
    assert comparison['changed_class_entries']==1
    assert comparison['changed_resource_entries']==0
    assert comparison['installed_jar_sha256'] != comparison['converted_jar_sha256']
    assert 'A.class' not in json.dumps(comparison)


def test_comparison_export_allowlist_rejects_arbitrary_fields_and_invalid_counts():
    from app.repair_evidence import safe_job
    from app.support_logs import SupportPrivacy
    result=safe_job({'id':'a'*32,'state':'blocked','conversion_comparison':{
        'changed_entries':2,'missing_entries':True,'added_entries':999999,
        'installed_jar_sha256':'b'*64,'converted_jar_sha256':'not a hash',
        'classes':['PRIVATE_CLASS_NAME'],'prompt':'PRIVATE_PROMPT'}},SupportPrivacy())
    assert result['conversion_comparison']=={'changed_entries':2,'installed_jar_sha256':'b'*64}
    assert 'PRIVATE' not in json.dumps(result)


def test_diagnostic_task_snapshot_contains_observed_worker_jobs_without_io():
    from app.repair_tasks import RepairTasks
    from app.support_logs import SupportPrivacy
    tasks=RepairTasks(SupportPrivacy())
    tasks.observe({'jobs':[{'id':'a'*32,'state':'blocked','message':'APK mismatch','created_at':1,'updated_at':2}]})
    old=[{'id':'local-'+str(n),'state':'completed','updated_at':'1970-01-01T00:00:00+00:00'} for n in range(150)]
    snapshot=tasks.diagnostic_snapshot({'items':old})
    assert snapshot['failed']==1 and snapshot['items'][0]['repair_state']=='blocked'
    assert any(row.get('repair_state')=='blocked' for row in snapshot['items'][:100]), 'Local history must not displace the newest worker failure'
    tree=ast.parse((ROOT/'autosourcarr/app/main.py').read_text(encoding='utf-8'))
    route=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='admin_diagnostics')
    assignments=[n for n in route.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='tasks' for t in n.targets)]
    call=assignments[0].value
    assert isinstance(call,ast.Call) and isinstance(call.func,ast.Attribute) and call.func.attr=='diagnostic_snapshot'


def test_private_worker_public_job_retains_only_bounded_comparison_field(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT/'repair-worker'))
    contracts=importlib.import_module('contracts')
    assert contracts.public_job({'id':'a'*32,'conversion_comparison':{'changed_entries':1},'private':'SECRET'}) == {
        'id':'a'*32,'conversion_comparison':{'changed_entries':1}}
