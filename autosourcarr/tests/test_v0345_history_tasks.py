"""Current version controls survive bounded worker history and clear-history."""
from types import SimpleNamespace
import importlib

from app.support_logs import SupportPrivacy

PKG='eu.kanade.tachiyomi.extension.en.example'


def test_current_confirmed_version_remains_actionable_without_a_worker_summary(monkeypatch):
    module=importlib.import_module('app.repair_tasks');workflow=importlib.import_module('app.repair_workflow').repair_workflow
    raw=importlib.import_module('app.source_raws').source_raws
    summary={PKG:{'revision':7,'pending':None,'working_at':2000,'can_revert':True,'jobs':{'a'*32:'confirmed'},
        'current':{'job_id':'a'*32,'binary_sha256':'b'*64,'raw_id':'c'*64,'status':'confirmed','installed_at':1000,'source_ids':['123']}}}
    monkeypatch.setattr(workflow,'history',SimpleNamespace(summaries=lambda:summary))
    monkeypatch.setattr(workflow,'admissions',{})
    monkeypatch.setattr(raw,'catalog',None)
    tasks=module.RepairTasks(SupportPrivacy());tasks.observe({'jobs':[]})
    item=tasks.merge({'items':[]})['items'][0]
    assert item['repair_is_current'] and item['repair_state']=='confirmed' and item['repair_can_revert']
    assert item['repair_working_at']==2000 and item['repair_history_revision']==7
    assert tasks.clear_history()['kept_for_review']==1
    assert len(tasks.merge({'items':[]})['items'])==1
    summary[PKG]['pending']='d'*32;summary[PKG]['can_revert']=False
    item=tasks.merge({'items':[]})['items'][0]
    assert not item['repair_can_feedback'] and not item['repair_can_revert']
