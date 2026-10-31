"""Read-only Tasks projection of bounded, observed private repair status.

The existing helper health check supplies snapshots. Reading Tasks never starts a
helper, runs a sandbox probe, reads a source profile or admits repair work.
"""
from datetime import datetime, timezone
import threading
import time

from .repair_evidence import safe_isolation, safe_job
from .support_logs import support_logs

TERMINAL = frozenset(('ready', 'installed', 'rolled-back', 'blocked', 'failed', 'cancelled', 'interrupted', 'discarded','raws-retained','confirmed','reported-not-working','reverted'))
FAILED = frozenset(('blocked', 'failed', 'cancelled', 'interrupted', 'recovery-needed', 'rollback-failed','reported-not-working'))


def iso(stamp):
    try:
        return datetime.fromtimestamp(stamp, timezone.utc).isoformat()
    except (ValueError, TypeError, OSError, OverflowError):
        return ''


def isolation_blocker(state):
    """Unknown/legacy status is still checked by the worker's execution gate."""
    value = state.get('isolation') if isinstance(state, dict) else None
    if isinstance(value, dict) and value.get('available') is False and value.get('reason') not in (None, 'not-checked'):
        return str(value.get('message') or 'Repair isolation is unavailable')
    return None


class RepairTasks:
    def __init__(self, privacy):
        self.privacy = privacy
        self.lock = threading.RLock()
        self.jobs = {}
        self.hidden = {}
        self.reachable = None
        self.observed = 0
        self.captured_at = ''
        self.isolation = None

    def observe(self, value):
        if not isinstance(value, dict):
            return
        with self.lock:
            rows = value.get('jobs')
            full = isinstance(rows, list)
            previous_jobs = self.jobs
            if full:
                self.jobs = {}
            if not full:
                rows = [value]
                if 'id' not in value:
                    if 'isolation' in value:
                        self.isolation = safe_isolation(value['isolation'], self.privacy)
                    return
            for row in rows[:100]:
                item = safe_job(row, self.privacy)
                if item:
                    previous = previous_jobs.get(item['id'])
                    if not previous or previous.get('updated_at', 0) <= item.get('updated_at', 0):
                        self.jobs[item['id']] = item
                    else:
                        self.jobs[item['id']] = previous
            captured = value.get('captured_at')
            if full and type(captured) in (int, float):
                for key, item in previous_jobs.items():
                    if item.get('created_at', 0) > captured and key not in self.jobs:
                        self.jobs[key] = item
            self.jobs = dict(sorted(self.jobs.items(), key=lambda pair: pair[1].get('created_at', 0), reverse=True)[:100])
            self.hidden = {key: sig for key, sig in self.hidden.items() if key in self.jobs}
            if 'isolation' in value:
                self.isolation = safe_isolation(value['isolation'], self.privacy)
            if full:
                self.reachable = True
                self.observed = time.monotonic()
                self.captured_at = datetime.now(timezone.utc).isoformat()

    def unavailable(self):
        with self.lock:
            self.reachable = False

    def needs_refresh(self):
        with self.lock:
            return time.monotonic() - self.observed >= 5

    def _items(self, operation):
        from .repair_workflow import repair_workflow
        histories=repair_workflow.history.summaries() if repair_workflow.history else {}
        stale = self.reachable is not True or time.monotonic() - self.observed > 20
        items = []
        jobs=list(self.jobs.values())
        for pkg,history in histories.items():
            current=history.get('current') or {};identifier=current.get('job_id')
            if identifier and identifier not in self.jobs:
                jobs.append({'id':identifier,'pkg_name':pkg,'state':'installed','version':'unknown','raw_first':True,
                    'created_at':current.get('installed_at',0),'updated_at':current.get('installed_at',0),'origin':'manual'})
        for job in jobs:
            stage, message = job['state'], job.get('message', '')
            source_history=histories.get(job.get('pkg_name')) or {}
            current=source_history.get('current') or {}
            if source_history.get('jobs',{}).get(job['id'])=='reverted':
                stage='reverted';message='Repair reverted. The prior binary, code reference and working confirmation were restored.'
            if current.get('job_id')==job['id']:
                stage={'confirmed':'confirmed','reported-not-working':'reported-not-working'}.get(current.get('status'),'installed')
                message={'confirmed':'User confirmed this installed source works.',
                         'reported-not-working':'Reported as not working. Retry starts from this attempt’s retained code.'}.get(stage,'Repair installed; confirm Fixed or report the remaining problem.')
            changing = operation.get('id') == job['id']
            if changing and stage not in {'confirmed','reported-not-working','reverted'} and operation.get('state') in {'queued', 'installing', 'installed', 'recovered', 'rolled-back', *FAILED}:
                stage = operation['state']
                if stage == 'queued':
                    stage = 'install-queued'
                if stage == 'recovered':
                    stage = 'installed'
                message = self.privacy.text(str(operation.get('message') or message))[:1200]
            state = 'failed' if stage in FAILED else 'completed' if stage in TERMINAL else 'running'
            signature = (stage, job.get('updated_at'), job.get('version'))
            if self.hidden.get(job['id']) == signature:
                continue
            activity = 'queued' if stage in {'queued', 'install-queued'} else 'running'
            if stale and state == 'running':
                activity = 'waiting'
            if stage in {'unknown','awaiting-raws'}:
                activity = 'waiting'
            item = {'id': 'source-repair:' + job['id'], 'kind': 'source-repair', 'repair_id': job['id'],
                    'name': 'Repair source', 'context': job.get('pkg_name', ''), 'state': state,
                    'repair_state': stage, 'repair_version': job.get('version', 'unknown'),
                    'repair_reviewable': job['state'] == 'ready' and stage not in {'install-queued', 'installing', 'installed'},
                    'origin': job.get('origin', ''), 'message': message, 'activity': activity,
                    'created_at': iso(job.get('created_at')), 'updated_at': iso(job.get('updated_at')),
                    'finished_at': iso(job.get('updated_at')) if state != 'running' else None,
                    'cancellable': state == 'running' and stage != 'unknown' and not changing and not stale,
                    'stale': stale, 'current': 0, 'total': 0}
            item['repair_inputs_available']=bool(job.get('baseline_sha256') and job.get('source_apk_sha256'))
            item['address_migration_count']=job.get('address_migration_count',0)
            item['raw_first']=job.get('raw_first') is True
            item['repair_raw_available']=job.get('raw_code_available') is True
            if stage=='awaiting-raws':item['raw_package']=job.get('pkg_name')
            if current.get('job_id')==job['id']:
                item.update(repair_history_revision=source_history['revision'],installed_sha256=current['binary_sha256'],
                            repair_can_revert=source_history['can_revert'],repair_is_current=True,
                            repair_can_feedback=not bool(source_history['pending']),
                            repair_working_at=source_history.get('working_at'),repair_installed_at=current.get('installed_at'))
            items.append(item)
        return items

    def merge(self, snapshot, operation=None):
        with self.lock:
            from .source_raws import source_raws
            from .repair_workflow import repair_workflow
            items = list(snapshot.get('items', [])) + self._items(operation or {})
            items += repair_workflow.pending_items(set(self.jobs))
            waiting={item.get('context') for item in items if item.get('repair_state')=='awaiting-raws'}
            items += [item for item in source_raws.pending_items() if item['raw_package'] not in waiting]
            running = [item for item in items if item['state'] == 'running']
            return {**snapshot, 'items': items, 'running': len(running),
                    'active': sum(item.get('activity', 'running') == 'running' for item in running),
                    'queued': sum(item.get('activity') == 'queued' for item in running),
                    'waiting': sum(item.get('activity') == 'waiting' for item in running),
                    'completed': sum(item['state'] == 'completed' for item in items),
                    'failed': sum(item['state'] == 'failed' for item in items),
                    'source_repairs': {'reachable': self.reachable, 'captured_at': self.captured_at,
                                       'stale': self.reachable is not True or time.monotonic() - self.observed > 20,
                                       'isolation': self.isolation}}

    def clear_history(self, operation=None):
        with self.lock:
            result = {'cleared': 0, 'completed': 0, 'failed': 0, 'kept_for_review': 0}
            for item in self._items(operation or {}):
                if item['repair_reviewable'] or item.get('repair_is_current'):
                    result['kept_for_review'] += 1
                elif item['state'] != 'running':
                    job = self.jobs[item['repair_id']]
                    self.hidden[job['id']] = (item['repair_state'], job.get('updated_at'), job.get('version'))
                    result['cleared'] += 1
                    result[item['state']] += 1
            return result

    def diagnostic_snapshot(self, snapshot, operation=None):
        """Keep current work and recent repairs within the export's row budget."""
        result = self.merge(snapshot, operation)
        result['items'] = sorted(result['items'], key=lambda item: (
            item.get('state') == 'running',
            str(item.get('updated_at') or item.get('created_at') or '')), reverse=True)
        return result


repair_tasks = RepairTasks(support_logs.privacy)


def tasks_router(admin_guard, registry=None):
    from fastapi import APIRouter, Depends, HTTPException
    from .config import settings
    from .source_maintenance import source_maintenance, worker
    from .task_registry import task_registry
    import asyncio
    import re
    registry = registry or (lambda: task_registry)
    router = APIRouter(prefix='/api/admin/tasks', dependencies=[Depends(admin_guard)])

    @router.get('')
    async def snapshot():
        # Integrated status is supplied by the helper's existing health check.
        # Legacy remote helpers have no owned health loop; bound their GET here.
        if settings.app_services_mode != 'integrated' and settings.repair_worker_url and repair_tasks.needs_refresh():
            try:
                await asyncio.wait_for(worker('GET', '/status'), 2)
            except (HTTPException, TimeoutError):
                repair_tasks.unavailable()
        return repair_tasks.merge(registry().snapshot(), source_maintenance.operation)

    @router.post('/clear-history')
    def clear_history():
        local = registry().clear_history()
        repairs = repair_tasks.clear_history(source_maintenance.operation)
        return {'ok': True, **{key: local[key] + repairs[key] for key in local},
                'kept_for_review': repairs['kept_for_review']}

    @router.post('/{task_id}/cancel')
    async def cancel(task_id: str):
        if task_id.startswith('source-repair:'):
            identifier = task_id.removeprefix('source-repair:')
            if not re.fullmatch('[0-9a-f]{32}', identifier):
                raise HTTPException(404, 'Invalid repair task identifier')
            result = await worker('POST', '/jobs/' + identifier + '/cancel')
            if result.get('requested') is not True:
                raise HTTPException(409, 'Repair is not running; refresh its status')
            return {'ok': True, 'task_id': task_id, 'cancel_requested': True}
        if not registry().request_cancel(task_id):
            raise HTTPException(409, 'Task is not running or cannot be cancelled safely')
        return {'ok': True, 'task_id': task_id, 'state': 'failed', 'error': 'Cancelled by user'}

    return router
