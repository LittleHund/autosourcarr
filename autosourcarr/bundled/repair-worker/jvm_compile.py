"""Trusted isolated Java overlay build for explicitly reconstructed JVM code.

Unchanged installed classes/resources are retained verbatim, except obsolete JAR
signatures removed from the modified candidate. Only classes emitted by javac for
approved edited sources overlay the exact private validation JAR. Raw backups are
unchanged; modified code does not claim the original publisher's signature.
No original-source or successful-compilation claim follows from decompilation.
"""
import io
import json
from pathlib import Path
import subprocess
import zipfile

from source_tools.archive import relative_name
from baseline_manifest import _payloads


def compile_overlay(root=Path('/work'),*,run=subprocess.run):
    plan=json.loads((root/'.repair-build.json').read_text())
    files=plan.get('changed_files')
    if not isinstance(files,list) or not 1<=len(files)<=30:raise ValueError('A bounded reconstructed patch is required')
    sources=[]
    for name in files:
        name=relative_name(name)
        if not name.startswith('src/') or not name.endswith('.java'):raise ValueError('Reconstruction compilation accepts approved existing Java sources only')
        path=root/name
        if path.is_symlink() or not path.is_file() or path.stat().st_size>8*1024*1024:raise ValueError('Invalid reconstructed source file')
        sources.append(str(path))
    out=root/'build'/'classes';out.mkdir(parents=True,exist_ok=False)
    baseline=(root/'.repair-input.jar').read_bytes();_payloads(baseline)
    classpath=':'.join((str(root/'.repair-input.jar'),'/opt/source-jvm/source-api.jar',
                       '/opt/android-sdk-linux/platforms/android-35/android.jar'))
    run(['/opt/build-jdk/bin/javac','-J-Xmx1024m','--release','11','-proc:none','-implicit:none',
         '-encoding','UTF-8','-classpath',classpath,'-d',str(out),*sources],check=True,timeout=600)
    overlay={path.relative_to(out).as_posix():path.read_bytes() for path in out.rglob('*.class')}
    if not overlay or len(overlay)>4096 or sum(map(len,overlay.values()))>32*1024*1024:raise ValueError('Invalid compiled class overlay')
    result=io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(baseline)) as old,zipfile.ZipFile(result,'w',zipfile.ZIP_DEFLATED) as new:
        for info in old.infolist():
            name=info.filename.upper()
            if name.startswith('META-INF/') and (name.endswith(('.SF','.RSA','.DSA','.EC')) or name.startswith('META-INF/SIG-')):continue
            if info.filename not in overlay:new.writestr(info,old.read(info))
        for name,data in sorted(overlay.items()):new.writestr(name,data)
    dest=root/'build'/'outputs'/'jar'/'reconstructed.jar';dest.parent.mkdir(parents=True)
    if len(result.getvalue())>32*1024*1024:raise ValueError('Compiled source exceeds the repair limit')
    dest.write_bytes(result.getvalue())


if __name__=='__main__':compile_overlay()
