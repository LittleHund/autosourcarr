"""Export fixed reviewed host policies without host access or configuration edits."""
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

FILES=('repair-seccomp.json','autosourcarr-internal.apparmor','LICENSE-moby','repair-host-setup.md')
POLICY_ROOTS=(Path('/opt/autosourcarr-tools/host-security'),Path(__file__).resolve().parents[1]/'bundled/host-security')


def export_host_setup():
    root=next((p for p in POLICY_ROOTS if p.is_dir() and not p.is_symlink()),None)
    if root is None: raise ValueError('Bundled host policies are missing. Rebuild the current AutoSourcarr image.')
    buffer=BytesIO()
    with ZipFile(buffer,'w',compression=ZIP_DEFLATED) as archive:
        for name in FILES:
            path=root/name
            if path.is_symlink() or not path.is_file() or path.stat().st_size>256*1024:
                raise ValueError('Bundled host policy is missing or invalid. Rebuild the current AutoSourcarr image.')
            archive.writestr('README.md' if name=='repair-host-setup.md' else 'security/'+name,path.read_bytes())
    return buffer.getvalue()
