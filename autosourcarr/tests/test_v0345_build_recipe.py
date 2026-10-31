"""Original Android output is converted in the private runtime, not discarded."""
import ast
import importlib
from pathlib import Path
import shutil
from types import SimpleNamespace,MethodType
import pytest

from test_v0339_baseline_manifest import package,XML,CLASS,CODE


@pytest.fixture
def build(monkeypatch):
    root=Path(__file__).resolve().parents[2];monkeypatch.syspath_prepend(str(root/'repair-worker'))
    contracts=importlib.import_module('contracts');validate=importlib.import_module('validate')
    tree=ast.parse((root/'repair-worker/runner.py').read_text())
    cls=next(node for node in tree.body if isinstance(node,ast.ClassDef) and node.name=='Runner')
    method=next(node for node in cls.body if isinstance(node,ast.FunctionDef) and node.name=='build')
    scope={'shutil':shutil,'package_bytes':contracts.package_bytes,'SourceBlocked':validate.SourceBlocked}
    exec(compile(ast.Module(body=[method],type_ignores=[]),'<actual-build-recipe>','exec'),scope)
    return scope['build']


@pytest.mark.parametrize('module',['.','src/en/example'])
def test_original_apk_recipe_converts_one_release_output_and_keeps_the_original_tree(tmp_path,build,module):
    original=tmp_path/'original';original.mkdir();(original/'kept.txt').write_text('original code')
    calls=[];apk=package([('AndroidManifest.xml',XML),('classes.dex',b'original ecosystem APK output')])
    native=package([('AndroidManifest.xml',XML),(CLASS,CODE)])
    def compile(job,path):
        out=path/module/'build/outputs/apk/release';out.mkdir(parents=True);(out/'extension-unsigned.apk').write_bytes(apk)
    runner=SimpleNamespace(path=lambda _:tmp_path,compile_tree=compile,
        native_from_apk=lambda job,data:(calls.append(data) or native))
    result=build(runner,{'id':'a'*32,'module':module,'raw_first':True},original,'build-test')
    assert result==native and calls==[apk] and (original/'kept.txt').read_text()=='original code'


def test_ambiguous_release_outputs_are_not_arbitrarily_selected(tmp_path,build):
    original=tmp_path/'original';original.mkdir()
    def compile(job,path):
        out=path/'build/outputs/apk/release';out.mkdir(parents=True)
        for name in ('one.apk','two.apk'):(out/name).write_bytes(b'APK output')
    runner=SimpleNamespace(path=lambda _:tmp_path,compile_tree=compile,native_from_apk=lambda *args:pytest.fail('Ambiguous outputs cannot be converted'))
    with pytest.raises(ValueError):build(runner,{'id':'a'*32,'module':'.','raw_first':True},original,'build-test')
