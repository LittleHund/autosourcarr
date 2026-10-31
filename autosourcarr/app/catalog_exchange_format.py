"""Bounded, typed catalogue data. A share file cannot supply SQL, paths or code."""
import hashlib
import json
import re
import uuid
from . import portable_identity

FORMAT='autosourcarr-catalogue'
MAX_BYTES=64*1024*1024
MAX_ENTRIES=100000
MAX_DECISIONS=200000
MAX_MEMBERS=2000
KINDS={'title','group','source-match','separate','entry-separate','import-choice','import-rejection','identity-review'}


def canonical(value):return json.dumps(value,sort_keys=True,ensure_ascii=False,separators=(',',':'),allow_nan=False)
def digest(value):return hashlib.sha256(canonical(value).encode('utf-8')).hexdigest()
def uid(kind='entry'):return 'asc1:'+kind+':autosourcarr:'+str(uuid.uuid4())


def check_uid(value,kind='entry'):
    if not isinstance(value,str):raise ValueError('A shared identity must be a string')
    prefix='asc1:'+kind+':autosourcarr:'
    if not value.startswith(prefix):raise ValueError('Unsupported shared identity namespace')
    try:parsed=uuid.UUID(value[len(prefix):])
    except ValueError as exc:raise ValueError('Invalid shared identity') from exc
    if not parsed.int or str(parsed)!=value[len(prefix):]:raise ValueError('Use a canonical shared identity')
    return value


def text(value,limit=16000):
    if not isinstance(value,str) or len(value)>limit or '\x00' in value:raise ValueError('Invalid or oversized catalogue text')
    try:value.encode('utf-8',errors='strict')
    except UnicodeError as exc:raise ValueError('Catalogue text must be valid UTF-8') from exc
    return value


def keys(value,required):
    if not isinstance(value,dict) or set(value)!=set(required):raise ValueError('Unsupported catalogue fields')


def reference(value):
    scope,_,_,path=portable_identity.parse(value)
    if scope not in {'work','edition','gallery','record'}:raise ValueError('Use a public catalogue, gallery or source reference')
    if path and not public_identity_text(path):raise ValueError('Source identity contains a private access parameter')
    return value


def public_identity_text(value):
    return not re.search(r'(?:[?&](?:auth|key|sig|signature|token|password|secret|session|api[_-]?key)=|https?://[^\s/]+@|/g/[1-9][0-9]*/[A-Za-z0-9]{8,})',str(value),re.I)


def metadata(value):
    keys(value,{'description','author','artist','genre','status'})
    for name in ('description','author','artist','genre'):text(value[name],64000 if name=='description' else 16000)
    if type(value['status']) is not int or not 0<=value['status']<=100:raise ValueError('Invalid catalogue publication status')
    return value


def locator(value):
    keys(value,{'reference','source_id','title','raw_title','gallery','metadata'})
    reference(value['reference']);text(value['title'],2000);text(value['raw_title'],2000);metadata(value['metadata'])
    sid=value['source_id']
    if not isinstance(sid,str) or not re.fullmatch(r'-?[0-9]{1,20}',sid) or sid!=str(int(sid)) or sid in {'0','-1'}:raise ValueError('Invalid stable source identity')
    scope,_,source,_=portable_identity.parse(value['reference'])
    if scope not in {'record','gallery'} or scope=='record' and source!=sid:raise ValueError('Source reference does not belong to its declared source')
    if type(value['gallery']) is not bool:raise ValueError('Invalid gallery profile hint')
    return value


def entry(value):
    keys(value,{'uid','aliases','lineage','title','titles','metadata','references','sources'})
    check_uid(value['uid']);text(value['title'],2000)
    for field,limit in [('aliases',512),('titles',256),('references',MAX_MEMBERS),('sources',MAX_MEMBERS)]:
        if not isinstance(value[field],list) or len(value[field])>limit:raise ValueError('Too many catalogue '+field)
    for item in value['aliases']:check_uid(item)
    for item in value['titles']:text(item,2000)
    for item in value['references']:reference(item)
    for item in value['sources']:locator(item)
    source_keys=[(item['source_id'],item['reference']) for item in value['sources']]
    if len(set(source_keys))!=len(source_keys):raise ValueError('Duplicate source record in one entry version')
    if not isinstance(value['lineage'],list) or len(value['lineage'])>513:raise ValueError('Too many lineage identities')
    for item in value['lineage']:
        keys(item,{'uid','records'});check_uid(item['uid'])
        if item['uid'] not in {value['uid'],*value['aliases']}:raise ValueError('Lineage must describe this entry or a retained alias')
        if not isinstance(item['records'],list) or len(item['records'])>MAX_MEMBERS:raise ValueError('Too many lineage source references')
        for record in item['records']:
            keys(record,{'source_id','reference'});reference(record['reference'])
            scope,_,sid,_=portable_identity.parse(record['reference'])
            if scope not in {'record','gallery'} or not isinstance(record['source_id'],str) or not re.fullmatch(r'-?[0-9]{1,20}',record['source_id']):raise ValueError('Invalid lineage source reference')
            if record['source_id']!=str(int(record['source_id'])) or record['source_id'] in {'0','-1'}:raise ValueError('Invalid lineage source identity')
            if scope=='record' and record['source_id']!=sid:raise ValueError('Lineage source does not match its reference')
    metadata(value['metadata'])
    return value


def decision(value):
    keys(value,{'uid','kind','entries','records','value'})
    check_uid(value['uid'],'decision')
    if not isinstance(value['kind'],str) or value['kind'] not in KINDS:raise ValueError('Unsupported manual decision')
    for field in ('entries','records'):
        if not isinstance(value[field],list) or len(value[field])>MAX_MEMBERS:raise ValueError('Too many decision members')
    for item in value['entries']:check_uid(item)
    for item in value['records']:locator(item)
    data=value['value']
    allowed={'title','group_title','note','reference','decision','backup_key','backup_title','backup_source','backup_source_id','backup_source_url','rejected_title','signature','grouping_suppressed'}
    if not isinstance(data,dict) or set(data)-allowed:raise ValueError('Unsupported decision fields')
    for name,item in data.items():text(item,64000 if name=='note' else 6000)
    if data.get('reference'):reference(data['reference'])
    if data.get('decision') and data['decision'] not in {'confirmed','excluded','needs-review'}:raise ValueError('Unsupported identity review choice')
    if data.get('backup_source_url'):portable_identity.source_path(data['backup_source_url'])
    if any(not public_identity_text(data.get(key,'')) for key in ('backup_key','backup_source_url','signature')):raise ValueError('Import decision contains a private access parameter')
    if data.get('signature') and not data['signature'].startswith(('source-url:','source-title:','entry-title:')):raise ValueError('Use a portable import rejection signature')
    if data.get('grouping_suppressed') not in (None,'0','1'):raise ValueError('Invalid manual grouping choice')
    if value['kind']=='title' and (len(value['entries'])!=1 or not data.get('title')):raise ValueError('A title decision needs one entry and a title')
    if value['kind']=='entry-separate' and len(value['entries'])!=2:raise ValueError('A separation needs two entry identities')
    if value['kind']=='identity-review' and (len(value['entries'])!=1 or not data.get('reference') or not data.get('decision')):raise ValueError('An identity review needs one entry, a reference and a decision')
    if value['kind'] in {'source-match','import-choice'} and (len(value['entries'])>1 or len(value['records'])>1):raise ValueError('This decision must identify one target source record')
    if value['kind']=='separate' and (not 1<=len(value['entries'])<=2 or len(value['records'])>1):raise ValueError('A source separation needs a target and at most one source record')
    return value


def validate(value):
    keys(value,{'format','version','origin','entries','decisions'})
    if value['format']!=FORMAT or type(value['version']) is not int or value['version']!=1:raise ValueError('Unsupported catalogue file version')
    check_uid(value['origin'],'origin')
    for field,limit in [('entries',MAX_ENTRIES),('decisions',MAX_DECISIONS)]:
        if not isinstance(value[field],list) or len(value[field])>limit:raise ValueError('Too many catalogue '+field)
    seen=set();members=0
    for row in value['entries']:
        entry(row);key=(row['uid'],digest(row))
        if key in seen:raise ValueError('Duplicate entry version in one catalogue file')
        seen.add(key);members+=len(row['sources'])+len(row['references'])+len(row['titles'])+len(row['aliases'])+sum(len(item['records']) for item in row['lineage'])
    decisions_seen=set()
    for row in value['decisions']:
        decision(row);key=(row['uid'],digest(row))
        if key in decisions_seen:raise ValueError('Duplicate decision version in one catalogue file')
        decisions_seen.add(key);members+=len(row['records'])+len(row['entries'])
    if members>1000000:raise ValueError('Catalogue collection limit exceeded; export smaller batches')
    return value


def decode(raw):
    if not isinstance(raw,bytes) or not raw or len(raw)>MAX_BYTES:raise ValueError('Choose a nonempty catalogue file up to 64 MiB')
    def pairs(items):
        result={}
        for key,value in items:
            if key in result:raise ValueError('Duplicate JSON field in catalogue file')
            result[key]=value
        return result
    def invalid(_):raise ValueError('Nonfinite JSON values are not supported')
    try:value=json.loads(raw.decode('utf-8',errors='strict'),object_pairs_hook=pairs,parse_constant=invalid)
    except (UnicodeError,json.JSONDecodeError,RecursionError) as exc:raise ValueError('Choose an exported UTF-8 AutoSourcarr catalogue file') from exc
    return validate(value)
