"""Immutable source snapshots with shared, content-addressed file objects.

Only code/build resources and bounded public provenance belong here. Instance
credentials, installed APK/JAR binaries and working/revert/feedback state do not.
Archive/hash validation is not proof that a source compiles or is trustworthy.
"""
import hashlib
import io
import ipaddress
import json
from pathlib import PurePosixPath
import re
import stat
from urllib.parse import urlsplit
import zipfile

from .fs import atomic_write, directory, json_bytes, read_regular

MAX_ZIP=32*1024*1024
MAX_REPOSITORY_ZIP=128*1024*1024
MAX_EXPANDED=160*1024*1024
MAX_FILE=8*1024*1024
MAX_FILES=8192
MAX_MANIFEST=2*1024*1024
PORTABLE='.autosourcarr-source.json'
HASH=r'[0-9a-f]{64}'
PACKAGE=r'[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*){2,}'
PRIVATE_NAMES={'.env','local.properties','auth.json','cookies.json','accounts.json',
               'signingkey.jks','signing.properties','secrets.properties','credentials.json',
               '.netrc','.npmrc','.pypirc','.git-credentials'}
IGNORED_DIRS={'.git','.gradle','.idea','.codex','.claude','.agents','build','out','node_modules','__pycache__'}
ROOT_DIRS={'src','gradle','lib','lib-multisrc','common','core','compiler','res','assets','java','kotlin'}


def digest(value):return hashlib.sha256(value).hexdigest()


def relative_name(value):
    if (not isinstance(value,str) or not value or len(value)>512 or '\\' in value or ':' in value
            or any(ord(c)<32 or ord(c)==127 for c in value)):
        raise ValueError('Unsafe raw source path')
    parts=PurePosixPath(value).parts
    if value.startswith('/') or not parts or any(part in {'..','.'} for part in value.split('/')):
        raise ValueError('Raw source paths must stay inside their project')
    return '/'.join(parts)


def package_name(value):
    if not isinstance(value,str) or len(value)>200 or not re.fullmatch(PACKAGE,value):
        raise ValueError('Invalid source package identity')
    return value


def module_name(value):
    if value=='.':return value
    value=relative_name(value)
    if len(value.split('/'))>8 or not re.fullmatch(r'[A-Za-z0-9_/-]+',value):
        raise ValueError('Invalid source project module')
    return value


def public_reference(value):
    if not isinstance(value,str) or not value or len(value)>2048 or any(ord(c)<33 or ord(c)==127 for c in value):
        raise ValueError('Invalid public source origin')
    parsed=urlsplit(value)
    if (parsed.scheme!='https' or not parsed.hostname or parsed.username or parsed.password
            or parsed.query or parsed.fragment or parsed.port not in {None,443}):
        raise ValueError('Source origins require public HTTPS without credentials or query values')
    host=parsed.hostname.lower().encode('idna').decode('ascii').rstrip('.')
    if host=='localhost' or host.endswith(('.localhost','.local')):raise ValueError('Private source origin refused')
    try:address=ipaddress.ip_address(host)
    except ValueError:address=None
    if address is not None and (not address.is_global or address.is_reserved or address.is_multicast):
        raise ValueError('Private source origin refused')
    return value


def metadata(value):
    allowed={'package','version','module','ecosystem','recipe','kind','origin','parent','extension_lib','upstream_module','overlay_sources'}
    if not isinstance(value,dict) or set(value)-allowed:raise ValueError('Unsupported raw source metadata')
    result={key:value.get(key) for key in ('package','version','module','ecosystem','recipe','kind')}
    package_name(result['package']);result['module']=module_name(result['module'])
    if not isinstance(result['version'],str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}',result['version']):
        raise ValueError('Installed source version is required for raw provenance')
    if not isinstance(result['ecosystem'],str) or result['ecosystem'] not in {'tachiyomi','keiyoushi','mihon','suwayomi'}:
        raise ValueError('Unsupported source ecosystem')
    if not isinstance(result['recipe'],str) or result['recipe'] not in {'gradle','jvm'}:raise ValueError('Unsupported compatible source build recipe')
    if not isinstance(result['kind'],str) or result['kind'] not in {'upstream','provided','reconstructed','repaired'}:raise ValueError('Invalid source-code provenance')
    origin=value.get('origin',{})
    if not isinstance(origin,dict) or set(origin)-{'repository','revision','index_url','basis'}:
        raise ValueError('Unsupported source origin metadata')
    result['origin']={}
    for key in ('repository','index_url'):
        if origin.get(key):result['origin'][key]=public_reference(origin[key])
    if origin.get('revision'):
        if not isinstance(origin['revision'],str) or not re.fullmatch(r'[0-9a-f]{40}',origin['revision']):
            raise ValueError('Source revision must be a full commit hash')
        result['origin']['revision']=origin['revision']
    if origin.get('basis'):
        if not isinstance(origin['basis'],str) or origin['basis'] not in {'known-repository','repository-candidate','user-provided','binary-reconstruction','derived-code'}:
            raise ValueError('Invalid source origin basis')
        result['origin']['basis']=origin['basis']
    if value.get('parent'):
        if not isinstance(value['parent'],str) or not re.fullmatch(HASH,value['parent']):raise ValueError('Invalid parent source snapshot')
        result['parent']=value['parent']
    if value.get('extension_lib'):
        if not isinstance(value['extension_lib'],str) or not re.fullmatch(r'[0-9]{1,3}\.[0-9]{1,3}',value['extension_lib']):
            raise ValueError('Invalid extension library metadata')
        result['extension_lib']=value['extension_lib']
    if value.get('upstream_module'):result['upstream_module']=module_name(value['upstream_module'])
    if 'overlay_sources' in value:
        rows=value['overlay_sources'];prefix=('' if result['module']=='.' else result['module']+'/')+'src/'
        if (result['recipe']!='jvm' or not isinstance(rows,list) or not 1<=len(rows)<=30
                or any(not isinstance(name,str) or not name.startswith(prefix) or not name.endswith('.java') for name in rows) or len(set(rows))!=len(rows)):
            raise ValueError('Invalid reconstructed Java overlay source list')
        result['overlay_sources']=sorted(relative_name(name) for name in rows)
    return result


def _raw_files(payload, meta, *, maximum=MAX_ZIP, entry_limit=MAX_FILES):
    if not isinstance(payload,bytes) or not 0<len(payload)<=maximum:raise ValueError('Raw source ZIP exceeds its bounded input limit or is empty')
    result={};total=0
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            infos=archive.infolist()
            if not infos or len(infos)>entry_limit or len({info.filename for info in infos})!=len(infos):
                raise ValueError('Raw source archive has duplicate or excessive entries')
            normalized=[]
            for info in infos:
                if info.orig_filename!=info.filename:
                    raise ValueError('Raw source archive contains an ambiguous normalized filename')
                name=relative_name(info.orig_filename.rstrip('/'))
                mode=info.external_attr>>16
                if info.flag_bits&1 or stat.S_ISLNK(mode) or stat.S_IFMT(mode) not in {0,stat.S_IFREG,stat.S_IFDIR}:
                    raise ValueError('Raw source archives cannot contain links, special files or encryption')
                if info.is_dir():continue
                normalized.append((name,info))
            first={name.split('/')[0] for name,_ in normalized}
            prefix=''
            if len(first)==1:
                candidate=next(iter(first))
                root_files={name[len(candidate)+1:] for name,_ in normalized if name.startswith(candidate+'/')}
                if (candidate not in ROOT_DIRS and candidate!=meta['module'].split('/')[0]
                        and any(name in root_files for name in ('gradlew','settings.gradle.kts','settings.gradle','LICENSE'))):
                    prefix=candidate+'/'
            for name,info in normalized:
                name=relative_name(name[len(prefix):] if prefix else name)
                if name==PORTABLE:continue  # Typed provenance is data, never an editable project file.
                parts=PurePosixPath(name).parts;lower=parts[-1].lower()
                if any(part in IGNORED_DIRS for part in parts[:-1]):continue
                if lower in PRIVATE_NAMES or lower.startswith('.env') or lower.endswith(('.jks','.keystore','.pem','.p12','.pfx','.key')):
                    raise ValueError('Remove private settings, accounts or signing files from the raw-code archive')
                if meta['module'].startswith('src/') and name.startswith('src/') and not name.startswith(meta['module']+'/'):
                    continue  # Keep the selected module and shared build/libraries, not other extensions.
                if lower.endswith(('.apk','.dex','.class')) or lower.endswith('.jar') and name!='gradle/wrapper/gradle-wrapper.jar':
                    raise ValueError('Installed/compiled source packages are not shareable raw code')
                if name in result or len(result)>=MAX_FILES or not 0<=info.file_size<=MAX_FILE:
                    raise ValueError('Raw source file is duplicate or too large')
                total+=info.file_size
                if total>MAX_EXPANDED:raise ValueError('Expanded raw source archive exceeds160MiB')
                data=archive.read(info)
                if len(data)!=info.file_size:raise ValueError('Raw source entry size changed')
                if b'-----BEGIN ' in data and b'PRIVATE KEY-----' in data:
                    raise ValueError('Private key material is not accepted as source code')
                result[name]=(data,bool((info.external_attr>>16)&0o111) or name=='gradlew')
    except (zipfile.BadZipFile,RuntimeError,UnicodeError,OSError) as exc:
        raise ValueError('Raw source ZIP could not be read safely') from exc
    base='' if meta['module']=='.' else meta['module']+'/'
    if not any(name.startswith(base) and name.endswith(('.kt','.java')) for name in result):
        raise ValueError('No Kotlin/Java source files exist in the selected project module')
    if any(name not in result for name in meta.get('overlay_sources',[])):
        raise ValueError('Reconstructed overlay references a missing source file')
    return result


def portable_metadata(payload):
    if not isinstance(payload,bytes) or not 0<len(payload)<=MAX_ZIP:raise ValueError('Invalid raw source ZIP')
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            rows=[info for info in archive.infolist() if info.filename==PORTABLE]
            if not rows:return None
            if len(rows)!=1 or rows[0].file_size>65536:raise ValueError('Invalid exported source provenance')
            value=json.loads(archive.read(rows[0]))
            if not isinstance(value,dict) or set(value)!={'schema','metadata'} or type(value['schema']) is not int or value['schema']!=1:raise ValueError('Invalid exported source provenance')
            return metadata(value['metadata'])
    except (zipfile.BadZipFile,RuntimeError,RecursionError,UnicodeError) as exc:raise ValueError('Exported source provenance is unreadable') from exc


class RawStore:
    def __init__(self, root, *, public=False):
        self.root=directory(root,public=public);self.public=public

    def _object(self, key):
        if not re.fullmatch(HASH,str(key)):raise ValueError('Invalid raw object fingerprint')
        return self.root/'objects'/key[:2]/key[2:]

    def _version(self, pkg, identifier):
        package_name(pkg)
        if not re.fullmatch(HASH,str(identifier)):raise ValueError('Invalid raw version fingerprint')
        return self.root/'versions'/digest(pkg.encode())/(str(identifier)+'.json')

    def put_zip(self, payload, info):
        meta=metadata(info);files=_raw_files(payload,meta)
        return self._put(meta,files)

    def put_repository_file(self, path, info):
        # Trusted downloader cache only. User uploads keep the smaller ZIP cap;
        # only the selected module and shared build code enter public objects.
        meta=metadata(info);payload=read_regular(path,MAX_REPOSITORY_ZIP)
        files=_raw_files(payload,meta,maximum=MAX_REPOSITORY_ZIP,entry_limit=65536)
        return self._put(meta,files)

    def _put(self, meta, files):
        refs={name:{'sha256':digest(data),'bytes':len(data),'executable':executable}
              for name,(data,executable) in sorted(files.items())}
        manifest={**meta,'schema':1,'files':refs}
        identifier=digest(json_bytes(manifest));value={**manifest,'id':identifier}
        encoded=json_bytes(value)
        if len(encoded)>MAX_MANIFEST:raise ValueError('Raw source receipt exceeds its metadata limit')
        # Validate every entry before publishing any code, never private input files.
        for name,(data,_) in files.items():
            atomic_write(self._object(refs[name]['sha256']),data,public=self.public,immutable=True)
        atomic_write(self._version(meta['package'],identifier),encoded,public=self.public,immutable=True)
        return value

    def get(self, pkg, identifier):
        path=self._version(pkg,identifier)
        directory(path.parent,public=self.public)
        try:value=json.loads(read_regular(path,MAX_MANIFEST))
        except (ValueError,UnicodeError) as exc:raise ValueError('Raw source receipt is invalid') from exc
        if not isinstance(value,dict) or type(value.get('schema')) is not int or value.get('schema')!=1 or value.get('id')!=identifier or value.get('package')!=pkg:
            raise ValueError('Raw source receipt identity changed')
        meta=metadata({key:item for key,item in value.items() if key not in {'schema','id','files'}})
        refs=value.get('files');total=0
        if not isinstance(refs,dict) or not 1<=len(refs)<=MAX_FILES:raise ValueError('Raw source receipt file limit')
        for name,ref in refs.items():
            relative_name(name)
            if (not isinstance(ref,dict) or set(ref)!={'sha256','bytes','executable'} or not re.fullmatch(HASH,str(ref.get('sha256')))
                    or type(ref.get('bytes')) is not int or not 0<=ref['bytes']<=MAX_FILE or type(ref.get('executable')) is not bool):
                raise ValueError('Raw source receipt file identity is invalid')
            total+=ref['bytes']
        if total>MAX_EXPANDED or digest(json_bytes({**meta,'schema':1,'files':refs}))!=identifier:
            raise ValueError('Raw source receipt fingerprint changed')
        return value

    def _contents(self, value):
        for name,ref in sorted(value['files'].items()):
            path=self._object(ref['sha256']);directory(path.parent,public=self.public)
            data=read_regular(path,ref['bytes'])
            if len(data)!=ref['bytes'] or digest(data)!=ref['sha256']:raise ValueError('Raw source object failed its integrity check')
            yield name,data,ref['executable']

    def export_zip(self, pkg, identifier, *, portable=False):
        value=self.get(pkg,identifier);output=io.BytesIO()
        with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
            for name,data,executable in self._contents(value):
                info=zipfile.ZipInfo(name,date_time=(1980,1,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED
                info.create_system=3;info.external_attr=(stat.S_IFREG|(0o755 if executable else 0o644))<<16
                archive.writestr(info,data)
                if output.tell()>MAX_ZIP:raise ValueError('Raw source export exceeds32MiB')
            if portable:
                info=metadata({key:item for key,item in value.items() if key not in {'schema','id','files'}})
                archive.writestr(PORTABLE,json_bytes({'schema':1,'metadata':info}))
        result=output.getvalue()
        if len(result)>MAX_ZIP:raise ValueError('Raw source export exceeds32MiB')
        return result

    def materialize(self, pkg, identifier, target):
        target=directory(target)
        if any(target.iterdir()):raise ValueError('Raw code requires an empty private job directory')
        value=self.get(pkg,identifier)
        for name,data,executable in self._contents(value):
            atomic_write(target/relative_name(name),data,immutable=True,executable=executable)
        return value

    def versions(self, pkg, limit=50):
        package_name(pkg);root=self.root/'versions'/digest(pkg.encode())
        if not root.exists():return []
        directory(root,public=self.public)
        paths=list(root.glob('*.json'))
        if len(paths)>4096:raise ValueError('Use a specific raw version; source history exceeds the listing limit')
        paths.sort(key=lambda path:path.stat().st_mtime_ns,reverse=True)
        return [self.get(pkg,path.stem) for path in paths[:max(1,min(int(limit),100))]]
