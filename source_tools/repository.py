"""Reuse pinned public repository downloads; publish only selected raw code.

HTTP is supplied by the controller's existing TLS/public-address-pinned fetcher.
Whole repository ZIP caches are private and disposable; retained code objects
and per-source version manifests live in RawStore and are never pruned here.
"""
import json
import math
import re
import time
import zipfile
import io
from urllib.parse import quote

from .archive import MAX_REPOSITORY_ZIP, digest, metadata
from .ecosystem import repository_archive, repository_plan
from .fs import atomic_write, directory, file_lock, json_bytes, read_regular


class RepositoryCache:
    def __init__(self,root,store,fetch,*,clock=time.time):
        self.root=directory(root);self.store=store;self.fetch=fetch;self.clock=clock

    def _fetch(self,url,maximum,seconds,*,accept=None):
        options={'max_bytes':maximum,'deadline':time.monotonic()+seconds}
        if accept:options['accept']=accept
        result=self.fetch(url,**options)
        if not isinstance(result,tuple) or len(result)!=2 or not isinstance(result[0],bytes) or not 0<len(result[0])<=maximum:
            raise ValueError('Original repository returned an invalid or oversized response')
        return result[0]

    @staticmethod
    def _read_json(path):
        try:value=json.loads(read_regular(path,65536))
        except FileNotFoundError:return None
        except (ValueError,UnicodeError) as exc:raise ValueError('Repository cache metadata is damaged') from exc
        if not isinstance(value,dict):raise ValueError('Repository cache metadata is invalid')
        return value

    def _resolve(self,root,plan,revision):
        if revision:
            if not isinstance(revision,str) or not re.fullmatch(r'[0-9a-f]{40}',revision):raise ValueError('Invalid original source commit')
            return revision
        saved=self._read_json(root/'head.json')
        if saved:
            stamp=saved.get('fetched_at');sha=saved.get('revision')
            if (saved.get('repository')==plan['repository'] and isinstance(sha,str) and re.fullmatch(r'[0-9a-f]{40}',sha)
                    and type(stamp) in {int,float} and math.isfinite(stamp) and 0<=self.clock()-stamp<=300):return sha
        raw=self._fetch(plan['resolve_url'],512*1024,30,accept='application/vnd.github.sha' if plan['host']=='github.com' else None)
        try:
            revision=raw.strip().decode('ascii') if re.fullmatch(rb'[0-9a-f]{40}',raw.strip()) else None
            if revision is None:
                body=json.loads(raw);revision=body.get('sha' if plan['host']=='github.com' else 'id') if isinstance(body,dict) else None
        except (ValueError,UnicodeError,RecursionError) as exc:raise ValueError('Original repository commit could not be resolved') from exc
        if not isinstance(revision,str) or not re.fullmatch(r'[0-9a-f]{40}',revision):
            raise ValueError('Original repository did not return an immutable source commit')
        atomic_write(root/'head.json',json_bytes({'repository':plan['repository'],'revision':revision,'fetched_at':self.clock()}))
        return revision

    def _download(self,root,plan,revision):
        ref=root/'commits'/(revision+'.json');saved=self._read_json(ref)
        if saved:
            key=saved.get('archive_sha256');size=saved.get('bytes')
            if (saved.get('repository')!=plan['repository'] or saved.get('revision')!=revision
                    or not isinstance(key,str) or not re.fullmatch(r'[0-9a-f]{64}',key)
                    or type(size) is not int or not 0<size<=MAX_REPOSITORY_ZIP):
                raise ValueError('Repository download cache has an invalid identity')
            path=root/'archives'/(key+'.zip')
            try:payload=read_regular(path,MAX_REPOSITORY_ZIP)
            except FileNotFoundError:payload=None
            if payload is not None:
                if len(payload)!=size or digest(payload)!=key:raise ValueError('Repository download cache changed; it was not trusted')
                return path
        payload=self._fetch(repository_archive(plan,revision),MAX_REPOSITORY_ZIP,60)
        try:
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                if not 1<=len(archive.infolist())<=65536:raise ValueError('Original raw repository has too many entries')
        except zipfile.BadZipFile as exc:raise ValueError('Original repository did not return a source ZIP') from exc
        key=digest(payload);path=root/'archives'/(key+'.zip')
        atomic_write(path,payload,immutable=True)
        atomic_write(ref,json_bytes({'repository':plan['repository'],'revision':revision,
            'archive_sha256':key,'bytes':len(payload),'fetched_at':self.clock()}))
        return path

    @staticmethod
    def _remove_cache_file(path,root):
        # Only bounded private download files; never recurse or remove raw objects.
        if path.is_symlink() or path.resolve().parent!=root.resolve():raise ValueError('Unsafe repository cache cleanup path')
        path.unlink(missing_ok=True)

    def _prune(self,root,keep_revision):
        commits=root/'commits'
        refs=[path for path in commits.glob('*.json') if re.fullmatch(r'[0-9a-f]{40}',path.stem)]
        refs.sort(key=lambda path:path.stat().st_mtime_ns,reverse=True)
        kept={keep_revision}
        for path in refs:
            if len(kept)>=3:break
            kept.add(path.stem)
        protected=set()
        for path in refs:
            value=self._read_json(path)
            if path.stem in kept:
                if value and isinstance(value.get('archive_sha256'),str):protected.add(value['archive_sha256'])
            else:self._remove_cache_file(path,commits)
        archives=root/'archives'
        for path in archives.glob('*.zip'):
            if re.fullmatch(r'[0-9a-f]{64}',path.stem) and path.stem not in protected:self._remove_cache_file(path,archives)

    def _historical_module(self,plan,module,version):
        """A removed module may still exist at a recorded immutable parent.

        Inspect at most eight module commits/twelve candidate manifests within
        thirty seconds. A version-code match is a selection hint, not proof that
        these sources reproduce the installed binary. No repository substitution.
        """
        if plan['host']!='github.com' or not module.startswith('src/'):
            raise ValueError('The selected module is absent; provide its original source revision/project')
        deadline=time.monotonic()+30
        url='https://api.github.com/repos/'+plan['path']+'/commits?path='+quote(module,safe='')+'&per_page=8'
        rows=json.loads(self._fetch(url,512*1024,8))
        if not isinstance(rows,list) or len(rows)>8:raise ValueError('Original repository module history is unavailable')
        candidates=[]
        for row in rows:
            if not isinstance(row,dict):continue
            parents=row.get('parents') or []
            if not isinstance(parents,list):raise ValueError('Original repository module parents are malformed')
            for value in [row.get('sha'),*(p.get('sha') for p in parents[:2] if isinstance(p,dict))]:
                if isinstance(value,str) and re.fullmatch(r'[0-9a-f]{40}',value) and value not in candidates:candidates.append(value)
        fallback=None
        for revision in candidates[:12]:
            remaining=deadline-time.monotonic()
            if remaining<=0:break
            try:
                text=self._fetch('https://raw.githubusercontent.com/'+plan['path']+'/'+revision+'/'+module+'/build.gradle.kts',64*1024,min(6,remaining)).decode('utf-8')
            except ValueError as exc:
                if 'HTTP 404' in str(exc):continue
                raise
            fallback=fallback or revision
            codes=re.findall(r'\b(?:extVersionCode|versionCode)\s*=\s*([0-9]+)\b',text)
            libraries=re.findall(r'''\blibVersion\s*=\s*["']([0-9.]+)["']''',text)
            if codes and version.endswith('.'+codes[0]) and (not libraries or version==libraries[0]+'.'+codes[0]):return revision
        if fallback:return fallback
        raise ValueError('No retained module was found in the bounded original repository history; provide its earlier commit/project or choose Unavailable')

    def obtain(self,origin):
        if not isinstance(origin,dict) or origin.get('state')!='known':
            raise ValueError('Original raw code is required; provide it or choose Unavailable')
        plan=repository_plan(origin.get('repository'))
        info=metadata({'package':origin.get('package'),'version':origin.get('version'),'module':origin.get('module'),
            'ecosystem':origin.get('ecosystem'),'recipe':'gradle',
            'kind':'upstream' if origin.get('basis')=='known-repository' else 'provided',
            'origin':{'repository':plan['repository'],'basis':origin.get('basis','user-provided'),
                **({'index_url':origin['index_url']} if origin.get('index_url') else {})}})
        root=directory(self.root/digest(plan['repository'].encode()))
        with file_lock(root/'fetch.lock'):
            revision=self._resolve(root,plan,origin.get('revision',''))
            path=self._download(root,plan,revision)
            info['origin']['revision']=revision
            try:value=self.store.put_repository_file(path,info)
            except ValueError as exc:
                if origin.get('revision') or str(exc)!='No Kotlin/Java source files exist in the selected project module':raise
                revision=self._historical_module(plan,info['module'],info['version'])
                path=self._download(root,plan,revision)
                info.update(kind='provided')
                info['origin'].update(revision=revision,basis='repository-candidate')
                value=self.store.put_repository_file(path,info)
            self._prune(root,revision)
            return value
