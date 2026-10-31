"""Actual settings imports use disposable env/config; no host paths are read."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

import pytest
from app.config import Settings
from app.setup_store import FILES,JOURNAL,STATE

ROOT=Path(__file__).resolve().parents[2]
READ_SETTINGS='''import json
from app.config import settings
print(json.dumps({'root':str(settings.settings_config_dir),'max':settings.max_source_concurrency,'auto':settings.source_auto_challenges}))
'''


def save_config(root,values,managed=None):
    root.mkdir(parents=True,exist_ok=True)
    (root/'autosourcarr.env').write_text(''.join(key+'='+str(value)+'\n' for key,value in values.items()))
    (root/STATE).write_text(json.dumps({'managed_keys':list(values) if managed is None else managed}))


def child(tmp_path,root=None,*,values=None,code=READ_SETTINGS):
    # Historical tests can change process env; none of those values belong to this fixture.
    keys={key.upper() for key in Settings.model_fields}
    env={key:value for key,value in os.environ.items() if key.upper() not in keys}
    data=tmp_path/'data';data.mkdir(exist_ok=True)
    env.update(PYTHONPATH=str(ROOT/'autosourcarr'),DATA_DIR=str(data),DATABASE_DIR=str(data),
               SOURCE_ENGINE_MODE='external',APP_SERVICES_MODE='external',TZ='UTC')
    if root is not None: env['SETTINGS_CONFIG_DIR']=str(root)
    env.update(values or {})
    return subprocess.run([sys.executable,'-c',code],cwd=tmp_path,env=env,capture_output=True,text=True,timeout=5)


@pytest.mark.parametrize('key,saved,stale,expected',[
    ('MAX_SOURCE_CONCURRENCY','6','stale-invalid',{'max':6}),
    ('SOURCE_AUTO_CHALLENGES','false','stale-invalid',{'auto':False}),
])
def test_managed_saved_value_overrides_invalid_stale_injected_value(tmp_path,key,saved,stale,expected):
    root=tmp_path/'config';save_config(root,{key:saved})
    result=child(tmp_path,root,values={key:stale})
    assert result.returncode==0,result.stderr
    parsed=json.loads(result.stdout)
    assert parsed['root']==str(root)
    assert all(parsed[key]==value for key,value in expected.items())


def test_environment_config_root_precedes_dotenv_config_root(tmp_path):
    injected=tmp_path/'injected';dotenv=tmp_path/'dotenv'
    save_config(injected,{'MAX_SOURCE_CONCURRENCY':'8'})
    save_config(dotenv,{'MAX_SOURCE_CONCURRENCY':'3'})
    (tmp_path/'.env').write_text('SETTINGS_CONFIG_DIR='+str(dotenv)+'\nMAX_SOURCE_CONCURRENCY=dotenv-invalid\n')
    result=child(tmp_path,injected,values={'MAX_SOURCE_CONCURRENCY':'injected-invalid'})
    assert result.returncode==0,result.stderr
    assert json.loads(result.stdout)=={'root':str(injected),'max':8,'auto':True}


def test_dotenv_root_loads_managed_override_before_full_dotenv_validation(tmp_path):
    root=tmp_path/'dotenv';save_config(root,{'MAX_SOURCE_CONCURRENCY':'7'})
    (tmp_path/'.env').write_text('SETTINGS_CONFIG_DIR='+str(root)+'\nMAX_SOURCE_CONCURRENCY=dotenv-invalid\n')
    result=child(tmp_path)
    assert result.returncode==0,result.stderr
    assert json.loads(result.stdout)['max']==7
    assert json.loads(result.stdout)['root']==str(root)


def test_invalid_final_managed_value_is_still_rejected(tmp_path):
    root=tmp_path/'config';save_config(root,{'MAX_SOURCE_CONCURRENCY':'saved-invalid'})
    result=child(tmp_path,root,values={'MAX_SOURCE_CONCURRENCY':'6'})
    assert result.returncode!=0
    assert 'max_source_concurrency' in result.stderr and 'int_parsing' in result.stderr
    assert 'settings = Settings(**startup_overrides' in result.stderr


def test_unmanaged_invalid_literal_is_still_rejected_by_final_settings(tmp_path):
    root=tmp_path/'config';save_config(root,{'MAX_SOURCE_CONCURRENCY':'6'})
    result=child(tmp_path,root,values={'SOURCE_ENGINE_MODE':'invalid-mode'})
    assert result.returncode!=0
    assert 'source_engine_mode' in result.stderr and 'literal_error' in result.stderr
    assert 'settings = Settings(**startup_overrides' in result.stderr


def test_unmanaged_saved_value_cannot_silently_override_invalid_process_value(tmp_path):
    root=tmp_path/'config';save_config(root,{'MAX_SOURCE_CONCURRENCY':'6'},managed=[])
    result=child(tmp_path,root,values={'MAX_SOURCE_CONCURRENCY':'stale-invalid'})
    assert result.returncode!=0
    assert 'max_source_concurrency' in result.stderr and 'int_parsing' in result.stderr


def test_partial_setup_recovery_precedes_final_validation_and_survives_next_boot(tmp_path):
    root=tmp_path/'config';save_config(root,{'MAX_SOURCE_CONCURRENCY':'99'},managed=[])
    previous={'autosourcarr.env':b'MAX_SOURCE_CONCURRENCY=4\n','source-host.env':None,STATE:None}
    journal={'transaction':str(uuid.uuid4()),
             'before':{key:base64.b64encode(value).decode() if value is not None else None for key,value in previous.items()},
             'effective_before':{'MAX_SOURCE_CONCURRENCY':6}}
    assert set(journal['before'])==set(FILES)
    (root/JOURNAL).write_text(json.dumps(journal))
    for _ in range(2):
        result=child(tmp_path,root,values={'MAX_SOURCE_CONCURRENCY':'stale-invalid'})
        assert result.returncode==0,result.stderr
        assert json.loads(result.stdout)['max']==6
    assert not (root/JOURNAL).exists()
    assert json.loads((root/STATE).read_text())['managed_keys']==['MAX_SOURCE_CONCURRENCY']


def test_bootstrap_default_path_remains_config_without_reading_it(tmp_path):
    root=tmp_path/'safe-import';save_config(root,{})
    code='''import os
from app.config import _SettingsRoot
os.environ.pop('SETTINGS_CONFIG_DIR',None)
print(_SettingsRoot(_env_file=None).settings_config_dir)
'''
    result=child(tmp_path,root,code=code)
    assert result.returncode==0,result.stderr
    assert result.stdout.strip()=='/config'
