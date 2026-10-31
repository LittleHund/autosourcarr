"""Bounded original-maintainer and public-search observations inside isolation.

Search results are leads only. Changed addresses become repair targets only when
the original source repository, pinned commit and same source file support them.
First-party HTTPS redirect plus that independent maintainer reference can support
automatic promotion; content/source identity validation is still mandatory.
"""
from html.parser import HTMLParser
import json
from pathlib import Path
import re
from urllib.parse import quote,urlsplit,parse_qs

import httpx

from source_tools.ecosystem import repository_plan
from source_tools.archive import relative_name
from website_probe import WebsiteProbe,public_url,origin

BASE=re.compile(r'\bbaseUrl\s*(?::\s*[A-Za-z0-9_?.<>]+\s*)?=\s*["\'](https?://[^"\'\s]+)["\']')
GETTER=re.compile(r'\bgetBaseUrl\s*\(\s*\)\s*\{[^{}]{0,1000}?\breturn\s*["\'](https?://[^"\'\s]+)["\']\s*;',re.S)


def base_addresses(text):
    result=[]
    for value in [*BASE.findall(text),*GETTER.findall(text)]:
        try:value=public_url(value)
        except (ValueError,UnicodeError):continue
        if value not in result:result.append(value)
    return result[:8]


class SearchLinks(HTMLParser):
    def __init__(self):super().__init__(convert_charrefs=True);self.links=[]
    def handle_starttag(self,tag,attrs):
        if tag!='a' or len(self.links)>=8:return
        values=dict(attrs);href=values.get('href') or ''
        if 'result__a' not in (values.get('class') or ''):return
        parsed=urlsplit('https:'+href if href.startswith('//') else href)
        if parsed.hostname and parsed.hostname.endswith('duckduckgo.com'):
            href=parse_qs(parsed.query).get('uddg',[''])[0]
        try:value=public_url(href)
        except (ValueError,UnicodeError):return
        if value not in self.links:self.links.append(value)


def research(inputs,probe):
    info=inputs['raw_meta'];repo=info.get('origin',{}).get('repository');evidence=[];targets=[];leads=[]
    source_files=inputs.get('files',[])[:6];head='';plan=None
    try:
        if repo:
            plan=repository_plan(repo)
            status,_,kind,body,readable=probe.response(plan['resolve_url'],accept='application/vnd.github.sha' if plan['host']=='github.com' else None)
            head=body.strip().decode('ascii') if status==200 and readable and re.fullmatch(rb'[0-9a-f]{40}',body.strip()) else ''
            if not re.fullmatch(r'[0-9a-f]{40}',head):
                decoded=json.loads(body) if status==200 and readable else {}
                head=decoded.get('sha' if plan['host']=='github.com' else 'id','')
            if not isinstance(head,str) or not re.fullmatch(r'[0-9a-f]{40}',head):head=''
    except (ValueError,KeyError,TypeError,RecursionError,httpx.HTTPError,TimeoutError):pass
    for row in source_files if head else []:
        name=relative_name(row['path'])
        if not name.endswith(('.kt','.java','.gradle.kts','.gradle')):continue
        if plan['host']=='github.com':url='https://raw.githubusercontent.com/'+plan['path']+'/'+head+'/'+name
        else:url=plan['repository']+'/-/raw/'+head+'/'+name
        try:
            status,_,_,body,readable=probe.response(url)
            if status!=200 or not readable:continue
            candidates=base_addresses(body.decode('utf-8',errors='replace'))
            # One corresponding address in this exact source file. Multi-site
            # factories need explicit evidence rather than a broad URL guess.
            if len(candidates)!=1:continue
            new=candidates[0]
            for source in inputs['sources'][:200]:
                try:old=public_url(source.get('baseUrl'))
                except (ValueError,UnicodeError):continue
                if old not in row.get('addresses',[]) or origin(old)==origin(new):continue
                observed=probe.one(new);observed.pop('_api_targets',None)
                if observed.get('state')!='working' or origin(observed.get('final_url',new))!=origin(new):continue
                redirect=next((value for value in inputs.get('website_checks',[])
                    if str(source['id']) in value.get('source_ids',[]) and value.get('state')=='working'
                    and value.get('requested_url','').startswith('https://') and value.get('final_url')==new
                    and all(url.startswith('https://') for url in value.get('redirects',[]))),None)
                original=info.get('origin',{})
                recognized=(info.get('kind') in {'upstream','reconstructed','repaired'}
                    and original.get('repository')=='https://github.com/keiyoushi/extensions-source'
                    and original.get('basis') in {'known-repository','binary-reconstruction'})
                target={'source_id':str(source['id']),'previous_url':old,'new_url':new,'basis':'original-maintainer-code',
                    'source_revision':head,'evidence_url':url,'trust':'corroborated' if redirect and recognized else 'review-required'}
                if not any(value['source_id']==target['source_id'] and value['new_url']==new for value in targets):targets.append(target)
                evidence.append({'state':'observed','url':url,'revision':head,'new_url':new,'trust':target['trust']})
        except (ValueError,KeyError,httpx.HTTPError,TimeoutError):continue
    # Public search is used only for unavailable/inconclusive original sites.
    # A result cannot grant permission to redirect source traffic to a mirror.
    for row in inputs.get('website_checks',[])[:2]:
        if row.get('state') not in {'not_found','inconclusive'}:continue
        try:
            old=public_url(row.get('requested_url'));host=urlsplit(old).hostname
            query=quote(host+' '+inputs['package'].split('.')[-1]+' official website moved',safe='')
            url='https://html.duckduckgo.com/html/?q='+query
            status,_,_,body,readable=probe.response(url)
            if status==200 and readable:
                parser=SearchLinks();parser.feed(body.decode('utf-8',errors='replace'))
                leads.extend({'url':value,'basis':'public-search-lead','trust':'unverified'} for value in parser.links[:4])
            evidence.append({'state':'searched' if status==200 else 'unavailable','provider':'DuckDuckGo','for_origin':origin(old),'http_status':status})
        except (ValueError,httpx.HTTPError,TimeoutError):continue
    return {'targets':targets[:16],'evidence':evidence[:16],'leads':leads[:8],
            'note':'Search leads are unverified. TLS/availability alone does not prove ownership; ambiguous moves require review.'}


def main():
    payload=Path('/work/research.json').read_bytes()
    if len(payload)>128*1024:raise ValueError('Research input exceeds its limit')
    inputs=json.loads(payload)
    with httpx.Client(proxy='http://127.0.0.1:8080',trust_env=False,verify=True,
        limits=httpx.Limits(max_connections=1,max_keepalive_connections=1),headers={'User-Agent':'AutoSourcarr source address research'}) as client:
        print(json.dumps(research(inputs,WebsiteProbe(client,deadline=80)),separators=(',',':')))


if __name__=='__main__':main()
