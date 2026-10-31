from pathlib import Path
import json
import shutil
import subprocess
import pytest
from test_reimagined_preview import sample, request


def test_sharing_file_write_and_uncertain_result_ownership():
    node=shutil.which('node')
    if not node:
        pytest.skip('Node.js unavailable')
    result=subprocess.run([node,str(Path(__file__).with_name('ui_v0402_sharing.js'))],capture_output=True,text=True,timeout=15)
    assert result.returncode==0,result.stdout+result.stderr


def test_sample_sharing_and_gallery_controls_are_synthetic_read_only(sample):
    base,data=sample
    for path in ('/api/admin/catalog-sharing/status','/api/admin/catalog-sharing/records','/api/admin/catalog-sharing/decisions','/api/admin/gallery-policies/100'):
        status,_,body=request(base,path)
        assert status==200 and isinstance(json.loads(body),dict)
    before=data.matrix()
    for method,path in [('POST','/api/admin/catalog-sharing/apply'),('POST','/api/admin/catalog-sharing/export'),('PUT','/api/admin/gallery-policies/100')]:
        status,_,_=request(base,path,{},method)
        assert status==409
    assert before==data.matrix()
