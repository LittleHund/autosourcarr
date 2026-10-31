"""Real class-header bytes; no JVM/native import success is implied."""
import importlib
import io
from pathlib import Path
import struct
import zipfile

import pytest

PKG='eu.kanade.tachiyomi.extension.en.fixture'


@pytest.fixture
def api(monkeypatch):
    root=Path(__file__).resolve().parents[2]
    monkeypatch.syspath_prepend(str(root));monkeypatch.syspath_prepend(str(root/'repair-worker'))
    return importlib.import_module('reconstruction')


def bytecode(name=PKG.replace('.','/')+'/ActualFactory',*,public=True,constructor=True,metadata=b'\xc0\x80'):
    values=[name.encode(),b'eu/kanade/tachiyomi/source/SourceFactory',b'java/lang/Object',b'<init>',b'()V',metadata]
    cp=b''.join(b'\x01'+struct.pack('>H',len(v))+v for v in values)
    cp+=b'\x07\x00\x01\x07\x00\x02\x07\x00\x03'
    head=b'\xca\xfe\xba\xbe'+struct.pack('>HHH',0,55,10)+cp
    return head+struct.pack('>HHHHHH',1 if public else 0,7,9,1,8,0)+struct.pack('>H',1 if constructor else 0)+(struct.pack('>HHHH',1,4,5,0) if constructor else b'')+b'\x00\x00'


def jar(data=None,*,name=None):
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w') as archive:
        archive.writestr(name or PKG.replace('.','/')+'/ActualFactory.class',data or bytecode())
        archive.writestr('assets/retained.txt',b'original resource')
    return output.getvalue()


def test_bytecode_derives_real_factory_and_keeps_modified_utf8_metadata_opaque(api):
    assert api.loading_candidates(jar(),PKG)==[PKG+'.ActualFactory']
    envelope=api.validation_envelope(jar(),pkg=PKG,entrypoint=PKG+'.ActualFactory',version='1.4.23',library='1.4')
    with zipfile.ZipFile(io.BytesIO(envelope)) as archive:
        assert archive.read(PKG.replace('.','/')+'/ActualFactory.class')==bytecode()
        assert archive.read('assets/retained.txt')==b'original resource'
        assert b'binary' not in archive.read('AndroidManifest.xml')


@pytest.mark.parametrize('data,name',[(bytecode()[:-1],None),(bytecode(public=False),None),(bytecode(constructor=False),None),
    (bytecode(),PKG.replace('.','/')+'/Guessed.class')])
def test_no_module_name_guess_or_invalid_loading_identity(api,data,name):
    with pytest.raises(ValueError):api.loading_candidates(jar(data,name=name),PKG)


def test_envelope_cannot_invent_entrypoint_or_library(api):
    with pytest.raises(ValueError):api.validation_envelope(jar(),pkg=PKG,entrypoint=PKG+'.Guessed',version='1.4.23',library='1.4')
    with pytest.raises(ValueError):api.validation_envelope(jar(),pkg=PKG,entrypoint=PKG+'.ActualFactory',version='1.4.23',library='9.9')


def test_renamed_package_can_retain_its_actual_old_bytecode_namespace(api):
    payload=jar(bytecode(name='other/namespace/Factory'),name='other/namespace/Factory.class')
    assert api.loading_candidates(payload,PKG)==['other.namespace.Factory']
    # Candidate discovery still is not a cold-import/source-identity proof.


def test_unrelated_class_resources_do_not_invent_or_hide_the_actual_factory(api):
    output=io.BytesIO(jar())
    with zipfile.ZipFile(output,'a') as archive:
        archive.writestr('assets/unrelated.class',b'not JVM bytecode')
        archive.writestr('module-info.class',b'not an instantiable source')
    assert api.loading_candidates(output.getvalue(),PKG)==[PKG+'.ActualFactory']


def test_cyclic_interface_graph_is_bounded_and_does_not_prove_a_source(api,monkeypatch):
    output=io.BytesIO();headers={}
    with zipfile.ZipFile(output,'w') as archive:
        for number in range(300):
            name=PKG.replace('.','/')+'/Class'+str(number)
            parents=[PKG.replace('.','/')+'/Class'+str((number+step)%300) for step in (1,2)]
            headers[str(number).encode()]={'name':name,'parent':parents[0],'interfaces':parents,
                'public':True,'concrete':True,'constructor':True}
            archive.writestr(name+'.class',str(number).encode())
    monkeypatch.setattr(api,'class_header',lambda data:headers[data])
    with pytest.raises(ValueError):api.loading_candidates(output.getvalue(),PKG)
