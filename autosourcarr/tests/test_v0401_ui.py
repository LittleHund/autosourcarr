from pathlib import Path
import shutil
import subprocess
import pytest
from test_reimagined_preview import sample,request


@pytest.mark.parametrize('script',['ui_v0401_tabs.js','ui_v0401_identity_races.js'])
def test_tabs_and_identity_review_request_draft_ownership(script):
    node=shutil.which('node')
    if not node:pytest.skip('Node.js unavailable')
    result=subprocess.run([node,str(Path(__file__).with_name(script))],capture_output=True,text=True,timeout=15)
    assert result.returncode==0,result.stdout+result.stderr


def test_preview_repair_states_and_identity_review_remain_synthetic(sample):
    import json
    base,data=sample
    _,_,body=request(base,'/api/admin/tasks');rows=json.loads(body)['items']
    repairs=[r for r in rows if r['kind'] in ('source-repair','source-raw-input')]
    assert {r['repair_state'] for r in repairs}=={'awaiting-raws','checking-backup','ready','blocked'}
    _,_,body=request(base,'/api/admin/identity-review?search=Vagabond');listing=json.loads(body)
    assert listing['total']==1
    eid=listing['items'][0]['id'];_,_,body=request(base,'/api/admin/identity-review/'+str(eid))
    state=json.loads(body);assert state['entry_id']==eid and state['references']
    status,_,_=request(base,'/api/admin/identity-review/'+str(eid),{'decision':'excluded'},'POST')
    assert status==409
