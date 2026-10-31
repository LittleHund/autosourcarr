"""Publish a previously validated native JAR atomically, per instance."""
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile


def publish(source, config, version):
    data = Path(source).read_bytes()
    if not 0 < len(data) <= 32*1024*1024: raise ValueError('Invalid JAR size')
    digest = hashlib.sha256(data).hexdigest()
    root = Path(config)/'extension-downloads'
    root.mkdir(parents=True,exist_ok=True)
    os.chmod(root,0o700)
    def replace(path, content):
        fd,tmp=tempfile.mkstemp(dir=root)
        try:
            with os.fdopen(fd,'wb') as stream:
                stream.write(content);stream.flush();os.fsync(stream.fileno())
                uid=int(os.environ.get("AUTOSOURCARR_PUBLISH_UID",os.getuid()))
                gid=int(os.environ.get("AUTOSOURCARR_PUBLISH_GID",os.getgid()))
                if (os.getuid(),os.getgid()) != (uid,gid): os.fchown(stream.fileno(),uid,gid)
            os.replace(tmp,path)
        finally: Path(tmp).unlink(missing_ok=True)
    replace(root/f'{version}-{digest}.jar',data)
    replace(root/'current-jar.json',json.dumps({'app_version':version,'sha256':digest,'size':len(data),'validated':True}).encode())

if __name__ == '__main__': publish(*sys.argv[1:])
