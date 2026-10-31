"""Authenticated Web UI seams; domain modules own operations and persistence."""
import asyncio
import base64
from pathlib import Path
from fastapi import APIRouter,Depends,HTTPException,Query,Request
from fastapi.responses import FileResponse
from pydantic import BaseModel,ConfigDict,Field
from .async_utils import run_blocking
from .read_lane import read_lane
from .library_projection import page,canonical_snapshot
from . import serving_profiles,review_workbench
from .instance_backup import InstanceBackups,MAX_ARCHIVE
from .task_registry import task_registry
from . import metadata_fields,decision_preview,profile_upgrade,source_engine_contract
from .due_scheduler import DueScheduler
from .runtime_observability import metrics


class Model(BaseModel):model_config=ConfigDict(extra='forbid')
class Membership(Model):
    revision:int=Field(ge=0);in_library:bool;monitored:bool=False
class Monitor(Model):
    revision:int=Field(ge=0);monitored:bool
class Profile(Model):
    revision:int=Field(ge=0);profile:dict
class QueueAction(Model):
    revision:str=Field(max_length=64);action:str=Field(max_length=20)
    chapter_id:int|None=None;to:int|None=None
class Pairs(Model):pairs:list[list[int]]=Field(min_length=1,max_length=100)
class ReviewedPairs(Pairs):revision:str=Field(max_length=64)
class BackupCreate(Model):passphrase:str=Field(min_length=12,max_length=1024)
class Restore(Model):
    id:str=Field(pattern=r'^[0-9a-f]{32}$');revision:str=Field(max_length=64);confirmation:str=Field(max_length=100)
class MetadataPatch(Model):
    revision:int=Field(ge=0);fields:dict=Field(default_factory=dict);restore_id:int|None=Field(default=None,ge=1)
class PolicyPreview(Model):
    draft:dict;pairs:list[list[int]]=Field(min_length=1,max_length=32)
class PolicyApply(PolicyPreview):revision:str=Field(max_length=64)
class UpgradePolicy(Model):revision:int=Field(ge=0);policy:dict
class UpgradePreview(Model):chapter_id:int=Field(gt=0);candidate_id:int=Field(gt=0)
class UpgradeApply(UpgradePreview):revision:str=Field(max_length=64)
class IdentityDecision(Model):
    reference:str=Field(max_length=6000);decision:str=Field(pattern='^(confirmed|excluded|needs-review|reset|restore)$')
    revision:str=Field(pattern='^[0-9a-f]{64}$');note:str=Field(default='',max_length=1200);restore_id:int|None=Field(default=None,ge=1)
class GalleryPolicy(Model):revision:int=Field(ge=0);policy:dict
class CatalogueExport(Model):after:int=Field(default=0,ge=0);limit:int=Field(default=2000,ge=1,le=5000);include_decisions:bool=True
class CatalogueApply(Model):
    id:str=Field(pattern='^[0-9a-f]{32}$');revision:str=Field(pattern='^[0-9a-f]{64}$');include_decisions:bool=True
class CatalogueReview(Model):
    uid:str=Field(max_length=100);digest:str=Field(pattern='^[0-9a-f]{64}$');revision:str=Field(pattern='^[0-9a-f]{64}$')
    action:str=Field(pattern='^(apply|ignore|separate)$')
class CatalogueLink(Model):
    uid:str=Field(max_length=100);digest:str=Field(pattern='^[0-9a-f]{64}$');revision:str=Field(pattern='^[0-9a-f]{64}$');target:int=Field(gt=0)


def router(database,service,manager,settings,admin_guard,admin_lock,quiesce,restart):
    routes=APIRouter(prefix='/api/admin',dependencies=[Depends(admin_guard)])
    def backup_store():
        roots={'config':settings.settings_config_dir,'data':settings.data_dir,'db':settings.database_base_dir}
        if settings.source_engine_mode=='integrated':roots['engine']=Path('/home/suwayomi/.local/share/Tachidesk')
        return InstanceBackups(roots,(Path(__file__).resolve().parent.parent/'VERSION').read_text().strip())

    async def local(function,*args,**kwargs):
        def invoke():
            try:return function(*args,**kwargs)
            finally:database.close_current_thread()
        return await read_lane.run(invoke)

    async def mutation(function,*args,**kwargs):
        try:return await function(*args,**kwargs)
        except KeyError as exc:raise HTTPException(404,'Work no longer exists') from exc
        except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    async def domain(function,*args,write=False,**kwargs):
        def invoke():
            try:return function(*args,**kwargs)
            finally:database.close_current_thread()
        try:return await run_blocking(invoke) if write else await local(invoke)
        except KeyError as exc:raise HTTPException(404,'Work or record no longer exists') from exc
        except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.get('/portable-identities/resolve')
    async def portable_identity(reference:str=Query(min_length=1,max_length=6000)):
        from .portable_identity import resolve
        return await domain(resolve,database,service,reference)

    @routes.get('/identity-review')
    async def identity_list(search:str=Query('',max_length=200),offset:int=Query(0,ge=0),limit:int=Query(30,ge=1,le=50)):
        from .identity_review import listing
        return await domain(listing,database,search,offset,limit)

    @routes.get('/identity-review/{entry_id}')
    async def identity_read(entry_id:int):
        from .identity_review import read
        return await domain(read,database,service,entry_id)

    @routes.post('/identity-review/{entry_id}')
    async def identity_save(entry_id:int,body:IdentityDecision):
        from .identity_review import save
        async with admin_lock:return await domain(save,database,service,entry_id,body.reference,body.decision,body.revision,body.note,body.restore_id,write=True)

    @routes.get('/research-improvements/status')
    async def improvements_status():
        def build():
            with database.catalog() as c:
                pending=c.execute('SELECT COUNT(*) FROM crawl_pending').fetchone()[0]
                attempts=[dict(r) for r in c.execute('SELECT * FROM quality_attempts ORDER BY attempted_at DESC LIMIT 20')]
            return {'due':DueScheduler(database).snapshot(),'pending_feed_pages':pending,'quality_attempts':attempts,
                    'metrics':metrics.snapshot(),'engine':source_engine_contract.status(database)}
        return await domain(build)

    @routes.get('/gallery-policies/{source_id}')
    async def gallery_policy_read(source_id:str):
        from .gallery_policy import read
        return await domain(read,database,source_id)

    @routes.put('/gallery-policies/{source_id}')
    async def gallery_policy_save(source_id:str,body:GalleryPolicy):
        from .gallery_policy import save
        async with admin_lock:
            result=await domain(save,database,service,source_id,body.policy,body.revision,write=True)
            return {**result,'task':service.schedule_gallery_layout(source_id)}

    @routes.post('/gallery-policies/{source_id}/apply')
    async def gallery_policy_apply(source_id:str):
        from .gallery_policy import read
        async with admin_lock:
            await domain(read,database,source_id)
            return service.schedule_gallery_layout(source_id)

    def catalogue():
        from .catalog_exchange import CatalogueExchange
        return CatalogueExchange(database,service)

    @routes.get('/catalog-sharing/status')
    async def catalogue_status():return await domain(catalogue().status)

    @routes.post('/catalog-sharing/export')
    async def catalogue_export(body:CatalogueExport):
        async with admin_lock:return await domain(catalogue().export,after=body.after,limit=body.limit,include_decisions=body.include_decisions,write=True)

    @routes.post('/catalog-sharing/preview')
    async def catalogue_preview(request:Request):
        from .catalog_exchange_format import MAX_BYTES
        raw=bytearray()
        try:
            async with asyncio.timeout(45):
                async for chunk in request.stream():
                    if len(raw)+len(chunk)>MAX_BYTES:raise HTTPException(413,'Catalogue file exceeds 64 MiB; export smaller batches')
                    raw.extend(chunk)
        except TimeoutError as exc:raise HTTPException(408,'Catalogue upload timed out; select the file again') from exc
        payload=bytes(raw);raw.clear()
        async with admin_lock:return await domain(catalogue().preview,payload,write=True)

    @routes.get('/catalog-sharing/stages/{stage_id}')
    async def catalogue_stage(stage_id:str,offset:int=Query(0,ge=0),limit:int=Query(100,ge=1,le=200)):
        return await domain(catalogue().stage,stage_id,offset=offset,limit=limit)

    @routes.post('/catalog-sharing/apply')
    async def catalogue_apply(body:CatalogueApply):
        async with admin_lock:return await domain(catalogue().apply,body.id,body.revision,include_decisions=body.include_decisions,write=True)

    @routes.get('/catalog-sharing/decisions')
    async def catalogue_decisions(offset:int=Query(0,ge=0),limit:int=Query(30,ge=1,le=50)):
        return await domain(catalogue().decisions,offset=offset,limit=limit)

    @routes.get('/catalog-sharing/records')
    async def catalogue_records(offset:int=Query(0,ge=0),limit:int=Query(30,ge=1,le=50)):
        return await domain(catalogue().records,offset=offset,limit=limit)

    @routes.post('/catalog-sharing/decisions/review')
    async def catalogue_decision_review(body:CatalogueReview):
        if body.action not in {'apply','ignore'}:raise HTTPException(409,'Choose Apply or Keep local')
        async with admin_lock:
            current=await domain(catalogue().decision,body.uid,body.digest)
            if current['state'] in {'applied','ignored'}:return {'already_reviewed':True,'state':current['state']}
            if current['revision']!=body.revision:raise HTTPException(409,'Retained choice or local records changed; refresh before applying')
            if body.action=='apply' and not current['can_apply']:raise HTTPException(409,current['message'])
            try:return service.schedule_catalogue_choice(body.uid,body.digest,body.revision,action=body.action)
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.post('/catalog-sharing/records/review')
    async def catalogue_record_review(body:CatalogueReview):
        async with admin_lock:return await domain(catalogue().review_record,body.uid,body.digest,body.revision,action=body.action,write=True)

    @routes.post('/catalog-sharing/records/link')
    async def catalogue_record_link(body:CatalogueLink):
        async with admin_lock:
            choice=await domain(catalogue().prepare_link,body.uid,body.digest,body.revision,body.target,write=True)
            return service.schedule_catalogue_choice(choice['uid'],choice['digest'],choice['revision'])

    @routes.post('/engine/compatibility')
    async def engine_compatibility(target:str=Query('source',pattern='^(source|main)$')):
        from .suwayomi import source_host
        async with admin_lock:
            try:return await source_engine_contract.check(database,source_host if target=='source' else manager.client,target)
            except Exception as exc:raise HTTPException(409,'Engine compatibility check incomplete: '+type(exc).__name__) from exc

    @routes.get('/metadata-fields/{entry_id}')
    async def metadata_read(entry_id:int):return await domain(metadata_fields.read,database,entry_id)

    @routes.put('/metadata-fields/{entry_id}')
    async def metadata_save(entry_id:int,body:MetadataPatch):
        async with admin_lock:return await domain(metadata_fields.save,database,service,entry_id,body.fields,body.revision,restore_id=body.restore_id,write=True)

    @routes.post('/policy-simulator/preview')
    async def policy_preview(body:PolicyPreview):
        async with admin_lock:return await domain(decision_preview.preview,database,service,body.draft,body.pairs)

    @routes.get('/policy-simulator/options')
    async def policy_options():
        from . import matching_policies
        def build():
            with database.catalog() as c:
                return {'sources':[dict(r) for r in c.execute('SELECT source_id,name,lang FROM source_config ORDER BY name,lang')],
                        'groups':[dict(r) for r in c.execute('SELECT id,name FROM source_groups ORDER BY name')],
                        'rules':[{'scope':k[0],'subject':k[1],'direction':k[2],'rule':v} for k,v in matching_policies.rules(database).items()],
                        'presets':matching_policies.presets()}
        return await domain(build)

    @routes.post('/policy-simulator/apply')
    async def policy_apply(body:PolicyApply):
        async with admin_lock:return await domain(decision_preview.apply,database,service,body.draft,body.pairs,body.revision,write=True)

    @routes.get('/serving-upgrades/{entry_id}')
    async def upgrade_read(entry_id:int):return await domain(profile_upgrade.read,database,entry_id)

    @routes.put('/serving-upgrades/{entry_id}')
    async def upgrade_save(entry_id:int,body:UpgradePolicy):
        async with admin_lock:return await domain(profile_upgrade.save,database,service,entry_id,body.policy,body.revision,write=True)

    @routes.post('/serving-upgrades/{entry_id}/preview')
    async def upgrade_preview(entry_id:int,body:UpgradePreview):
        async with admin_lock:return await domain(profile_upgrade.preview,database,service,entry_id,body.chapter_id,body.candidate_id)

    @routes.post('/serving-upgrades/{entry_id}/apply')
    async def upgrade_apply(entry_id:int,body:UpgradeApply):
        async with admin_lock:return await domain(profile_upgrade.apply,database,service,entry_id,body.chapter_id,body.candidate_id,body.revision,write=True)

    @routes.get('/library')
    async def library(search:str=Query('',max_length=200),source:str=Query('',max_length=200),scope:str='all',sort:str='updated',
       publication:str='any',title_state:str='any',match_status:str='any',source_count:str='',record_count:str='',cursor:str=Query('',max_length=2048),limit:int=Query(60,ge=1,le=120)):
        snapshot=service.cached_subbed_payload();service.schedule_status_aux_refresh(delay=0)
        duplicates={int(eid) for group in (service._duplicate_snapshot[1] if service._duplicate_snapshot else []) for eid in group.get('entry_ids',[])}
        def build():
            return page(database,canonical_snapshot(service,snapshot),search=search,source=source,scope=scope,sort=sort,publication=publication,title_state=title_state,
                                match_status=match_status,cursor=cursor,limit=limit,duplicates=duplicates,source_count=source_count,record_count=record_count)
        try:return await local(build)
        except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.get('/library/{entry_id}/controls')
    async def controls(entry_id:int):
        result=await local(manager.tracking,entry_id)
        snapshot=service.cached_subbed_payload()
        result['in_library']=entry_id in service.canonical_entry_ids(snapshot.get('entry_ids',[])) if snapshot.get('ok') is True else None
        return result

    @routes.put('/library/{entry_id}/membership')
    async def membership(entry_id:int,body:Membership):
        async with admin_lock:
            return await mutation(manager.membership,entry_id,body.in_library,body.monitored,body.revision)

    @routes.put('/library/{entry_id}/monitoring')
    async def monitor(entry_id:int,body:Monitor):
        async with admin_lock:
            try:
                manga=await manager.exact_manga(entry_id)
                if body.monitored and not manga['inLibrary']:raise ValueError('Add the work to your library before monitoring')
                return await run_blocking(manager.save_tracking,entry_id,expected=body.revision,monitored=body.monitored,main_id=manga['id'])
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.post('/library/{entry_id}/observe')
    async def observe(entry_id:int,refresh:bool=False):
        async with admin_lock:
            async with manager.lock:return await mutation(manager.observe,entry_id,refresh=refresh)

    @routes.post('/library/{entry_id}/download')
    async def download(entry_id:int):
        async with admin_lock:return await mutation(manager.enqueue_missing,entry_id)

    @routes.get('/downloads')
    async def downloads():return await mutation(manager.queue)

    @routes.get('/activity/history')
    async def activity_history(offset:int=Query(0,ge=0,le=300),limit:int=Query(50,ge=1,le=100)):
        return task_registry.history(offset=offset,limit=limit)

    @routes.post('/downloads')
    async def queue_action(body:QueueAction):
        async with admin_lock:return await mutation(manager.queue_action,body.action,body.revision,body.chapter_id,body.to)

    @routes.get('/profiles/{entry_id}')
    async def profile(entry_id:int):
        try:return await local(serving_profiles.read,database,entry_id)
        except KeyError as exc:raise HTTPException(404,'Work no longer exists') from exc

    @routes.put('/profiles/{entry_id}')
    async def profile_save(entry_id:int,body:Profile):
        async with admin_lock:
            try:return await run_blocking(serving_profiles.save,database,service,entry_id,body.profile,body.revision)
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.get('/review-workbench')
    async def audit(entry_id:int|None=None):return await local(review_workbench.audit,database,service,entry_id)

    @routes.post('/review-workbench/preview')
    async def review_preview(body:Pairs):
        try:
            async with admin_lock:return await local(review_workbench.preview,database,service,body.pairs)
        except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.post('/review-workbench/apply')
    async def review_apply(body:ReviewedPairs):
        async with admin_lock:
            try:return await run_blocking(review_workbench.apply,database,service,body.pairs,body.revision)
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.post('/review-workbench/{identifier}/undo')
    async def review_undo(identifier:str):
        async with admin_lock:
            try:return await run_blocking(review_workbench.undo,database,service,identifier)
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.get('/instance/backups')
    async def backups():return await local(lambda:{'items':backup_store().listing(),'pending_restore':backup_store().pending_path.exists()})

    @routes.post('/instance/backups')
    async def create_backup(body:BackupCreate):
        try:
            async with quiesce():
                return await run_blocking(database.quiescent_files,lambda:backup_store().create(body.passphrase))
        except ValueError as exc:raise HTTPException(409,str(exc)) from exc

    @routes.get('/instance/backups/{identifier}/download')
    async def export_backup(identifier:str):
        try:return FileResponse(backup_store().export(identifier),filename='autosourcarr-'+identifier+'.ascbackup',media_type='application/octet-stream',headers={'Cache-Control':'no-store'})
        except ValueError as exc:raise HTTPException(404,str(exc)) from exc

    @routes.delete('/instance/backups/{identifier}')
    async def delete_backup(identifier:str):
        async with admin_lock:
            try:return await run_blocking(backup_store().delete,identifier)
            except ValueError as exc:raise HTTPException(404,str(exc)) from exc

    @routes.post('/instance/restore/preview')
    async def restore_preview(request:Request):
        raw=bytearray()
        async for chunk in request.stream():
            if len(raw)+len(chunk)>MAX_ARCHIVE+64:raise HTTPException(413,'Encrypted backup exceeds 128 MiB')
            raw.extend(chunk)
        try:
            password=base64.b64decode(request.headers.get('X-Backup-Passphrase',''),validate=True).decode('utf-8')
            async with admin_lock:return await run_blocking(backup_store().stage,bytes(raw),password)
        except (ValueError,UnicodeError) as exc:raise HTTPException(400,str(exc)) from exc

    @routes.post('/instance/restore/apply')
    async def restore_apply(body:Restore):
        async with admin_lock:
            try:result=await run_blocking(backup_store().arm,body.id,body.revision,body.confirmation)
            except ValueError as exc:raise HTTPException(409,str(exc)) from exc
        restart()
        return {**result,'restart_scheduled':True}

    @routes.post('/tasks/{task_id}/retry-safe')
    async def retry_task(task_id:str):
        with task_registry._lock:
            row=task_registry._tasks.get(task_id)
            if not row or row.cancel_requested or not row.interrupted or row.kind!='library-monitor':
                raise HTTPException(409,'This task cannot be safely replayed. Review its original action.')
            try:entry_id=int(row.context)
            except ValueError as exc:raise HTTPException(409,'Task identity is unavailable') from exc
        async with admin_lock:
            result=await mutation(manager.observe,entry_id)
            return {'observed':result,'note':'Read-only observation retried; no previous mutation was replayed'}
    return routes
