"""Find source declarations from an extension repository, never a web search.

Declarations locate candidate code, not the original bytes of a manual install.
Only bounded HTTPS reads through the controller's pinned public fetcher are used.
"""
import json
import http.client
import re
import time
from urllib.parse import urlsplit
from .ecosystem import KEIYOUSHI_SOURCE,_known_index,module_hint,repository_plan
from .archive import package_name,public_reference


def index_project(value):
    public_reference(value);p=urlsplit(value);parts=p.path.strip('/').split('/')
    if p.hostname=='github.com' and len(parts)>=5 and parts[2]=='raw':
        if parts[3:5]==['refs','heads']:
            if len(parts)<7:return None
            branch=parts[5]
        else:branch=parts[3]
        owner,repo=parts[:2]
    elif p.hostname=='raw.githubusercontent.com' and len(parts)>=4:owner,repo,branch=parts[:3]
    else:return None
    repository_plan('https://github.com/'+owner+'/'+repo)
    if not re.fullmatch(r'[A-Za-z0-9_.-]+',branch):return None
    if parts[-1] not in {'index.pb','index.json','index.min.json'}:return None
    base='https://raw.githubusercontent.com/'+owner+'/'+repo+'/'+branch+'/'
    return {'owner':owner,'repository':'https://github.com/'+owner+'/'+repo,'readme':base+'README.md','index':base+'index.min.json'}


def declared_source(index_url,fetch,*,deadline=None):
    """Recognised mapping or one explicit same-publisher Source Code section."""
    if _known_index(index_url):return {'repository':KEIYOUSHI_SOURCE,'ecosystem':'keiyoushi','declaration':'https://github.com/keiyoushi/extensions'}
    project=index_project(index_url)
    if not project:return None
    body,resolved=fetch(project['readme'],max_bytes=128*1024,deadline=deadline or time.monotonic()+8)
    # Redirecting a README to a different publisher cannot assert code ownership.
    rp=urlsplit(resolved)
    if rp.hostname!='raw.githubusercontent.com' or rp.path.split('/')[1:3]!=urlsplit(project['readme']).path.split('/')[1:3]:return None
    text=body.decode('utf-8');sections=re.split(r'(?m)^#{1,6}\s+',text)
    candidates=set()
    for section in sections:
        title,_,content=section.partition('\n')
        if not re.fullmatch(r'(?:source(?:\s+code)?|source\s+repository)',title.strip(),re.I):continue
        for link in re.findall(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?',content):
            plan=repository_plan(link)
            if plan['path'].split('/')[0].casefold()==project['owner'].casefold():candidates.add(plan['repository'])
    if len(candidates)==1:return {'repository':next(iter(candidates)),'ecosystem':'tachiyomi','declaration':project['readme']}
    return None


def discover(extension,index_urls,fetch):
    """A configured index needs exact package presence before it can nominate code.

    An installed extension's declared index is already repository-family evidence;
    absent/current-version differences still never establish binary correspondence.
    """
    pkg=package_name(extension.get('pkgName'));declared=extension.get('storeIndexUrl');found={};errors=[]
    urls=list(dict.fromkeys([u for u in [declared,*index_urls] if isinstance(u,str) and u]))[:5]
    for index in urls:
        try:
            project=index_project(index)
            if not project:continue
            if index!=declared:
                body,_=fetch(project['index'],max_bytes=3*1024*1024,deadline=time.monotonic()+8)
                rows=json.loads(body)
                if not isinstance(rows,list) or not any(isinstance(r,dict) and r.get('pkg')==pkg for r in rows):continue
            source=declared_source(index,fetch)
            if source:found[source['repository']]={**source,'index_url':index}
        except (ValueError,OSError,UnicodeError,RecursionError,http.client.HTTPException) as exc:errors.append(type(exc).__name__)
    if len(found)!=1:
        return {'state':'needs-input','reason':('Multiple repositories advertise this package. Choose its original code repository.' if len(found)>1 else
            'No unambiguous code declaration was found for this installed package. Select an upstream project below, provide its ZIP, or choose Unavailable.'),'checked':len(urls),'errors':errors}
    source=next(iter(found.values()))
    return {'package':pkg,'version':extension['versionName'],'module':module_hint(pkg),'state':'known',
            'repository':source['repository'],'ecosystem':source['ecosystem'],'revision':'',
            'index_url':source['index_url'],'basis':'repository-candidate'}
