"""Private encrypted snapshots and restart-time verified, journalled restore.

No app imports at module scope: the launcher applies pending state before any
database, source engine or helper is opened. Backups require caller quiescence.
"""
from contextlib import closing
from datetime import datetime,timezone
import hashlib
import io
import json
import os
from pathlib import Path,PurePosixPath
import re
import shutil
import sqlite3
import stat
import tempfile
import time
import uuid
import zipfile

MAGIC=b'ASCBK2\n'
MAX_ARCHIVE=128*1024*1024
MAX_TOTAL=512*1024*1024
MAX_FILES=20000
EXCLUDED={'cover-cache','instance-backups','diagnostics','logs','support-logs','downloads','node_modules','.cache','cache','caches','kcef','chrome','chromium','runtime'}


def repair_idle_for_snapshot(value):
    """Fresh explicit ownership flags, never historical job labels, admit backup."""
    if not isinstance(value,dict) or 'active' not in value: return False
    if any(type(value.get(key)) is not bool for key in ('pending_jobs','account_busy','cleanup_uncertain')):return False
    return value['active'] is None and not any(value[key] for key in ('pending_jobs','account_busy','cleanup_uncertain'))


def digest(content):return hashlib.sha256(content).hexdigest()
def timestamp():return datetime.now(timezone.utc).isoformat()


def atomic(path,content):
    path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    tmp=path.with_name(path.name+'.pending')
    with open(tmp,'wb') as f:
        os.chmod(tmp,0o600);f.write(content);f.flush();os.fsync(f.fileno())
    os.replace(tmp,path)


def publish_file(source,destination,expected_hash=None):
    destination.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    tmp=destination.with_name(destination.name+'.pending');checksum=hashlib.sha256()
    try:
        with source.open('rb') as src,tmp.open('wb') as dst:
            os.chmod(tmp,0o600)
            for chunk in iter(lambda:src.read(65536),b''):
                checksum.update(chunk);dst.write(chunk)
            dst.flush();os.fsync(dst.fileno())
        if expected_hash is not None and checksum.hexdigest()!=expected_hash:raise ValueError('Staged backup content hash changed during publication')
        os.replace(tmp,destination)
    finally:tmp.unlink(missing_ok=True)


def protect(content,password,*,decrypt=False):
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.exceptions import InvalidTag
    if not isinstance(password,str) or not 12<=len(password)<=1024:raise ValueError('Use a passphrase of 12 to 1024 characters')
    if len(content)>MAX_ARCHIVE+64:raise ValueError('Backup exceeds 128 MiB encrypted input limit')
    if decrypt:
        if not content.startswith(MAGIC) or len(content)<len(MAGIC)+44:raise ValueError('Unsupported encrypted backup')
        salt=content[len(MAGIC):len(MAGIC)+16];nonce=content[len(MAGIC)+16:len(MAGIC)+28]
    else:salt=os.urandom(16);nonce=os.urandom(12)
    key=hashlib.pbkdf2_hmac('sha256',password.encode('utf-8'),salt,600000,dklen=32)
    cipher=AESGCM(key)
    if decrypt:
        try:return cipher.decrypt(nonce,content[len(MAGIC)+28:],MAGIC+salt)
        except InvalidTag as exc:raise ValueError('Wrong passphrase or damaged encrypted backup') from exc
    return MAGIC+salt+nonce+cipher.encrypt(nonce,content,MAGIC+salt)


class InstanceBackups:
    def __init__(self,roots,version):
        self.roots={k:Path(v).resolve() for k,v in roots.items()}
        if set(self.roots)-{'config','data','engine','db'} or not {'config','data','db'}<=set(self.roots):raise ValueError('Invalid instance roots')
        self.version=version;self.root=self.roots['data']/'instance-backups'
        self.root.mkdir(parents=True,exist_ok=True,mode=0o700)
        os.chmod(self.root,0o700)
        self.pending_path=self.roots['config']/'instance-restore.json'

    def files(self):
        result={};dbpaths={self.roots['db']/n for n in ('catalog.sqlite','chapter-origins.sqlite')}
        for label,root in self.roots.items():
            if label=='db' or not root.is_dir():continue
            for parent,dirs,names in os.walk(root,followlinks=False):
                parent=Path(parent)
                dirs[:]=[d for d in dirs if d not in EXCLUDED and not (parent/d).is_symlink() and not (parent/d).is_junction()]
                for name in names:
                    path=parent/name
                    if any(other!=root and other.is_relative_to(root) and path.is_relative_to(other)
                           for other in self.roots.values()):continue
                    if path==self.pending_path or name in {'database-relocation.json','instance-restore.json'}:continue
                    if path in dbpaths or any(str(path)==str(dbp)+s for dbp in dbpaths for s in ('-wal','-shm')):continue
                    if path.is_symlink() or not path.is_file():raise ValueError('Linked or special instance files cannot be backed up')
                    result[label+'/'+path.relative_to(root).as_posix()]=path
        return result

    @staticmethod
    def snapshot(source,target,deadline):
        with closing(sqlite3.connect(source.as_uri()+'?mode=ro',uri=True,timeout=1)) as src,closing(sqlite3.connect(target)) as dst:
            def progress(*args):
                if time.monotonic()>deadline:raise TimeoutError('Database backup deadline exceeded')
            src.backup(dst,pages=256,progress=progress,sleep=.01)
            dst.commit()

    @staticmethod
    def check_database(path,table):
        with closing(sqlite3.connect(path.as_uri()+'?mode=ro',uri=True,timeout=1)) as c:
            if c.execute('PRAGMA quick_check').fetchall()!=[('ok',)]:raise ValueError('Database integrity check failed')
            if c.execute('PRAGMA foreign_key_check').fetchone():raise ValueError('Database foreign-key check failed')
            if not c.execute('SELECT 1 FROM sqlite_master WHERE type=\'table\' AND name=?',(table,)).fetchone():raise ValueError('Backup is missing the required database schema')

    def create(self,password):
        if self.pending_path.exists() or (self.roots['config']/'database-relocation.json').exists():
            raise ValueError('Finish the pending restore or database relocation before creating a backup')
        if len(list(self.root.glob('*.ascbackup')))>=10:raise ValueError('Ten backups are retained. Export and remove an old backup before creating another.')
        identifier=uuid.uuid4().hex;deadline=time.monotonic()+120
        manifest={'format':2,'version':self.version,'created_at':timestamp(),'paths':{k:str(v) for k,v in self.roots.items()},
                  'excluded':sorted(EXCLUDED),'files':{},'directories':{}}
        with tempfile.TemporaryDirectory(prefix='snapshot-',dir=self.root) as raw:
            temporary=Path(raw)
            files=self.files()
            for name,table in [('catalog.sqlite','entries'),('chapter-origins.sqlite','virtual_chapters')]:
                target=temporary/name
                self.snapshot(self.roots['db']/name,target,deadline);self.check_database(target,table)
                files['db/'+name]=target
            total=0;buffer=io.BytesIO()
            with zipfile.ZipFile(buffer,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=3) as z:
                for name,path in sorted(files.items()):
                    if len(manifest['files'])>=MAX_FILES or time.monotonic()>deadline:raise ValueError('Backup file/deadline limit exceeded')
                    before=path.stat();total+=before.st_size
                    if total>MAX_TOTAL:raise ValueError('Selected instance state exceeds the 512 MiB backup limit')
                    checksum=hashlib.sha256()
                    with path.open('rb') as source,z.open(name,'w') as output:
                        for chunk in iter(lambda:source.read(65536),b''):
                            checksum.update(chunk);output.write(chunk)
                    after=path.stat()
                    if (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns):raise ValueError('Instance files changed during snapshot; retry after work finishes')
                    if buffer.tell()>MAX_ARCHIVE:raise ValueError('Compressed backup exceeds 128 MiB limit')
                    manifest['files'][name]={'sha256':checksum.hexdigest(),'bytes':before.st_size,
                        'mode':stat.S_IMODE(before.st_mode)&0o777,'uid':before.st_uid,'gid':before.st_gid}
                    label=PurePosixPath(name).parts[0]
                    if label!='db':
                        root=self.roots[label]
                        for directory in (path.parent,*path.parent.parents):
                            if not directory.is_relative_to(root):break
                            ds=directory.stat();key=label+'/'+directory.relative_to(root).as_posix()
                            manifest['directories'][key]={'mode':stat.S_IMODE(ds.st_mode)&0o777,'uid':ds.st_uid,'gid':ds.st_gid}
                z.writestr('instance.json',json.dumps(manifest))
            encrypted=protect(buffer.getvalue(),password)
        atomic(self.root/(identifier+'.ascbackup'),encrypted)
        summary={'id':identifier,'created_at':manifest['created_at'],'version':self.version,'bytes':len(encrypted),'files':len(manifest['files'])}
        atomic(self.root/(identifier+'.json'),json.dumps(summary).encode())
        return summary

    def export(self,identifier):
        if not re.fullmatch('[0-9a-f]{32}',identifier):raise ValueError('Invalid backup identity')
        path=self.root/(identifier+'.ascbackup')
        if path.is_symlink() or not path.is_file():raise ValueError('Backup is unavailable')
        return path

    def listing(self):
        return [json.loads(p.read_text()) for p in sorted(self.root.glob('*.json'),key=lambda p:p.stat().st_mtime,reverse=True)[:50]]

    def delete(self,identifier):
        path=self.export(identifier)
        path.unlink();(self.root/(identifier+'.json')).unlink(missing_ok=True)
        return {'deleted':identifier}

    def stage(self,encrypted,password):
        if self.pending_path.exists():raise ValueError('A restore is already pending; finish it before staging another')
        data=protect(encrypted,password,decrypt=True)
        identifier=uuid.uuid4().hex;target=self.root/'staged'/identifier
        target.mkdir(parents=True,mode=0o700)
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                infos=z.infolist();names=[p.filename for p in infos]
                if len(infos)>MAX_FILES+1 or len(names)!=len(set(names)) or 'instance.json' not in names:raise ValueError('Invalid backup inventory')
                if sum(i.file_size for i in infos)>MAX_TOTAL+8*1024*1024:raise ValueError('Backup decoded byte limit exceeded')
                if z.getinfo('instance.json').file_size>8*1024*1024:raise ValueError('Backup inventory limit exceeded')
                manifest=json.loads(z.read('instance.json'))
                if manifest.get('format')!=2 or set(names)!={'instance.json',*manifest['files']}:raise ValueError('Backup manifest does not match archive')
                if manifest.get('version')!=self.version:raise ValueError('Restore requires the same application version')
                if manifest.get('paths')!={k:str(v) for k,v in self.roots.items()}:raise ValueError('Restore requires the same configured container paths')
                for info in infos:
                    if info.filename=='instance.json':continue
                    p=PurePosixPath(info.filename)
                    if p.is_absolute() or '..' in p.parts or '\\' in info.filename or ':' in info.filename or len(p.parts)<2 or p.parts[0] not in self.roots:
                        raise ValueError('Unsafe backup path')
                    if any(part in EXCLUDED for part in p.parts[1:]) or p.name in {'instance-restore.json','database-relocation.json'}:
                        raise ValueError('Backup contains a reserved instance path')
                    if p.parts[0]=='db' and info.filename not in {'db/catalog.sqlite','db/chapter-origins.sqlite'}:raise ValueError('Unexpected database file')
                    mode=info.external_attr>>16
                    if stat.S_IFMT(mode) not in (0,stat.S_IFREG):raise ValueError('Backup contains a linked/special file')
                    dest=target/info.filename;dest.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
                    checksum=hashlib.sha256();copied=0
                    with z.open(info) as source,dest.open('wb') as out:
                        os.chmod(dest,0o600)
                        for chunk in iter(lambda:source.read(65536),b''):
                            copied+=len(chunk)
                            if copied>info.file_size:raise ValueError('Backup member size changed')
                            checksum.update(chunk);out.write(chunk)
                    if checksum.hexdigest()!=manifest['files'][info.filename]['sha256']:raise ValueError('Backup content hash mismatch')
            atomic(target/'instance.json',json.dumps(manifest).encode())
            self.verify_stage(target)
            summary={'id':identifier,'revision':digest((target/'instance.json').read_bytes()),'integrity':'ok','databases':2,
                     'files':len(manifest['files']),'version':manifest['version'],'created_at':manifest['created_at'],
                     'includes':['Both databases and manual decisions','Settings and signing identity','Retained instance/source state'],
                     'excluded':manifest['excluded']}
            prior=sorted((p for p in (self.root/'staged').iterdir() if p.is_dir() and p!=target),key=lambda p:p.stat().st_mtime,reverse=True)
            for obsolete in prior[1:]:
                if obsolete.is_symlink() or obsolete.is_junction() or not obsolete.resolve().is_relative_to((self.root/'staged').resolve()):raise ValueError('Linked staging directory refused')
                shutil.rmtree(obsolete)
            return summary
        except BaseException:
            # Only this freshly created staging directory is eligible for cleanup.
            assert target.resolve().is_relative_to((self.root/'staged').resolve())
            shutil.rmtree(target)
            raise

    def verify_stage(self,target):
        manifest=json.loads((target/'instance.json').read_text())
        for name,expected in manifest['files'].items():
            p=target/name
            if not p.resolve().is_relative_to(target.resolve()) or p.is_symlink():
                raise ValueError('Staged backup content hash changed')
            with p.open('rb') as f:
                if hashlib.file_digest(f,'sha256').hexdigest()!=expected['sha256']:raise ValueError('Staged backup content hash changed')
            for field in ('uid','gid','mode'):
                if type(expected.get(field)) is not int or not 0<=expected[field]<=2147483647:raise ValueError('Invalid restored file metadata')
        for name,metadata in manifest.get('directories',{}).items():
            parts=PurePosixPath(name).parts
            if not parts or parts[0] not in self.roots or '..' in parts or ':' in name or '\\' in name or PurePosixPath(name).is_absolute():
                raise ValueError('Invalid directory metadata path')
            prefix=PurePosixPath(name).as_posix().rstrip('/')+'/'
            if not any(filename.startswith(prefix) for filename in manifest['files']):raise ValueError('Directory metadata has no selected files')
            for field in ('uid','gid','mode'):
                if type(metadata.get(field)) is not int or not 0<=metadata[field]<=2147483647:raise ValueError('Invalid restored directory metadata')
        for name,table in [('catalog.sqlite','entries'),('chapter-origins.sqlite','virtual_chapters')]:self.check_database(target/'db'/name,table)
        return manifest

    def arm(self,identifier,revision,confirmation):
        if confirmation!='RESTORE THIS INSTANCE':raise ValueError('Type RESTORE THIS INSTANCE to apply')
        if not re.fullmatch('[0-9a-f]{32}',identifier):raise ValueError('Invalid staged identity')
        if self.pending_path.exists():raise ValueError('A restore is already pending; finish it before staging another')
        if (self.roots['config']/'database-relocation.json').exists():raise ValueError('Finish database relocation before restoring an instance')
        target=self.root/'staged'/identifier;self.verify_stage(target)
        if digest((target/'instance.json').read_bytes())!=revision:raise ValueError('Staged backup changed; preview again')
        atomic(self.pending_path,json.dumps({'id':identifier,'revision':revision,'armed_at':timestamp()}).encode())
        return {'restart_required':True,'id':identifier}

    def apply_pending(self):
        if not self.pending_path.exists():return {'restored':False}
        marker=json.loads(self.pending_path.read_text());identifier=marker['id']
        if not re.fullmatch('[0-9a-f]{32}',identifier):raise ValueError('Invalid pending restore identity')
        target=self.root/'staged'/identifier;manifest=self.verify_stage(target)
        if digest((target/'instance.json').read_bytes())!=marker['revision']:raise ValueError('Pending restore manifest hash changed')
        if manifest['paths']!={k:str(v) for k,v in self.roots.items()}:raise ValueError('Pending restore paths changed')
        rollback=self.root/'rollback'/identifier;rollback.mkdir(parents=True,exist_ok=True,mode=0o700)
        # Keep a restart-replayable, exact pre-restore copy. The marker remains
        # until all verified files are installed; failure blocks app startup.
        old=self.files()
        for name in ('catalog.sqlite','chapter-origins.sqlite'):
            p=self.roots['db']/name
            if p.exists():old['db/'+name]=p
        for name,path in old.items():
            backup=rollback/name
            if not backup.exists():
                if name.startswith('db/'):
                    backup.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
                    self.snapshot(path,backup,time.monotonic()+120)
                else:publish_file(path,backup)
        for name in sorted(manifest['files']):
            parts=PurePosixPath(name).parts;destination=self.roots[parts[0]].joinpath(*parts[1:])
            if any(p.is_symlink() or p.is_junction() for p in (destination,*destination.parents) if p!=p.parent):raise ValueError('Linked restore target refused')
            publish_file(target/name,destination,manifest['files'][name]['sha256'])
            metadata=manifest['files'][name]
            os.chmod(destination,metadata['mode']&0o777)
            if hasattr(os,'chown') and os.geteuid()==0:os.chown(destination,metadata['uid'],metadata['gid'])
        for name,metadata in sorted(manifest.get('directories',{}).items(),key=lambda row:len(row[0]),reverse=True):
            parts=PurePosixPath(name).parts
            if not parts or parts[0] not in self.roots or '..' in parts or ':' in name or '\\' in name:raise ValueError('Invalid directory metadata path')
            directory=self.roots[parts[0]].joinpath(*parts[1:])
            if directory.is_symlink() or directory.is_junction():raise ValueError('Linked directory refused')
            os.chmod(directory,int(metadata['mode'])&0o777)
            if hasattr(os,'chown') and os.geteuid()==0:os.chown(directory,int(metadata['uid']),int(metadata['gid']))
        for name,path in old.items():
            if name not in manifest['files']:path.unlink()
        for name in ('catalog.sqlite','chapter-origins.sqlite'):
            for suffix in ('-wal','-shm'):(self.roots['db']/(name+suffix)).unlink(missing_ok=True)
        self.pending_path.unlink()
        return {'restored':True,'id':identifier,'rollback_retained':True}


if __name__=='__main__':
    # Launcher-only path. Import config after this module is defined; no database
    # or helper imports occur until pending files have been verified and applied.
    from .config import settings
    roots={'config':settings.settings_config_dir,'data':settings.data_dir,'db':settings.database_base_dir}
    if settings.source_engine_mode=='integrated':roots['engine']=Path('/home/suwayomi/.local/share/Tachidesk')
    store=InstanceBackups(roots,(Path(__file__).resolve().parent.parent/'VERSION').read_text().strip())
    store.apply_pending()
