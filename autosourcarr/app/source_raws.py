"""Install-time code capture; raw objects may be shared, instance state never is.

Only explicit acquisition tasks read repositories. Tasks/status use a bounded
observed cache. A late download or an old upload dialog cannot replace the code
reference for a newer installed version.
"""
import asyncio
import copy
import json
from pathlib import Path
import re
import threading
import time

from bundled.source_tools.archive import RawStore, digest, module_name, package_name,portable_metadata
from bundled.source_tools.ecosystem import source_origin
from bundled.source_tools.fs import atomic_write, directory, file_lock, json_bytes, read_regular
from bundled.source_tools.repository import RepositoryCache
from bundled.source_tools.discovery import discover

from .async_utils import run_blocking
from .source_repositories import fetch_manifest


class SourceRawCatalog:
    def __init__(self, data_dir, *, fetch=fetch_manifest, clock=time.time):
        self.clock=clock; self.lock=threading.RLock(); self.cache={};self.fetch=fetch;self.discovery_cache={}
        root=Path(data_dir)
        self.shared=RawStore(root/'sources'/'raw',public=True)
        self.private=RawStore(root/'source-maintenance'/'raw-private')
        self.requests=directory(root/'source-maintenance'/'raw-requests')
        self.repositories=RepositoryCache(root/'source-maintenance'/'raw-downloads',self.shared,fetch,clock=clock)
        for path in sorted(self.requests.glob('*.json'))[:4096]:
            if not re.fullmatch(r'[0-9a-f]{64}',path.stem):continue
            value=self._read(path)
            if value:self.cache[value['package']]=value

    def _path(self,pkg):
        return self.requests/(digest(package_name(pkg).encode())+'.json')

    def _read(self,path):
        try:value=json.loads(read_regular(path,32768))
        except FileNotFoundError:return None
        if (not isinstance(value,dict) or value.get('schema')!=1
                or type(value.get('revision')) is not int or value['revision']<1
                or value.get('state') not in {'acquiring','available','needs-input','reconstructing','removed'}):
            raise ValueError('Raw-code request journal is damaged; it was not reset')
        if self._path(value.get('package'))!=path:raise ValueError('Raw-code request package identity changed')
        return value

    def _save(self,value):
        atomic_write(self._path(value['package']),json_bytes(value))
        self.cache[value['package']]=copy.deepcopy(value)
        return copy.deepcopy(value)

    def status(self,pkg):
        package_name(pkg)
        with self.lock:return copy.deepcopy(self.cache.get(pkg))

    @staticmethod
    def _binary(value):
        if value is None:return ''
        if not isinstance(value,str) or not re.fullmatch(r'[0-9a-f]{64}',value):raise ValueError('Invalid installed source hash')
        return value

    def begin(self,extension,*,manual=None,binary_sha256=None,supplied=None,force=False):
        origin=source_origin(extension,manual=manual,supplied=supplied)
        pkg=origin['package'];path=self._path(pkg);binary=self._binary(binary_sha256)
        with self.lock,file_lock(path.with_suffix('.lock')):
            old=self._read(path)
            if (old and not force and supplied is None and (old['state']=='available' or old['state']=='needs-input' and old.get('discovery_version')) and binary
                    and old['version']==origin['version'] and old.get('binary_sha256')==binary):
                self.cache[pkg]=old;return copy.deepcopy(old)
            same=bool(old and old['state']!='removed' and old['version']==origin['version'] and old.get('origin')==origin
                and (not binary or not old.get('binary_sha256') or old['binary_sha256']==binary))
            if same and not force:
                if binary and not old.get('binary_sha256'):
                    old['binary_sha256']=binary;old['revision']+=1
                    return self._save(old)
                self.cache[pkg]=old
                return copy.deepcopy(old)
            value={'schema':1,'package':pkg,'version':origin['version'],'revision':(old or {}).get('revision',0)+1,
                'binary_sha256':binary,'origin':origin,'updated_at':self.clock(),
                'state':'acquiring' if origin['state']=='known' else 'needs-input',
                'message':'Archiving the original source project.' if origin['state']=='known' else origin['reason']}
            return self._save(value)

    def _current(self,pkg,revision,*,version=None,binary=None):
        value=self._read(self._path(pkg))
        if (not value or type(revision) is not int or value['revision']!=revision
                or version is not None and value['version']!=version
                or binary is not None and value.get('binary_sha256','')!=binary):
            raise ValueError('The installed source or raw-code request changed. Refresh before supplying code.')
        return value

    def acquire(self,intent):
        pkg=package_name(intent.get('package'));path=self._path(pkg)
        # Network/ZIP work holds neither the journal nor live-source admin lock.
        try:
            snapshot=self.repositories.obtain(intent['origin'])
            changes={'state':'available','raw_id':snapshot['id'],'visibility':'shared',
                     'message':('Upstream candidate code archived. Its revision is not proven to reproduce this installation; compatible build and installed-source identity must pass before repair.' if snapshot.get('origin',{}).get('basis')=='repository-candidate' else 'Original repository code archived; matching build and source identity are checked during repair.')}
        except (ValueError,OSError) as exc:
            changes={'state':'needs-input','message':'Original raw code could not be archived: '+str(exc)[:1600]+'. Provide the project or choose Unavailable.'}
        with self.lock,file_lock(path.with_suffix('.lock')):
            try:current=self._current(pkg,intent['revision'])
            except ValueError:return self.status(pkg)
            current.update(changes,revision=current['revision']+1,updated_at=self.clock())
            return self._save(current)

    def discover(self,intent,extension,index_urls=()):
        """Public reads are outside the journal lock; late discovery is fenced."""
        def cached_fetch(url,**options):
            with self.lock:saved=self.discovery_cache.get(url)
            if saved and 0<=self.clock()-saved[0]<300:return saved[1]
            result=self.fetch(url,**options)
            with self.lock:
                if len(self.discovery_cache)>=8:self.discovery_cache.pop(next(iter(self.discovery_cache)))
                self.discovery_cache[url]=(self.clock(),result)
            return result
        origin=discover(extension,index_urls,cached_fetch)
        path=self._path(intent['package'])
        with self.lock,file_lock(path.with_suffix('.lock')):
            current=self._current(intent['package'],intent['revision'],version=extension['versionName'])
            if current['state']!='needs-input':return current
            current.update(discovery_version=1,updated_at=self.clock(),revision=current['revision']+1)
            if origin['state']=='known':current.update(origin=origin,state='acquiring',message='Declared upstream repository found. Archiving candidate code; original installed bytes remain unchanged.')
            else:current['message']=origin['reason']
            return self._save(current)

    def provide(self,extension,payload,*,expected_revision,binary_sha256=None,module=None,share=False):
        origin=source_origin(extension,manual=True);pkg=origin['package'];path=self._path(pkg)
        binary=self._binary(binary_sha256)
        if type(share) is not bool:raise ValueError('Choose whether the supplied public code may be shared')
        with self.lock,file_lock(path.with_suffix('.lock')):
            current=self._current(pkg,expected_revision,version=origin['version'])
            if current.get('binary_sha256') and current['binary_sha256']!=binary:raise ValueError('Installed binary changed after this raw-code request')
            if current['state'] not in {'needs-input','acquiring'}:raise ValueError('This source already has raw code; start a fresh input request first')
            info={'package':pkg,'version':origin['version'],'module':module_name(module or origin['module']),
                  'ecosystem':current['origin'].get('ecosystem','tachiyomi'),'recipe':'gradle','kind':'provided',
                  'origin':{'basis':'user-provided',**{key:current['origin'][key] for key in ('repository','index_url') if current['origin'].get(key)}}}
            exported=portable_metadata(payload)
            if exported:
                if exported['package']!=pkg:raise ValueError('Exported source code belongs to another installed package')
                info.update(module=exported['module'],ecosystem=exported['ecosystem'],recipe=exported['recipe'])
                for key in ('upstream_module','overlay_sources'):
                    if key in exported:info[key]=exported[key]
                if exported['origin'].get('repository'):info['origin']['repository']=exported['origin']['repository']
            snapshot=(self.shared if share else self.private).put_zip(payload,info)
            current.update(state='available',raw_id=snapshot['id'],visibility='shared' if share else 'private',
                binary_sha256=binary,revision=current['revision']+1,updated_at=self.clock(),message='Provided raw code retained. Build and identity still require validation.')
            return self._save(current)

    def retain(self,pkg,payload,info,*,expected_revision=None,share=False,activate=False,expected_binary=None):
        """Retain a job's reconstructed/repaired code without publishing feedback."""
        path=self._path(pkg)
        if info.get('package')!=pkg:raise ValueError('Retained code has a different package')
        with self.lock,file_lock(path.with_suffix('.lock')):
            current=self._read(path)
            if expected_revision is not None:current=self._current(pkg,expected_revision)
            snapshot=(self.shared if share else self.private).put_zip(payload,info)
            matching=bool(current and current['version']==info['version'] and (expected_binary is None or current.get('binary_sha256')==expected_binary))
            if activate and matching:
                current.update(state='available',raw_id=snapshot['id'],visibility='shared' if share else 'private',
                    revision=current['revision']+1,updated_at=self.clock(),message='Retained '+info['kind']+' code; working confirmation is separate.')
                self._save(current)
            return snapshot

    def resolve(self,pkg,raw_id):
        package_name(pkg)
        # A corrupt private snapshot is never silently replaced by a same-name
        # shared file. Missing IDs alone may fall through to the public store.
        try:return self.private,self.private.get(pkg,raw_id)
        except FileNotFoundError:return self.shared,self.shared.get(pkg,raw_id)

    def request_input(self,pkg,*,expected_revision,binary_sha256):
        path=self._path(pkg)
        with self.lock,file_lock(path.with_suffix('.lock')):
            current=self._current(pkg,expected_revision)
            current.update(state='needs-input',binary_sha256=self._binary(binary_sha256),revision=current['revision']+1,
                updated_at=self.clock(),message='Provide matching original code, or choose Unavailable to reconstruct the installed source.')
            current.pop('raw_id',None);current.pop('visibility',None)
            return self._save(current)

    def removed(self,pkg):
        path=self._path(pkg)
        with self.lock,file_lock(path.with_suffix('.lock')):
            current=self._read(path)
            if current:
                current.update(state='removed',revision=current['revision']+1,updated_at=self.clock(),message='Source uninstalled; archived code versions retained.')
                self._save(current)

    def adopt(self,extension,binary_sha256,raw_id):
        pkg=package_name(extension['pkgName']);path=self._path(pkg)
        store,snapshot=self.resolve(pkg,raw_id)
        with self.lock,file_lock(path.with_suffix('.lock')):
            old=self._read(path)
            value={'schema':1,'package':pkg,'version':extension['versionName'],
                'revision':(old or {}).get('revision',0)+1,'state':'available','binary_sha256':self._binary(binary_sha256),
                'raw_id':raw_id,'visibility':'shared' if store.public else 'private','updated_at':self.clock(),
                'origin':source_origin(extension),'message':'Code reference matches this installed repair; Fixed confirmation remains separate.'}
            return self._save(value)

    def pending_items(self):
        with self.lock:
            rows=[]
            for value in self.cache.values():
                if value['state']!='needs-input':continue
                rows.append({'id':'source-raw:'+digest(value['package'].encode())[:24],
                    'kind':'source-raw-input','name':'Source code required','context':value['package'],
                    'state':'running','activity':'waiting','raw_package':value['package'],
                    'raw_revision':value['revision'],'raw_module':value['origin']['module'],
                    'source_version':value['version'],'message':value['message'],'origin':'manual',
                    'current':0,'total':0,'cancellable':False,'created_at':'','updated_at':''})
            return rows[:500]


class SourceRawService:
    def __init__(self):
        self.catalog=None;self.tasks=set();self.keys={};self.semaphore=None;self.stopping=False;self.error=''

    async def start(self,admin_lock=None):
        from .config import settings
        try:self.catalog=await run_blocking(SourceRawCatalog,settings.data_dir);self.error=''
        except Exception as exc:
            from .support_logs import support_logs
            self.catalog=None;self.error='Source-code storage could not be opened ('+type(exc).__name__+'). Existing files were retained; check diagnostics and storage permissions.'
            support_logs.record('source-raws',self.error);return
        self.semaphore=asyncio.Semaphore(1);self.stopping=False;self.admin_lock=admin_lock
        # Reconcile a stopped acquisition, never replay repair/model work.
        for value in list(self.catalog.cache.values()):
            if value['state']=='acquiring':self.schedule_intent(value)
        self._own(self._backfill,'Installed source inventory',key=('inventory',),origin='automatic')

    async def _backfill(self):
        from .source_engine import source_engine
        from .source_maintenance import inventory
        from .source_orphans import source_orphans
        for _ in range(90):
            if source_engine.state=='ready':break
            if self.stopping:return
            await asyncio.sleep(1)
        else:return
        rows=await inventory()
        rows=await run_blocking(source_orphans.annotate_extensions,[row for row in rows if row.get('isInstalled')])
        for row in rows:
            previous=self.catalog.status(row['pkgName'])
            if (not previous or previous['version']!=row['versionName'] or previous['state']=='removed'
                    or previous['state']=='needs-input' and not previous.get('discovery_version')):
                self.schedule(row,origin='automatic')

    def pending_items(self):
        from .repair_workflow import repair_workflow
        from .support_logs import support_logs
        active={row['package'] for row in repair_workflow.admission_rows() if row.get('raw_only') and row['state'] in {'admitting','accepted'}}
        rows=[row for row in self.catalog.pending_items() if row['raw_package'] not in active] if self.catalog else []
        if self.error:rows.append({'id':'source-raw-storage','kind':'source-raw-acquisition','name':'Source code archive unavailable',
            'state':'failed','activity':'waiting','context':'Source code storage','origin':'automatic','message':self.error,
            'current':0,'total':0,'cancellable':False,'created_at':'','updated_at':''})
        for row in rows:row['message']=support_logs.privacy.text(row['message'])[:1800]
        return rows

    def schedule(self,extension,*,manual=None,origin='manual'):
        if self.stopping or self.catalog is None:return
        # Detached only inside the owned application supervisor. Every task is
        # registered and drained by stop(); this is not a Codex background job.
        async def capture():
            from .repair_workflow import repair_workflow,identity_hash
            from .source_maintenance import inventory,installed_path,sha
            from contextlib import nullcontext
            async with self.admin_lock if self.admin_lock else nullcontext():
                current=extension if extension.get('apkName') else next((row for row in await inventory() if row['pkgName']==extension['pkgName'] and row.get('isInstalled')),None)
                if not current or current['versionName']!=extension['versionName']:return
                binary=sha(await run_blocking(installed_path(current).read_bytes))
                if repair_workflow.history:
                    ref={'binary_sha256':binary,'raw_id':None,'version':current['versionName'],'entrypoint':None,'extension_lib':None,
                         'source_ids':sorted(str(row['id']) for row in current.get('source',{}).get('nodes',[])),
                         'identity_sha256':identity_hash(current),'job_id':None}
                    await run_blocking(repair_workflow.history.observe,current['pkgName'],ref)
            intent=await run_blocking(self.catalog.begin,extension,manual=manual,force=manual is True,binary_sha256=binary)
            if intent['state']=='needs-input' and not intent.get('discovery_version'):
                indexes=await run_blocking(self.configured_indexes)
                intent=await run_blocking(self.catalog.discover,intent,extension,indexes)
            if intent['state']=='acquiring':await self._acquire(intent)
        key=None if manual is True else ('capture',extension['pkgName'],extension.get('versionName'),manual)
        self._own(capture,extension['pkgName'],key=key,origin=origin)

    @staticmethod
    def configured_indexes():
        from .config import settings
        from .source_repositories import repository_snapshot
        try:return [r['url'] for r in repository_snapshot(Path(settings.settings_config_dir)/'source-host.env')['repositories']]
        except (ValueError,OSError):return []

    def discover_intent(self,intent,extension):
        async def operation():
            indexes=await run_blocking(self.configured_indexes)
            row=await run_blocking(self.catalog.discover,intent,extension,indexes)
            if row['state']=='acquiring':await self._acquire(row)
        self._own(operation,intent['package'],key=('discovery',intent['package'],intent['revision']))

    def schedule_intent(self,intent,*,origin='manual'):
        if not self.stopping:self._own(lambda:self._acquire(intent),intent['package'],key=('acquire',intent['package'],intent['revision']),origin=origin)

    def _own(self,operation,pkg,*,key=None,origin='manual'):
        from .task_registry import task_registry
        from .support_logs import support_logs
        if key is not None and key in self.keys:return
        task_id=task_registry.create('Archive source code',context=pkg,kind='source-raw-acquisition',origin=origin,activity='queued')
        async def owned():
            try:
                if origin=='automatic':
                    from .service import service
                    task_registry.mark_waiting(task_id,message='Waiting for an idle browser before archiving source code')
                    await service._wait_for_background_idle()
                async with self.semaphore:
                    task_registry.mark_active(task_id)
                    await operation()
                task_registry.complete(task_id,message='Raw-code capture finished; missing code appears in Tasks → Repairs.')
            except asyncio.CancelledError:
                task_registry.fail(task_id,'Cancelled');raise
            except Exception as exc:
                support_logs.record('source-raws',type(exc).__name__+': '+str(exc))
                task_registry.fail(task_id,str(support_logs.privacy.text(str(exc)))[:1000])
        task=asyncio.create_task(owned());self.tasks.add(task)
        if key is not None:self.keys[key]=task
        def finished(done):
            self.tasks.discard(done)
            if key is not None and self.keys.get(key) is done:self.keys.pop(key,None)
        task.add_done_callback(finished)
        task_registry.bind_runtime(task_id,task)

    async def _acquire(self,intent):
        return await run_blocking(self.catalog.acquire,intent)

    async def stop(self):
        self.stopping=True
        tasks=list(self.tasks)
        for task in tasks:task.cancel()
        if tasks:await asyncio.gather(*tasks,return_exceptions=True)


source_raws=SourceRawService()
