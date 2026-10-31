"""Trusted build recipe with a fake compiler; output is not native compilation proof."""
import importlib
import io
import json
from pathlib import Path
import zipfile
import pytest

from test_v0345_reconstruction import jar,PKG


@pytest.fixture
def api(monkeypatch):
    root=Path(__file__).resolve().parents[2];monkeypatch.syspath_prepend(str(root));monkeypatch.syspath_prepend(str(root/'repair-worker'))
    return importlib.import_module('jvm_compile')


def test_only_approved_java_overlay_changes_installed_classes_and_resources_are_exact(tmp_path,api):
    name='src/'+PKG.replace('.','/')+'/ActualFactory.java'
    path=tmp_path/name;path.parent.mkdir(parents=True);path.write_text('public class ActualFactory {}')
    output=io.BytesIO(jar())
    with zipfile.ZipFile(output,'a') as signed:
        signed.writestr('META-INF/OLD.SF',b'old signature');signed.writestr('META-INF/OLD.RSA',b'old certificate')
    baseline=output.getvalue();(tmp_path/'.repair-input.jar').write_bytes(baseline)
    (tmp_path/'.repair-build.json').write_text(json.dumps({'changed_files':[name]}))
    calls=[]
    def compile(command,**options):
        calls.append((command,options));out=tmp_path/'build/classes'/PKG.replace('.','/');out.mkdir(parents=True)
        (out/'ActualFactory.class').write_bytes(b'synthetic compiler overlay')
    api.compile_overlay(tmp_path,run=compile)
    command=calls[0][0]
    assert '-proc:none' in command and '-implicit:none' in command and command[-1]==str(path)
    assert calls[0][1]=={'check':True,'timeout':600}
    with zipfile.ZipFile(tmp_path/'build/outputs/jar/reconstructed.jar') as output:
        assert output.read('assets/retained.txt')==b'original resource'
        assert output.read(PKG.replace('.','/')+'/ActualFactory.class')==b'synthetic compiler overlay'
        assert 'META-INF/OLD.SF' not in output.namelist() and 'META-INF/OLD.RSA' not in output.namelist()
    assert (tmp_path/'.repair-input.jar').read_bytes()==baseline


@pytest.mark.parametrize('names',[[],['../bad.java'],['src/Module.kt'],['build.gradle.kts']])
def test_no_unbounded_or_build_file_compilation(tmp_path,api,names):
    (tmp_path/'.repair-build.json').write_text(json.dumps({'changed_files':names}))
    with pytest.raises(ValueError):api.compile_overlay(tmp_path,run=lambda *a,**k:pytest.fail('Invalid inputs cannot launch a compiler'))
