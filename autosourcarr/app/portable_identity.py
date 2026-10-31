"""ASC Media ID v1: deterministic, typed references to existing public identities.

No central allocator, title hash, local manga number, network lookup or merge is
involved. Work references and exact source-record references remain distinct.
The reversible source key allows indexed local lookup without a new registry.
"""
import base64
import re
import uuid
from urllib.parse import parse_qsl,urlsplit,urljoin

PROVIDERS={'anilist':'work','myanimelist':'work','kitsu':'work','mangaupdates':'edition','mangadex':'edition','comicvine':'edition'}
GALLERIES={'nhentai':({'nhentai.net','www.nhentai.net'},r'/g/([1-9][0-9]*)/?'),
           'ehentai':({'e-hentai.org','exhentai.org'},r'/g/([1-9][0-9]*)/[A-Za-z0-9]+/?'),
           'hitomi':({'hitomi.la'},r'/(?:galleries/([1-9][0-9]*)|(?:manga|doujinshi|artistcg|gamecg|imageset)/[^/]+-([1-9][0-9]*))\.html')}
MAX_REFERENCE=6000
_SENSITIVE=re.compile(r'password|passwd|token|secret|authorization|api[_-]?key|session|cookie|signature',re.I)


def source_path(value):
    if not isinstance(value,str) or not value or len(value)>4096 or any(ord(c)<33 or ord(c)==127 for c in value):raise ValueError('Invalid public source URL')
    p=urlsplit(value)
    if p.fragment or p.username or p.password or value.startswith('//') or '\\' in value:raise ValueError('Ambiguous source URL')
    if p.scheme and (p.scheme not in {'https','http'} or not p.hostname):raise ValueError('Invalid source URL scheme')
    if not p.path.startswith('/') or any(_SENSITIVE.search(k) for k,_ in parse_qsl(p.query)):raise ValueError('Source identity may contain private values')
    # Do not decode, casefold, strip semantic query values or normalise editions.
    return p.path+('?' +p.query if '?' in value else '')


def _external(provider,value):
    if not isinstance(value,str) or len(value)>100:raise ValueError('Invalid external catalogue ID')
    if provider=='mangadex':
        parsed=str(uuid.UUID(value))
        if parsed=='00000000-0000-0000-0000-000000000000':raise ValueError('Empty MangaDex identity')
        return parsed
    if not re.fullmatch(r'[0-9]{1,20}',value) or int(value)<=0:raise ValueError('Invalid numeric catalogue ID')
    return str(int(value))


def record_id(source,url):
    if not isinstance(source,str) or not re.fullmatch(r'-?[0-9]{1,20}',source):raise ValueError('Invalid stable source ID')
    source=str(int(source))
    if source in {'0','-1'}:raise ValueError('Local/unknown sources have no shared public identity')
    path=source_path(url)
    encoded=base64.urlsafe_b64encode(path.encode('utf-8')).decode('ascii').rstrip('=')
    result=f'asc1:record:tachiyomi:{source}:{encoded}'
    if len(result)>MAX_REFERENCE:raise ValueError('Source reference exceeds the portable ID limit')
    return result


def parse(reference):
    if not isinstance(reference,str) or not 1<=len(reference)<=MAX_REFERENCE:raise ValueError('Enter a complete ASC Media ID')
    parts=reference.split(':')
    if len(parts)==4 and parts[:3]==['asc1','entry','autosourcarr']:
        try:value=str(uuid.UUID(parts[3]))
        except ValueError as exc:raise ValueError('Invalid portable entry identity') from exc
        if value!=parts[3] or value=='00000000-0000-0000-0000-000000000000':raise ValueError('Use a canonical portable entry identity')
        return 'entry','autosourcarr',value,None
    if len(parts)==4 and parts[0]=='asc1' and (PROVIDERS.get(parts[2])==parts[1] or parts[1]=='gallery' and parts[2] in GALLERIES):
        scope,provider,value=parts[1:]
        if _external(provider,value)!=value:raise ValueError('Use the canonical catalogue ID')
        return scope,provider,value,None
    if len(parts)==5 and parts[:3]==['asc1','record','tachiyomi']:
        source,encoded=parts[3:]
        if not re.fullmatch(r'[A-Za-z0-9_-]+',encoded):raise ValueError('Invalid source reference')
        try:path=base64.b64decode(encoded+'='*(-len(encoded)%4),altchars=b'-_',validate=True).decode('utf-8')
        except (ValueError,UnicodeError) as exc:raise ValueError('Invalid source reference') from exc
        if record_id(source,path)!=reference:raise ValueError('Use the canonical source reference')
        return 'record','tachiyomi',source,path
    raise ValueError('Unsupported ASC Media ID namespace')


def gallery_reference(row):
    found=set()
    for url in (row.get('real_url'),row.get('source_manga_url')):
        if not url:continue
        try:
            source_path(url)
            p=urlsplit(urljoin(str(row.get('base_url') or ''),url))
            if p.query or p.scheme not in {'https','http'}:continue
            for provider,(hosts,pattern) in GALLERIES.items():
                match=re.fullmatch(pattern,p.path)
                if p.hostname in hosts and match:
                    value=next(v for v in match.groups() if v)
                    found.add(f'asc1:gallery:{provider}:{_external(provider,value)}')
        except (ValueError,TypeError):continue
    # Conflicting cached real/source URLs are not an automatic cross-reference.
    return next(iter(found)) if len(found)==1 else None


def describe(entry):
    references={}
    for row in entry.get('identities') or []:
        provider=row.get('provider');scope=PROVIDERS.get(provider)
        if not scope or row.get('identity_scope')!=scope:continue
        try:external=_external(provider,row.get('external_id'))
        except (ValueError,TypeError,AttributeError):continue
        key=f'asc1:{scope}:{provider}:{external}'
        references[key]={'id':key,'scope':scope,'provider':provider,'external_id':external,
                         'association':'locked' if row.get('locked')==1 else 'inferred'}
    for row in [*(entry.get('matches') or []),*(entry.get('source_variants') or [])]:
        gallery=gallery_reference(row)
        if gallery:
            _,_,provider,external=gallery.split(':')
            references[gallery]={'id':gallery,'scope':'gallery','provider':provider,'external_id':external,'association':'source URL'}
            continue  # Do not encode gallery access tokens into a source-path ID.
        try:key=record_id(str(row.get('source_id') or ''),row.get('source_manga_url'))
        except (ValueError,TypeError):continue
        references[key]={'id':key,'scope':'record','provider':'tachiyomi','association':'attached source'}
    return {'standard':'ASC Media ID v1','local_entry_id':entry.get('canonical_id',entry.get('id')),
            'references':[references[k] for k in sorted(references)],
            'note':'The same public anchor gives the same ID on every instance. Work IDs do not prove edition or chapter equivalence; inferred catalogue associations need review. Different catalogues need a corroborated cross-reference. Local numeric reader IDs stay unchanged.'}


def resolve(database,service,reference):
    scope,provider,external,path=parse(reference)
    with database.catalog() as c:
        if scope=='entry':
            from .catalog_identity import lookup
            ids=lookup(database,service,reference)
        elif scope=='gallery':
            patterns=(f'%/{external}/%',f'%-{external}.html',f'%/{external}.html')
            ids=set()
            for table in ('source_matches','source_variants'):
                for row in c.execute(f'''SELECT sm.entry_id,sm.source_manga_url,sm.real_url,sc.base_url FROM {table} sm
                        LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.active=1
                        AND (sm.source_manga_url LIKE ? OR sm.source_manga_url LIKE ? OR sm.source_manga_url LIKE ?
                         OR sm.real_url LIKE ? OR sm.real_url LIKE ? OR sm.real_url LIKE ?)''',patterns+patterns):
                    if gallery_reference(dict(row))==reference:ids.add(row['entry_id'])
        elif scope=='record':
            # Exact source ID narrows the lookup; never scan every work or use a
            # server-local source_manga_id as a portable identity.
            rows=[dict(r) for table in ('source_matches','source_variants') for r in c.execute(
                f'''SELECT entry_id,source_manga_url FROM {table} WHERE source_id=? AND active=1
                    AND substr(source_manga_url,-length(?))=?''',(external,path,path))]
            ids=set()
            for row in rows:
                try:
                    if source_path(row['source_manga_url'])==path:ids.add(row['entry_id'])
                except ValueError:pass
        else:
            # Canonical numeric provider IDs tolerate historical zero padding.
            predicate='lower(external_id)=?' if provider=='mangadex' else "(external_id=? OR ltrim(external_id,'0')=?)"
            arguments=(provider,scope,external) if provider=='mangadex' else (provider,scope,external,external)
            rows=c.execute('SELECT entry_id,external_id FROM identity_links WHERE provider=? AND identity_scope=? AND '+predicate,arguments)
            ids=set()
            for row in rows:
                try:
                    if _external(provider,row['external_id'])==external:ids.add(row['entry_id'])
                except ValueError:pass
        canonical=sorted({service.canonical_entry_id(int(i)) for i in ids})
    from .catalog_identity import lookup
    canonical=sorted(set(canonical)|lookup(database,service,reference))
    from .identity_review import lookup_entries
    canonical=lookup_entries(database,service,reference,canonical)
    items=service._entry_summaries(canonical[:50]) if canonical else []
    return {'reference':reference,'items':items,'ambiguous':len(canonical)>1,'total':len(canonical),'truncated':len(canonical)>50,
            'note':'Local associations only. Multiple editions/separate works remain separate; no automatic merge or source request.'}
