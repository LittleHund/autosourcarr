"""Small local read/edit endpoints for matching requirements and recorded merges."""
import json

from fastapi import APIRouter, Depends, HTTPException, Query
from pydantic import BaseModel, ConfigDict, Field

from . import matching_policies as policies
from . import matching_presets
from .async_utils import run_blocking
from .db import now_iso
from .read_lane import read_lane


# Old releases recorded merge IDs and a reason, without original snapshots.
# Only explicit automatic markers prove origin; unknown/manual records stay out.
LEGACY_AUTOMATIC = """source_id='*' AND typeof(old_source_manga_id)='integer' AND typeof(new_source_manga_id)='integer'
    AND old_source_manga_id>0 AND new_source_manga_id>0
    AND old_source_manga_id<>new_source_manga_id
    AND (change_type='merge:automatic-consolidation' OR change_type LIKE 'merge:automatic-safe:%')"""


class PolicyUpdate(BaseModel):
    model_config = ConfigDict(extra='forbid')
    revision: str
    rule: dict | None


class PresetUpdate(BaseModel):
    model_config = ConfigDict(extra='forbid')
    revision: str = Field(max_length=64)
    name: str = Field(max_length=80)
    description: str = Field(default='', max_length=500)
    rule: dict


class PresetDelete(BaseModel):
    model_config = ConfigDict(extra='forbid')
    revision: str = Field(max_length=64)


def configuration(database, service):
    service._reload_source_cache()
    values = policies.rules(database)
    return {'revision': policies.revision(values),
            **matching_presets.configuration(database),
            'rules': [{'scope': k[0], 'subject': k[1], 'direction': k[2], 'rule': v} for k, v in values.items()],
            'sources': [{'id': sid, 'name': row.get('name') or sid, 'lang': row.get('lang') or '', 'group_id': row.get('group_id')}
                        for sid, row in service._source_cache.items()],
            'groups': service.list_source_groups()}


def save(database, service, scope, subject, direction, update):
    if scope not in {'source','group'} or direction not in {'internal','external'}:
        raise ValueError('Unknown source/group or matching direction.')
    rule = policies.validate(update.rule)
    with service._catalog_maintenance_lock:
        current = configuration(database, service)
        if update.revision != current['revision']:
            raise HTTPException(409, 'Matching rules changed in another window. Reload before saving.')
        if not any(str(row['id']) == subject for row in current['sources' if scope == 'source' else 'groups']):
            raise KeyError(subject)
        with database.catalog() as c:
            if rule is None:
                c.execute('DELETE FROM matching_policies WHERE scope=? AND subject=? AND direction=?', (scope, subject, direction))
            else:
                c.execute('INSERT INTO matching_policies(scope,subject,direction,rule_json,updated_at) VALUES(?,?,?,?,?) ON CONFLICT(scope,subject,direction) DO UPDATE SET rule_json=excluded.rule_json,updated_at=excluded.updated_at',
                          (scope, subject, direction, json.dumps(rule), now_iso()))
        policies.invalidate(database)
        service._invalidate_duplicate_snapshot()
        return configuration(database, service)


def decoded(raw):
    try:
        value = json.loads(raw or '{}')
        return value if isinstance(value, dict) else {}
    except (ValueError, TypeError):
        return {}


def history(database, service, *, offset=0, limit=50, query=''):
    # Read only compact JSON projections; do not ship all source snapshots per row.
    projection = """SELECT id,target_entry_id,duplicate_entry_id,reason,merged_at,repaired_at,
       CASE WHEN json_valid(target_snapshot_json) THEN json_extract(target_snapshot_json,'$.entry.preferred_title') END left_title,
       CASE WHEN json_valid(duplicate_snapshot_json) THEN json_extract(duplicate_snapshot_json,'$.entry.preferred_title') END right_title,
       0 legacy
       FROM automatic_merge_events
       UNION ALL
       SELECT -h.id,h.new_source_manga_id,h.old_source_manga_id,substr(h.change_type,7),h.changed_at,NULL,
       'Entry '||h.new_source_manga_id,'Entry '||h.old_source_manga_id,1
       FROM match_history h WHERE """ + LEGACY_AUTOMATIC + """
       AND NOT EXISTS (SELECT 1 FROM automatic_merge_events e
           WHERE e.target_entry_id=h.new_source_manga_id AND e.duplicate_entry_id=h.old_source_manga_id)"""
    term = '%'+query.strip().replace('\\','\\\\').replace('%','\\%').replace('_','\\_')+'%'
    where = "WHERE (?='' OR left_title LIKE ? ESCAPE '\\' OR right_title LIKE ? ESCAPE '\\' OR CAST(target_entry_id AS TEXT)=? OR CAST(duplicate_entry_id AS TEXT)=?)"
    args = (query.strip(), term, term, query.strip(), query.strip())
    with database.catalog() as c:
        total = c.execute('SELECT COUNT(*) FROM ('+projection+') '+where, args).fetchone()[0]
        rows = [dict(r) for r in c.execute('SELECT * FROM ('+projection+') '+where+' ORDER BY merged_at DESC,id DESC LIMIT ? OFFSET ?', (*args, limit, offset))]
        canonical = {r['id']: service.canonical_entry_id(r['target_entry_id']) for r in rows}
        ids = list(set(canonical.values()))
        existing = {r[0] for r in c.execute('SELECT id FROM entries WHERE id IN ('+','.join('?' for _ in ids)+')', ids)} if ids else set()
    for row in rows:
        row['current_entry_id'] = canonical[row['id']] if canonical[row['id']] in existing else None
        row['legacy'] = bool(row['legacy'])
        row['state'] = 'Older automatic merge · reason only' if row['legacy'] else 'Re-evaluated' if row['repaired_at'] else 'Recorded merge'
    return {'items': rows, 'total': total, 'offset': offset, 'limit': limit}


def detail(database, service, event_id):
    with database.catalog() as c:
        if event_id < 0:
            older = c.execute('SELECT * FROM match_history WHERE id=? AND '+LEGACY_AUTOMATIC, (-event_id,)).fetchone()
            # Match the list's de-duplication and never expose a manual/unknown
            # event through a guessed negative ID.
            if older and c.execute('SELECT 1 FROM automatic_merge_events WHERE target_entry_id=? AND duplicate_entry_id=?',
                                    (older['new_source_manga_id'], older['old_source_manga_id'])).fetchone():
                older = None
            row = {'id': event_id, 'target_entry_id': older['new_source_manga_id'],
                   'duplicate_entry_id': older['old_source_manga_id'], 'reason': older['change_type'][6:],
                   'merged_at': older['changed_at'], 'repaired_at': None,
                   'target_snapshot_json': '{}', 'duplicate_snapshot_json': '{}',
                   'evidence_json': json.dumps({'provenance': 'Retained automatic merge marker; original snapshots and decision scores were not stored.',
                                                'match_history_id': older['id']}), 'legacy': True} if older else None
        else:
            row = c.execute('SELECT * FROM automatic_merge_events WHERE id=?', (event_id,)).fetchone()
    if not row:
        raise KeyError(event_id)
    row = dict(row)
    snapshots = [decoded(row['target_snapshot_json']), decoded(row['duplicate_snapshot_json'])]
    if row.get('legacy'):
        snapshots = [{'entry': {'id': row['target_entry_id']}}, {'entry': {'id': row['duplicate_entry_id']}}]
    def entry_of(snapshot):
        value = snapshot.get('entry')
        return value if isinstance(value, dict) else {}
    def rows_of(snapshot, key):
        value = snapshot.get(key)
        return value if isinstance(value, list) else []
    # Older releases saved left/right order even when the right entry survived.
    snapshots.sort(key=lambda s: 0 if entry_of(s).get('id') == row['target_entry_id'] else 1)
    before = []
    for s in snapshots:
        entry = entry_of(s)
        role = ('Surviving entry' if entry.get('id') == row['target_entry_id'] else
                'Absorbed entry' if entry.get('id') == row['duplicate_entry_id'] else 'Original entry')
        before.append({'entry': {k: entry.get(k) for k in ('id','preferred_title','description','author','artist','thumbnail_url')},
                       'role': role,
                       'titles': list(dict.fromkeys(str(t.get('title') or '') for t in rows_of(s, 'titles') if isinstance(t, dict))),
                       'records': [{k: r.get(k) for k in ('source_id','source_manga_id','source_title','description','author','artist')}
                                   for r in [*rows_of(s, 'matches'), *rows_of(s, 'variants')] if isinstance(r, dict)],
                       'available': bool(entry) and not row.get('legacy', False)})
    current = service.canonical_entry_id(row['target_entry_id'])
    return {'id': row['id'], 'merged_at': row['merged_at'], 'reason': row['reason'], 'repaired_at': row['repaired_at'],
            'before': before, 'evidence': decoded(row.get('evidence_json')), 'legacy': row.get('legacy', False),
            'current_entry_id': current if service.entry_exists(current) else None}


def router(database, service, admin_guard):
    routes = APIRouter(prefix='/api/admin', dependencies=[Depends(admin_guard)])

    async def local_read(function, /, *args, **kwargs):
        def build():
            try:
                return function(database, service, *args, **kwargs)
            finally:
                database.close_current_thread()
        return await read_lane.run(build)

    @routes.get('/matching-policies')
    async def read_policies():
        return await local_read(configuration)

    @routes.put('/matching-policies/{scope}/{subject}/{direction}')
    async def write_policy(scope: str, subject: str, direction: str, update: PolicyUpdate):
        try:
            return await run_blocking(save, database, service, scope, subject, direction, update)
        except ValueError as e:
            raise HTTPException(400, str(e))
        except KeyError:
            raise HTTPException(404, 'Source or group no longer exists.')

    @routes.get('/matching-presets')
    async def read_presets():
        return await local_read(lambda db, unused: matching_presets.configuration(db))

    async def write_preset(update, preset_id=None):
        try:
            return await run_blocking(matching_presets.save, database, service, update, preset_id)
        except ValueError as e:
            raise HTTPException(400, str(e))
        except KeyError:
            raise HTTPException(404, 'Preset no longer exists.')

    @routes.post('/matching-presets', status_code=201)
    async def create_preset(update: PresetUpdate):
        return await write_preset(update)

    @routes.put('/matching-presets/{preset_id}')
    async def edit_preset(preset_id: str, update: PresetUpdate):
        return await write_preset(update, preset_id)

    @routes.delete('/matching-presets/{preset_id}')
    async def delete_preset(preset_id: str, update: PresetDelete):
        try:
            return await run_blocking(matching_presets.delete, database, service, preset_id, update.revision)
        except ValueError as e:
            raise HTTPException(400, str(e))
        except KeyError:
            raise HTTPException(404, 'Preset no longer exists.')

    @routes.get('/duplicates/history')
    async def read_history(offset: int = Query(0, ge=0, le=1000000), limit: int = Query(50, ge=1, le=100), q: str = Query('', max_length=200)):
        return await local_read(history, offset=offset, limit=limit, query=q)

    @routes.get('/duplicates/history/{event_id}')
    async def read_detail(event_id: int):
        try:
            return await local_read(detail, event_id)
        except KeyError:
            raise HTTPException(404, 'Recorded merge not found.')
    return routes
