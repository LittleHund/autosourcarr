"""Persistent per-instance templates; applied rules are independent copies."""
import hashlib
import json
import uuid

from fastapi import HTTPException

from . import matching_policies
from .db import now_iso


def configuration(database, connection=None):
    presets = [{**p, 'builtin': True} for p in matching_policies.presets()]
    if connection is None:
        with database.catalog() as c:
            return configuration(database, c)
    rows = [dict(r) for r in connection.execute('SELECT * FROM matching_presets ORDER BY name COLLATE NOCASE,id')]
    revision = hashlib.sha256(json.dumps(rows, sort_keys=True).encode()).hexdigest()
    presets.extend({'id': r['id'], 'name': r['name'], 'description': r['description'],
                    'rule': json.loads(r['rule_json']), 'builtin': False} for r in rows)
    return {'presets': presets, 'preset_revision': revision}


def save(database, service, update, preset_id=None):
    name = update.name.strip()
    description = update.description.strip()
    if not name or len(name) > 80 or any(ord(ch) < 32 or ord(ch) == 127 for ch in name):
        raise ValueError('Use a preset name of 1–80 characters without line breaks.')
    if len(description) > 500 or any(ord(ch) < 32 and ch not in '\n\t' for ch in description):
        raise ValueError('Preset descriptions are limited to 500 characters.')
    rule = matching_policies.validate(update.rule)
    if rule is None:
        raise ValueError('Choose matching checks or save a Disabled preset.')
    with service._catalog_maintenance_lock:
        with database.catalog() as c:
            # Check and write under the same SQLite writer reservation, including
            # independent service objects sharing a retained catalogue file.
            c.execute('BEGIN IMMEDIATE')
            current = configuration(database, c)
            if update.revision != current['preset_revision']:
                raise HTTPException(409, 'Presets changed in another window. Reload presets before saving.')
            selected = next((p for p in current['presets'] if p['id'] == preset_id), None)
            if preset_id:
                if not selected:
                    raise KeyError(preset_id)
                if selected['builtin']:
                    raise ValueError('Use Save as new to customize a built-in preset.')
            elif len(current['presets']) >= 205:
                raise ValueError('This instance already has 200 custom presets. Edit or delete an existing preset.')
            if any(p['id'] != preset_id and p['name'].casefold() == name.casefold() for p in current['presets']):
                raise ValueError('A preset with this name already exists. Choose a different name.')
            preset_id = preset_id or 'custom-'+str(uuid.uuid4())
            c.execute('INSERT INTO matching_presets(id,name,description,rule_json,updated_at) VALUES(?,?,?,?,?) '
                      'ON CONFLICT(id) DO UPDATE SET name=excluded.name,description=excluded.description,'
                      'rule_json=excluded.rule_json,updated_at=excluded.updated_at',
                      (preset_id, name, description, json.dumps(rule), now_iso()))
            result = configuration(database, c)
        return result


def delete(database, service, preset_id, revision):
    with service._catalog_maintenance_lock:
        with database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            current = configuration(database, c)
            if revision != current['preset_revision']:
                raise HTTPException(409, 'Presets changed in another window. Reload presets before deleting.')
            selected = next((p for p in current['presets'] if p['id'] == preset_id), None)
            if not selected:
                raise KeyError(preset_id)
            if selected['builtin']:
                raise ValueError('Built-in presets cannot be deleted.')
            c.execute('DELETE FROM matching_presets WHERE id=?', (preset_id,))
            result = configuration(database, c)
        return result
