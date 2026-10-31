"""Private review of portable references; no matcher/source-binding authority."""
import hashlib,json
from . import portable_identity as portable

SCHEMA='''
CREATE TABLE IF NOT EXISTS portable_identity_reviews (
 entry_id INTEGER NOT NULL, reference TEXT NOT NULL, decision TEXT NOT NULL,
 evidence_sha256 TEXT NOT NULL, note TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(entry_id,reference)
);
CREATE INDEX IF NOT EXISTS idx_portable_review_reference ON portable_identity_reviews(reference,decision,entry_id);
CREATE TABLE IF NOT EXISTS portable_identity_review_history (
 id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL, old_entry_id INTEGER NOT NULL,
 reference TEXT NOT NULL, decision TEXT NOT NULL, evidence_sha256 TEXT NOT NULL,
 note TEXT NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_portable_review_history ON portable_identity_review_history(entry_id,id);
'''


def _hash(value):return hashlib.sha256(json.dumps(value,sort_keys=True,ensure_ascii=False,separators=(',',':')).encode()).hexdigest()


def evidence(entry):
    return _hash({'entry':entry.get('canonical_id',entry['id']),
      'sources':sorted((str(r['source_id']),str(r.get('source_manga_url') or ''),str(r.get('source_title') or '')) for r in [*(entry.get('matches') or []),*(entry.get('source_variants') or [])]),
      'providers':sorted((str(r.get('provider')),str(r.get('external_id')),str(r.get('identity_scope'))) for r in entry.get('identities') or [])})


def _rows(database,eid):
    with database.catalog() as c:return [dict(r) for r in c.execute('SELECT * FROM portable_identity_reviews WHERE entry_id=? ORDER BY reference',(eid,))]


def review_evidence(database,entry):
    with database.catalog() as c:
        shared=[r[0] for r in c.execute('SELECT reference FROM catalogue_public_refs WHERE entry_id=? ORDER BY reference',(entry.get('canonical_id',entry['id']),))]
    return _hash([evidence(entry),shared]) if shared else evidence(entry)


def decorate(database,entry):
    from .catalog_identity import references
    result=dict(entry);base=portable.describe(entry);refs={r['id']:dict(r) for r in [*references(database,int(base['local_entry_id'])),*base['references']]};proof=review_evidence(database,entry)
    for row in _rows(database,int(base['local_entry_id'])):
        ref=row['reference'];decision=row['decision'];fresh=row['evidence_sha256']==proof
        if decision=='excluded':refs.pop(ref,None);continue
        if decision=='confirmed' and fresh:
            scope,provider,external,_=portable.parse(ref)
            refs[ref]={**refs.get(ref,{'id':ref,'scope':scope,'provider':provider,'external_id':external}),
                       'association':'confirmed on this instance'}
        elif ref in refs:refs[ref]['association']='needs local review' if decision=='needs-review' else 'evidence changed; review again'
    result['portable_identity']={**base,'references':[refs[k] for k in sorted(refs)]}
    return result


def lookup_entries(database,service,reference,ids):
    """Overlay exact local exclusions/custom confirmations on the normal lookup."""
    with database.catalog() as c:
        rows=[dict(r) for r in c.execute('SELECT * FROM portable_identity_reviews WHERE reference=?',(reference,))]
    excluded={service.canonical_entry_id(r['entry_id']) for r in rows if r['decision']=='excluded'}
    result=set(ids)-excluded
    for row in rows:
        if row['decision']!='confirmed' or row['entry_id'] in excluded:continue
        if service.canonical_entry_id(row['entry_id']) in result:continue
        try:entry=service.get_entry(row['entry_id'])
        except KeyError:continue
        if row['evidence_sha256']==review_evidence(database,entry):result.add(int(entry.get('canonical_id',entry['id'])))
    return sorted(result-excluded)


def read(database,service,eid):
    from .catalog_identity import references
    eid=service.canonical_entry_id(int(eid));entry=service.get_entry(eid);proof=review_evidence(database,entry);rows=_rows(database,eid)
    refs={r['id']:{**r,'decision':'inherited','note':'','current_evidence':True,'manual':False} for r in [*references(database,eid),*portable.describe(entry)['references']] if r['scope']!='entry'}
    for row in rows:
        scope,provider,external,_=portable.parse(row['reference'])
        refs[row['reference']]={**refs.get(row['reference'],{'id':row['reference'],'scope':scope,'provider':provider,'external_id':external,'association':'local reference','manual':True}),
                               'decision':row['decision'],'note':row['note'],'current_evidence':row['evidence_sha256']==proof}
    for ref in refs.values():
        # Shared references are warnings about association/editions, not duplicate proof.
        others=portable.resolve(database,service,ref['id'])
        ref['shared_with']=[{'id':r['id'],'title':r['preferred_title']} for r in others['items'] if r['id']!=eid][:12]
    with database.catalog() as c:
        history=[dict(r) for r in c.execute('SELECT * FROM portable_identity_review_history WHERE entry_id=? ORDER BY id DESC LIMIT 40',(eid,))]
    return {'entry_id':eid,'title':entry['preferred_title'],'revision':_hash([proof,rows]),'evidence_sha256':proof,
            'references':[refs[k] for k in sorted(refs)],'history':history,
            'note':'Reviews affect portable-ID associations on this instance only. Original provider evidence, source bindings, merges and chapters are unchanged. Shared work IDs can legitimately cover different editions.'}


def listing(database,search='',offset=0,limit=30):
    term='%'+str(search).replace('\\','\\\\').replace('%','\\%').replace('_','\\_')+'%'
    with database.catalog() as c:
        total=c.execute("SELECT COUNT(*) FROM entries WHERE preferred_title LIKE ? ESCAPE '\\'",(term,)).fetchone()[0]
        rows=[dict(r) for r in c.execute("""SELECT e.id,e.preferred_title,
             (SELECT COUNT(*) FROM identity_links i WHERE i.entry_id=e.id) provider_count,
             (SELECT COUNT(*) FROM portable_identity_reviews r WHERE r.entry_id=e.id) saved_decisions
             FROM entries e WHERE preferred_title LIKE ? ESCAPE '\\' ORDER BY e.preferred_title COLLATE NOCASE,e.id LIMIT ? OFFSET ?""",(term,limit,offset))]
    return {'items':rows,'total':total,'offset':offset,'limit':limit}


def save(database,service,eid,reference,decision,revision,note='',restore_id=None):
    from .db import now_iso
    if decision not in {'confirmed','excluded','needs-review','reset','restore'}:raise ValueError('Choose a supported identity review action')
    if not isinstance(note,str) or len(note)>1200:raise ValueError('Review notes must be at most1200characters')
    with service._catalog_maintenance_lock,database.catalog() as c:
        if not c.in_transaction:c.execute('BEGIN IMMEDIATE')
        old=read(database,service,eid);eid=old['entry_id']
        if revision!=old['revision']:raise ValueError('Entry identity or review changed; reload before saving')
        if decision=='restore':
            row=c.execute('SELECT * FROM portable_identity_review_history WHERE entry_id=? AND id=?',(eid,restore_id)).fetchone()
            if not row:raise ValueError('Retained review is unavailable')
            reference=row['reference'];decision='needs-review';note=row['note']
        scope,_,_,_=portable.parse(reference)
        known={r['id'] for r in old['references']}
        if scope=='entry':raise ValueError('Portable entry lineage is retained automatically; review public work/source associations instead')
        if reference not in known and (scope not in {'work','edition','gallery'} or decision!='needs-review' or len(note.strip())<8):
            raise ValueError('Add a public catalogue/gallery reference for review with a short provenance note first')
        previous=c.execute('SELECT * FROM portable_identity_reviews WHERE entry_id=? AND reference=?',(eid,reference)).fetchone()
        c.execute('INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES(?,?,?,?,?,?,?,?)',
                  (eid,eid,reference,previous['decision'] if previous else 'inherited',previous['evidence_sha256'] if previous else old['evidence_sha256'],previous['note'] if previous else '',decision,now_iso()))
        if decision=='reset':c.execute('DELETE FROM portable_identity_reviews WHERE entry_id=? AND reference=?',(eid,reference))
        else:c.execute('INSERT OR REPLACE INTO portable_identity_reviews VALUES(?,?,?,?,?,?)',(eid,reference,decision,old['evidence_sha256'],note.strip(),now_iso()))
    return read(database,service,eid)


def reparent(c,target,duplicate):
    from .db import now_iso
    rows=[dict(r) for r in c.execute('SELECT * FROM portable_identity_reviews WHERE entry_id=?',(duplicate,))]
    c.execute('UPDATE portable_identity_review_history SET entry_id=? WHERE entry_id=?',(target,duplicate))
    for row in rows:
        c.execute('INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES(?,?,?,?,?,?,?,?)',
                  (target,duplicate,row['reference'],row['decision'],row['evidence_sha256'],row['note'],'retained after merge; surviving decision wins; restore requires review',now_iso()))
        if not c.execute('SELECT 1 FROM portable_identity_reviews WHERE entry_id=? AND reference=?',(target,row['reference'])).fetchone():
            c.execute('INSERT INTO portable_identity_reviews VALUES(?,?,?,?,?,?)',(target,row['reference'],'excluded' if row['decision']=='excluded' else 'needs-review',row['evidence_sha256'],row['note'],now_iso()))
    c.execute('DELETE FROM portable_identity_reviews WHERE entry_id=?',(duplicate,))
