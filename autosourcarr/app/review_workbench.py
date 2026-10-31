"""Unified existing evidence and reversible, revision-bound separate decisions."""
import hashlib
import json
import uuid
from .db import now_iso


def checksum(value):return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()


def preview(database,service,pairs):
    if not isinstance(pairs,list) or not 1<=len(pairs)<=100:raise ValueError('Select 1 to 100 work pairs')
    seen=set();items=[]
    with database.catalog() as c:
        for pair in pairs:
            if not isinstance(pair,list) or len(pair)!=2 or any(type(v) is not int or v<=0 for v in pair):raise ValueError('Use exact positive work IDs')
            left,right=sorted(pair)
            if left==right or (left,right) in seen:raise ValueError('Duplicate or identical work pair')
            seen.add((left,right))
            entries=[dict(r) for r in c.execute('SELECT id,preferred_title,updated_at FROM entries WHERE id IN (?,?) ORDER BY id',(left,right))]
            if len(entries)!=2 or any(service.canonical_entry_id(v)!=v for v in (left,right)):raise ValueError('Work identities changed; reload review')
            old=c.execute('SELECT * FROM duplicate_reviews WHERE left_entry_id=? AND right_entry_id=?',(left,right)).fetchone()
            items.append({'pair':[left,right],'entries':entries,'before':dict(old) if old else None,
                          'effect':'Remember these as separate works; no manga, chapters or downloads are removed'})
    return {'items':items,'revision':checksum(items),'operation':'not_duplicate','undo':'Only while these exact decisions and work identities remain unchanged'}


def apply(database,service,pairs,revision):
    with service._catalog_maintenance_lock:
        with database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            current=preview(database,service,pairs)
            if current['revision']!=revision:raise ValueError('Review changed; preview the selected pairs again')
            identifier=uuid.uuid4().hex
            for item in current['items']:
                left,right=item['pair'];service.review_duplicate(left,right,'not_duplicate')
                item['after']=dict(c.execute('SELECT * FROM duplicate_reviews WHERE left_entry_id=? AND right_entry_id=?',(left,right)).fetchone())
            c.execute('INSERT INTO matching_review_actions(id,action_json,created_at) VALUES(?,?,?)',(identifier,json.dumps(current),now_iso()))
    return {'id':identifier,'applied':len(current['items']),'undo_available':True}


def undo(database,service,identifier):
    with service._catalog_maintenance_lock:
        with database.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            row=c.execute('SELECT * FROM matching_review_actions WHERE id=?',(identifier,)).fetchone()
            if not row or row['undone_at']:raise ValueError('Review action is unavailable or already undone')
            action=json.loads(row['action_json'])
            for item in action['items']:
                left,right=item['pair']
                current=c.execute('SELECT * FROM duplicate_reviews WHERE left_entry_id=? AND right_entry_id=?',(left,right)).fetchone()
                entries=[dict(r) for r in c.execute('SELECT id,preferred_title,updated_at FROM entries WHERE id IN (?,?) ORDER BY id',(left,right))]
                if entries!=item['entries'] or any(service.canonical_entry_id(i)!=i for i in (left,right)) or (dict(current) if current else None)!=item['after']:
                    raise ValueError('Work or decision changed after this review; scoped undo is unavailable')
            for item in action['items']:
                left,right=item['pair'];old=item['before']
                if old:c.execute('UPDATE duplicate_reviews SET decision=?,reviewed_at=? WHERE left_entry_id=? AND right_entry_id=?',(old['decision'],old['reviewed_at'],left,right))
                else:c.execute('DELETE FROM duplicate_reviews WHERE left_entry_id=? AND right_entry_id=?',(left,right))
                c.execute('INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(?,?,?,?)',(left,right,'undo-reviewed-separation',now_iso()))
            c.execute('UPDATE matching_review_actions SET undone_at=? WHERE id=?',(now_iso(),identifier))
        service._invalidate_duplicate_snapshot()
    return {'undone':len(action['items'])}


def audit(database,service,entry_id=None,limit=100):
    limit=max(1,min(100,int(limit)))
    with database.catalog() as c:
        where=' WHERE entry_id=?' if entry_id else '';args=(entry_id,limit) if entry_id else (limit,)
        matches=[dict(r) for r in c.execute('SELECT * FROM match_history'+where+' ORDER BY id DESC LIMIT ?',args)]
        where=' WHERE left_entry_id=? OR right_entry_id=?' if entry_id else '';args=(entry_id,entry_id,limit) if entry_id else (limit,)
        decisions=[dict(r) for r in c.execute('SELECT * FROM duplicate_review_history'+where+' ORDER BY id DESC LIMIT ?',args)]
        actions=[{'id':r['id'],'created_at':r['created_at'],'undone_at':r['undone_at']} for r in c.execute('SELECT * FROM matching_review_actions ORDER BY created_at DESC LIMIT 20')]
    from .matching_review import history
    return {'matches':matches,'decisions':decisions,'bulk_actions':actions,
            'automatic_merges':history(database,service,query=str(entry_id) if entry_id else '',limit=min(limit,50)),
            'entry':service.get_entry(entry_id) if entry_id else None,
            'note':'Original-member evidence and legacy reason-only history retain their existing provenance. Undo applies to new reviewed separation batches only.'}
