"""Private, per-package code/binary history; installation is not confirmation.

The controller pairs plans with its existing stopped-engine promotion journal.
This module never installs code, opens a source profile or shares instance state.
Every mutating action is bound to the observed binary and journal revision.
"""
import copy
import hashlib
import json
import math
import re
import threading
import time

from bundled.source_tools.archive import HASH, package_name
from bundled.source_tools.fs import atomic_write, directory, file_lock, json_bytes, read_regular

MAX_HISTORY=512*1024
JOB=r'[0-9a-f]{32}'
STATUSES={'unknown','unconfirmed','confirmed','reported-not-working'}
JOB_STATES={'preparing','installed','confirmed','reported-not-working','reverted',
            'failed','blocked','cancelled','interrupted','discarded'}
REF_KEYS={'binary_sha256','raw_id','version','entrypoint','extension_lib','source_ids','identity_sha256','job_id'}


class HistoryConflict(ValueError):
    pass


def _job(value):
    if not isinstance(value,str) or not re.fullmatch(JOB,value):raise HistoryConflict('Invalid repair job identity')
    return value


def _hash(value, *, optional=False):
    if optional and value is None:return None
    if not isinstance(value,str) or not re.fullmatch(HASH,value):raise HistoryConflict('Invalid repair fingerprint')
    return value


def _reference(value, *, internal=False, candidate=False):
    if value is None:return None
    allowed=REF_KEYS|({'status','installed_at','confirmed_at'} if internal else set())
    if not isinstance(value,dict) or set(value)-allowed:raise HistoryConflict('Unsupported source-version reference')
    result={key:value.get(key) for key in REF_KEYS}
    result['binary_sha256']=_hash(result['binary_sha256']);_hash(result['raw_id'],optional=not candidate)
    if not isinstance(result['version'],str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}',result['version']):
        raise HistoryConflict('Source version metadata is missing')
    if result['job_id'] is not None:_job(result['job_id'])
    if result['entrypoint'] is not None and (not isinstance(result['entrypoint'],str) or not re.fullmatch(r'[A-Za-z_$][A-Za-z0-9_.$]{0,239}',result['entrypoint'])):
        raise HistoryConflict('Invalid source loading-class reference')
    if result['extension_lib'] is not None and (not isinstance(result['extension_lib'],str) or not re.fullmatch(r'[0-9]{1,3}\.[0-9]{1,3}',result['extension_lib'])):
        raise HistoryConflict('Invalid source library reference')
    ids=result['source_ids']
    if not isinstance(ids,list) or len(ids)>200 or any(not isinstance(sid,str) or not re.fullmatch(r'-?[0-9]{1,20}',sid) for sid in ids):
        raise HistoryConflict('Invalid source identities in repair history')
    result['source_ids']=sorted(set(ids));_hash(result['identity_sha256'],optional=not candidate)
    if candidate and (not result['entrypoint'] or not result['extension_lib'] or not ids):
        raise HistoryConflict('Validated source loading identity is required before installation')
    if internal:
        status=value.get('status')
        if not isinstance(status,str) or status not in STATUSES:raise HistoryConflict('Invalid source confirmation state')
        result['status']=status
        for key in ('installed_at','confirmed_at'):
            if key in value:
                stamp=value[key]
                if type(stamp) not in {int,float} or not math.isfinite(stamp) or stamp<0:raise HistoryConflict('Invalid repair-history timestamp')
                result[key]=stamp
    return result


def _binary(value):return value.get('binary_sha256') if value else None


def _compatible(left,right):
    return bool(left and right and left.get('entrypoint') and left.get('extension_lib')
        and left.get('identity_sha256') and all(left.get(key)==right.get(key)
        for key in ('entrypoint','extension_lib','source_ids','identity_sha256')))


class RepairHistory:
    def __init__(self, root, *, clock=time.time):
        self.root=directory(root);self.clock=clock
        self._cache={};self._cache_lock=threading.RLock()

    def _path(self,pkg):
        package_name(pkg)
        return self.root/(hashlib.sha256(pkg.encode()).hexdigest()+'.json')

    @staticmethod
    def _initial(pkg):
        return {'schema':1,'package':pkg,'revision':0,'epoch':0,'current':None,'working':None,
                'revert':None,'pending':None,'jobs':{},'last_plan':None,'last_recovery':None}

    def _validate(self,state,pkg):
        if (not isinstance(state,dict) or set(state)!=set(self._initial(pkg))
                or type(state['schema']) is not int or state['schema']!=1 or state['package']!=pkg):
            raise HistoryConflict('Source repair history has an unsupported schema or package')
        for key in ('revision','epoch'):
            if type(state[key]) is not int or not 0<=state[key]<2**53-1:raise HistoryConflict('Invalid source-history revision')
        for key in ('current','working'):
            _reference(state[key],internal=True)
        if state['working'] and state['working']['status']!='confirmed':raise HistoryConflict('Working history lacks a user confirmation')
        if state['pending'] is not None:_job(state['pending'])
        if state['last_plan'] is not None:_hash(state['last_plan'])
        if state['last_recovery'] is not None:_hash(state['last_recovery'])
        if state['revert'] is not None:
            revert=state['revert']
            if not isinstance(revert,dict) or set(revert)!={'job_id','target','working_before','epoch'}:
                raise HistoryConflict('Invalid source revert receipt')
            _job(revert['job_id']);_reference(revert['target'],internal=True);_reference(revert['working_before'],internal=True)
            if type(revert['epoch']) is not int or not 0<=revert['epoch']<=state['epoch']:raise HistoryConflict('Invalid revert epoch')
        if not isinstance(state['jobs'],dict) or len(state['jobs'])>64:raise HistoryConflict('Repair history exceeds its bounded job index')
        for identifier,row in state['jobs'].items():
            _job(identifier)
            if not isinstance(row,dict) or set(row)-{'state','epoch','baseline_sha256','created_at','finished_at','feedback'}:
                raise HistoryConflict('Invalid repair history job')
            if not isinstance(row.get('state'),str) or row['state'] not in JOB_STATES:raise HistoryConflict('Invalid repair history job state')
            if type(row.get('epoch')) is not int or not 0<=row['epoch']<=state['epoch']:raise HistoryConflict('Invalid repair history job epoch')
            _hash(row.get('baseline_sha256'),optional=True)
            if not isinstance(row.get('feedback',''),str) or len(row.get('feedback',''))>6000:raise HistoryConflict('Repair feedback exceeds its limit')
            for key in ('created_at','finished_at'):
                if key in row and (type(row[key]) not in {int,float} or not math.isfinite(row[key]) or row[key]<0):
                    raise HistoryConflict('Invalid history job timestamp')
        if state['pending'] and state['pending'] not in state['jobs']:raise HistoryConflict('Missing pending repair receipt')
        return state

    def _read(self,pkg):
        path=self._path(pkg)
        try:state=json.loads(read_regular(path,MAX_HISTORY))
        except FileNotFoundError:state=self._initial(pkg)
        except (ValueError,UnicodeError) as exc:raise HistoryConflict('Source repair history is damaged; it was not reset') from exc
        self._validate(state,pkg)
        return state

    def _cache_state(self,state):
        with self._cache_lock:self._cache[state['package']]=copy.deepcopy(state)

    def _save(self,state):
        self._validate(state,state['package']);value=json_bytes(state)
        if len(value)>MAX_HISTORY:raise HistoryConflict('Source history exceeds its persistence limit')
        atomic_write(self._path(state['package']),value)
        self._cache_state(state)
        return copy.deepcopy(state)

    def snapshot(self,pkg):
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg);self._cache_state(state);return copy.deepcopy(state)

    def cached(self):
        # No filesystem/native source work in task/health rendering paths.
        with self._cache_lock:return copy.deepcopy(self._cache)

    def load(self):
        for path in sorted(self.root.glob('*.json'))[:4096]:
            value=json.loads(read_regular(path,MAX_HISTORY));pkg=value.get('package') if isinstance(value,dict) else None
            if self._path(pkg)!=path:raise HistoryConflict('Repair-history file/package changed')
            self.snapshot(pkg)

    def summaries(self,*,include_source_ids=False):
        with self._cache_lock:
            return {pkg:{'revision':state['revision'],'pending':state['pending'],
                'current':{key:copy.deepcopy(state['current'].get(key)) for key in ('job_id','binary_sha256','raw_id','version','status','installed_at','confirmed_at',*(['source_ids'] if include_source_ids else []))} if state['current'] else None,
                'working_at':state['working'].get('confirmed_at') if state['working'] else None,
                'can_revert':bool(state['revert'] and not state['pending']),
                'jobs':{key:row['state'] for key,row in state['jobs'].items()}}
                for pkg,state in self._cache.items()}

    @staticmethod
    def _revision(state,expected):
        if type(expected) is not int or expected!=state['revision']:raise HistoryConflict('Repair history changed; refresh before acting')

    @staticmethod
    def _active(state,job,observed):
        _job(job);_hash(observed,optional=True)
        if state['pending']:raise HistoryConflict('A new repair is active; wait or cancel it before changing the installed version')
        if not state['current'] or state['current'].get('job_id')!=job or _binary(state['current'])!=observed:
            raise HistoryConflict('This is not the currently installed repair; refresh its version')

    def admit(self,pkg,job,baseline):
        _job(job);baseline=_reference(baseline)
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg)
            if job in state['jobs']:
                if state['jobs'][job]['baseline_sha256']!=_binary(baseline):raise HistoryConflict('Repair request identity was reused for different input')
                return copy.deepcopy(state)
            if state['pending']:raise HistoryConflict('This source already has an owned repair; wait or cancel it first')
            if _binary(state['current'])!=_binary(baseline):
                # An ordinary source install/update is an observed change, not a
                # confirmation of working code or permission to revert old jobs.
                state['current']={**baseline,'status':'unknown'} if baseline else None
                state['revert']=None
            if state['current'] and state['current']['status']=='confirmed':
                state['revert']=None  # A later fix closes the earlier Fixed→Revert window.
            state['epoch']+=1;state['revision']+=1;state['pending']=job
            state['jobs'][job]={'state':'preparing','epoch':state['epoch'],'baseline_sha256':_binary(baseline),
                                'created_at':self.clock(),'feedback':''}
            keep={job,state['current'].get('job_id') if state['current'] else None,
                  state['working'].get('job_id') if state['working'] else None,
                  state['revert']['job_id'] if state['revert'] else None}
            for identifier in list(state['jobs']):
                if len(state['jobs'])<=64:break
                if identifier not in keep:del state['jobs'][identifier]
            return self._save(state)

    def observe(self,pkg,reference):
        reference=_reference(reference)
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg)
            if _binary(state['current'])==_binary(reference):return state
            state['current']={**reference,'status':'unknown'} if reference else None
            state['revert']=None;state['revision']+=1
            return self._save(state)

    def _plan(self,before,after,action,job):
        after['revision']=before['revision']+1
        value={'schema':1,'package':before['package'],'action':action,'job_id':job,
               'before':copy.deepcopy(before),'after':after,'target':copy.deepcopy(after['current'])}
        value['id']=hashlib.sha256(json_bytes(value)).hexdigest()
        return value

    def _checked_plan(self,plan):
        if not isinstance(plan,dict) or set(plan)!={'schema','package','action','job_id','before','after','target','id'}:
            raise HistoryConflict('Invalid source promotion plan')
        raw={key:value for key,value in plan.items() if key!='id'}
        if hashlib.sha256(json_bytes(raw)).hexdigest()!=plan['id']:raise HistoryConflict('Source promotion plan changed')
        package_name(plan['package']);_job(plan['job_id'])
        if type(plan['schema']) is not int or plan['schema']!=1 or not isinstance(plan['action'],str) or plan['action'] not in {'install','revert'}:raise HistoryConflict('Unsupported source promotion plan')
        self._validate(plan['before'],plan['package']);self._validate(plan['after'],plan['package'])
        if plan['after']['revision']!=plan['before']['revision']+1 or plan['target']!=plan['after']['current']:
            raise HistoryConflict('Source promotion plan does not match its version target')
        return plan

    def plan_install(self,pkg,job,candidate,*,expected_revision):
        _job(job);candidate=_reference(candidate,candidate=True)
        if candidate is None or candidate.get('job_id') not in {None,job}:raise HistoryConflict('Candidate does not belong to this repair')
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg);self._revision(state,expected_revision)
            if state['pending']!=job:raise HistoryConflict('Repair admission is no longer current')
            current=state['current'];anchor=copy.deepcopy(current)
            if current and any(current.get(key) and current[key]!=candidate[key]
                    for key in ('entrypoint','extension_lib','source_ids','identity_sha256')):
                raise HistoryConflict('Source loading or source identities changed; this is not a compatible repair')
            if current and state['revert'] and state['revert']['job_id']==current.get('job_id'):
                anchor=copy.deepcopy(state['revert']['target'])
            elif current and state['working'] and (_binary(current)==_binary(state['working']) or _compatible(state['working'],candidate)):
                anchor=copy.deepcopy(state['working'])
            after=copy.deepcopy(state)
            after['current']={**candidate,'job_id':job,'status':'unconfirmed','installed_at':self.clock()}
            after['pending']=None;after['last_recovery']=None
            after['revert']={'job_id':job,'target':anchor,'working_before':copy.deepcopy(state['working']),'epoch':state['epoch']}
            after['jobs'][job]['state']='installed';after['jobs'][job]['finished_at']=self.clock()
            return self._plan(state,after,'install',job)

    def bind_baseline_code(self,pkg,job,raw_id):
        _job(job);_hash(raw_id)
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg)
            if state['pending']!=job:raise HistoryConflict('Repair admission is no longer current')
            if state['current'] and not state['current']['raw_id']:
                state['current']['raw_id']=raw_id;state['revision']+=1
                return self._save(state)
            return state

    def commit(self,plan,*,observed_sha256):
        plan=self._checked_plan(plan);_hash(observed_sha256,optional=True)
        if observed_sha256!=_binary(plan['target']):raise HistoryConflict('Installed binary does not match the source promotion plan')
        path=self._path(plan['package'])
        with file_lock(path.with_suffix('.lock')):
            state=self._read(plan['package'])
            if state['last_plan']==plan['id'] and _binary(state['current'])==observed_sha256:return copy.deepcopy(state)
            if state!=plan['before']:raise HistoryConflict('Source history changed before promotion completed')
            after=copy.deepcopy(plan['after']);after['last_plan']=plan['id']
            return self._save(after)

    def confirm(self,pkg,job,*,expected_revision,observed_sha256):
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg);self._revision(state,expected_revision);self._active(state,job,observed_sha256)
            if not state['current']['raw_id']:raise HistoryConflict('Working source code must be retained before marking Fixed')
            state['current'].update(status='confirmed',confirmed_at=self.clock())
            state['working']=copy.deepcopy(state['current']);state['jobs'][job]['state']='confirmed'
            state['revision']+=1
            return self._save(state)

    def report_failed(self,pkg,job,*,expected_revision,observed_sha256,feedback=''):
        if not isinstance(feedback,str) or len(feedback)>6000:raise HistoryConflict('Describe the remaining problem in at most6000 characters')
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg);self._revision(state,expected_revision);self._active(state,job,observed_sha256)
            state['current']['status']='reported-not-working'
            state['jobs'][job].update(state='reported-not-working',feedback=feedback)
            if _binary(state['working'])==observed_sha256:
                state['working']=copy.deepcopy(state['revert']['working_before']) if state['revert'] else None
            state['revision']+=1
            return self._save(state)

    def plan_revert(self,pkg,job,*,expected_revision,observed_sha256):
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            state=self._read(pkg);self._revision(state,expected_revision);self._active(state,job,observed_sha256)
            receipt=state['revert']
            if not receipt or receipt['job_id']!=job:raise HistoryConflict('A later fix closed this version’s revert window')
            after=copy.deepcopy(state);after['current']=copy.deepcopy(receipt['target'])
            after['working']=copy.deepcopy(receipt['working_before']);after['revert']=None
            after['jobs'][job]['state']='reverted';after['jobs'][job]['finished_at']=self.clock()
            return self._plan(state,after,'revert',job)

    def finish(self,pkg,job,state):
        _job(job)
        if state not in {'failed','blocked','cancelled','interrupted','discarded'}:raise HistoryConflict('Invalid terminal repair outcome')
        path=self._path(pkg)
        with file_lock(path.with_suffix('.lock')):
            value=self._read(pkg)
            if job not in value['jobs']:raise HistoryConflict('Repair history was not admitted here')
            if value['pending']!=job:return copy.deepcopy(value)
            value['pending']=None;value['jobs'][job].update(state=state,finished_at=self.clock());value['revision']+=1
            return self._save(value)

    def restore_before(self,plan,*,observed_sha256):
        plan=self._checked_plan(plan)
        if plan['action']!='install' or observed_sha256!=_binary(plan['before']['current']):
            raise HistoryConflict('Interrupted install recovery does not match the restored binary')
        path=self._path(plan['package'])
        with file_lock(path.with_suffix('.lock')):
            state=self._read(plan['package'])
            if state['last_recovery']==plan['id']:return copy.deepcopy(state)
            if state!=plan['before'] and state['last_plan']!=plan['id']:raise HistoryConflict('A newer source history cannot be replaced by old recovery')
            restored=copy.deepcopy(plan['before']);restored['revision']=state['revision']+1
            restored['pending']=None;restored['last_plan']=None;restored['last_recovery']=plan['id']
            restored['jobs'][plan['job_id']].update(state='interrupted',finished_at=self.clock())
            return self._save(restored)
