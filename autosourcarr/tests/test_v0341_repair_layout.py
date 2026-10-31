"""Repair workspace ownership, account privacy and preserved UI behavior."""
from html.parser import HTMLParser
from pathlib import Path
from test_v0315_repair_ui import DOM, SOURCE
from test_v086_ui import node


def test_only_ai_controls_are_in_sources_and_all_repair_work_is_in_tasks_repairs():
    class Tree(HTMLParser):
        def __init__(self): super().__init__(); self.stack=[]; self.ids={}
        def handle_starttag(self,tag,attrs):
            values=dict(attrs); identity=values.get('id')
            if identity:
                assert identity not in self.ids, identity
                self.ids[identity]=[value for _,value in self.stack]
            if tag not in {'input','img','meta','link','br','hr','source'}: self.stack.append((tag,identity))
        def handle_endtag(self,tag):
            for index in range(len(self.stack)-1,-1,-1):
                if self.stack[index][0]==tag:
                    del self.stack[index:]; break
        def handle_startendtag(self,tag,attrs): self.handle_starttag(tag,attrs)
    parser=Tree(); parser.feed((Path(__file__).resolve().parents[1]/'app/static/index.html').read_text())
    for identity in ('sourceRepairForm','sourceRepairOriginalApk','sourceRepairWatch','rollbackSourceRepair','repairPrepare','repairIsolation',
                     'sourceRepairDetails','repairTasksRunningList','repairTasksFailedList'):
        assert 'tasksRepairsPanel' in parser.ids[identity], identity
        assert 'sourceMaintenancePanel' not in parser.ids[identity], identity
    for identity in ('repairLogin','repairCheckLogin','repairLogout','configureRepairAI','repairAccountDiagnostics','repairDeviceCode'):
        assert 'sourceMaintenancePanel' in parser.ids[identity], identity
        assert 'tasksRepairsPanel' not in parser.ids[identity], identity
    assert 'tasksGeneralPanel' in parser.ids['tasksRunningList']


def test_ai_view_does_not_load_packages_and_moving_to_repairs_clears_device_codes():
    node(DOM+r'''
let surface='none';
ids.sources.classList={contains:()=>active&&surface==='ai'};
ids.tasks.classList={contains:()=>active&&surface==='repairs'};
ids.tasksRepairsPanel.classList={contains:()=>active&&surface==='repairs'};
'''+SOURCE+r'''
(async()=>{
 active=true;surface='ai';control={busy:true,action:'login',message:'Finish sign-in',verification_url:'https://auth.openai.com/codex/device',device_code:'ABCD-12345',expires_at:Date.now()/1000+900};
 await loadSourceRepairs();assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345');
 assert(!requests.some(item=>item.path.endsWith('/installed')),'AI setup must not load source packages');
 surface='repairs';observer();await new Promise(resolve=>setImmediate(resolve));await loadSourceRepairs();
 assert(ids.repairDeviceLogin.hidden&&ids.repairDeviceCode.textContent==='','Device codes must clear outside Repair AI');
 assert(requests.some(item=>item.path.endsWith('/installed')),'Repair workspace owns installed-package choices');
})().catch(error=>{console.error(error);process.exitCode=1});
''')
