"""Bounded public website observations, executed only inside the repair namespace.

No source cookies, account files, response bodies or arbitrary request headers are
returned. Every connection uses the existing public-IP-pinning egress proxy.
"""
import ipaddress
import json
from html.parser import HTMLParser
from pathlib import Path
import re
import sys
import time
from urllib.parse import urljoin, urlsplit, urlunsplit

import httpx

MAX_BODY = 128 * 1024
MAX_REQUESTS = 16
MAX_SITES = 8
REDIRECTS = {301, 302, 303, 307, 308}


def public_url(value):
    if not isinstance(value, str) or not value or len(value) > 2048 or any(c.isspace() or ord(c) < 32 for c in value):
        raise ValueError('Invalid public website address')
    parsed = urlsplit(value)
    if parsed.scheme not in {'http','https'} or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError('Website addresses cannot contain credentials, query values or fragments')
    host = parsed.hostname.lower().encode('idna').decode('ascii').rstrip('.')
    if host == 'localhost' or host.endswith(('.localhost','.local')):
        raise ValueError('Local website addresses are not allowed')
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        address = None
    if address is not None and (not address.is_global or address.is_multicast or address.is_reserved):
        raise ValueError('Private/reserved website addresses are not allowed')
    if isinstance(address,ipaddress.IPv6Address) and address.ipv4_mapped and not address.ipv4_mapped.is_global:
        raise ValueError('Private mapped addresses are not allowed')
    port = parsed.port or (443 if parsed.scheme == 'https' else 80)
    if port not in {80,443}:
        raise ValueError('Only standard public HTTP/HTTPS ports are allowed')
    authority = '['+host+']' if ':' in host else host
    if port != (443 if parsed.scheme == 'https' else 80): authority += ':'+str(port)
    return urlunsplit((parsed.scheme,authority,parsed.path or '/','',''))


def origin(value):
    parsed = urlsplit(public_url(value))
    return parsed.scheme+'://'+parsed.netloc


class ApiLinks(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.urls = []

    def handle_starttag(self, tag, attrs):
        if tag.lower() != 'link' or len(self.urls) >= 4: return
        values = dict(attrs)
        if values.get('rel') == 'https://api.w.org/' and isinstance(values.get('href'),str):
            self.urls.append(values['href'])


class WebsiteProbe:
    def __init__(self, client, *, pace=3.0, deadline=60.0, clock=time.monotonic, sleep=time.sleep):
        self.client,self.pace,self.clock,self.sleep = client,pace,clock,sleep
        self.deadline = clock()+deadline
        self.last = None
        self.requests = 0

    def response(self, url, *, accept=None):
        if accept is not None and (not isinstance(accept,str) or accept!='application/vnd.github.sha'):raise ValueError('Unsupported public observation response type')
        if self.requests >= MAX_REQUESTS or self.clock() >= self.deadline:
            raise TimeoutError('Website observation budget reached')
        if self.last is not None:
            self.sleep(max(0,self.last+self.pace-self.clock()))
        if self.clock() >= self.deadline: raise TimeoutError('Website observation deadline reached')
        self.requests += 1; self.last = self.clock()
        # A public response must not introduce cookies into subsequent probes.
        self.client.cookies.clear()
        headers={'Accept-Encoding':'identity'}
        if accept:headers['Accept']=accept
        with self.client.stream('GET',url,headers=headers,timeout=10,follow_redirects=False) as response:
            status = response.status_code
            location = response.headers.get('location','')
            kind = response.headers.get('content-type','').split(';',1)[0].lower().strip()
            # iter_raw avoids the unbounded transparent-decoder allocation that a
            # returned-size cap alone would not prevent. No decompression occurs.
            encoded = response.headers.get('content-encoding','identity').lower().strip()
            body = bytearray()
            if encoded in {'','identity'} and status not in REDIRECTS:
                for chunk in response.iter_raw(chunk_size=16384):
                    if self.clock() >= self.deadline: raise TimeoutError('Website observation deadline reached')
                    remaining = MAX_BODY-len(body)
                    body.extend(chunk[:remaining])
                    if len(chunk) > remaining or len(body) == MAX_BODY: break
            return status,location,kind,bytes(body),encoded in {'','identity'}

    def one(self, value, *, allowed_origin=None):
        try: requested = public_url(value)
        except (ValueError,UnicodeError): return {'state':'inconclusive','reason':'invalid-public-address'}
        current,chain = requested,[]
        try:
            for _ in range(5):
                status,location,kind,body,readable = self.response(current)
                if status in REDIRECTS:
                    target = public_url(urljoin(current,location))
                    if allowed_origin is not None and origin(target)!=allowed_origin:
                        raise ValueError('API redirect changed origin')
                    if not location or target in [requested,*chain]:
                        raise ValueError('Redirect loop or missing target')
                    if urlsplit(current).scheme == 'https' and urlsplit(target).scheme != 'https':
                        raise ValueError('HTTPS downgrade is not allowed')
                    chain.append(target);current=target;continue
                text = body.decode('utf-8',errors='replace').lower()
                challenge = any(word in text for word in ('cf-chl-','cloudflare challenge','checking your browser before accessing')) or bool(
                    re.search(r'<title[^>]*>\s*(?:just a moment|attention required|captcha|human verification|verify you are human)',text)) or (
                    status in {401,403,503} and any(word in text for word in ('g-recaptcha','hcaptcha','verify you are human')))
                state = 'rate_limited' if status==429 else 'verification' if challenge else 'not_found' if status in {404,410} else (
                    'working' if 200<=status<300 and readable and kind in {'text/html','application/json','application/ld+json'} and body else 'inconclusive')
                result = {'requested_url':requested,'final_url':current,'redirects':chain,'http_status':status,'state':state,
                          'content_kind':'json' if 'json' in kind else 'html' if kind=='text/html' else 'other'}
                if not readable:result['reason']='encoded-response-not-decoded'
                if state=='working' and kind=='text/html':
                    links=ApiLinks();links.feed(body.decode('utf-8',errors='replace'))
                    targets=[]
                    for candidate in links.urls:
                        try: candidate=public_url(urljoin(current,candidate))
                        except (ValueError,UnicodeError): continue
                        if origin(candidate)==origin(current) and candidate not in targets:targets.append(candidate)
                    result['_api_targets']=targets[:2]
                if kind in {'application/json','application/ld+json'} and state=='working':
                    try: valid=isinstance(json.loads(body), (dict,list))
                    except (ValueError,RecursionError):valid=False
                    if not valid:result['state']='inconclusive';result['reason']='invalid-or-truncated-json'
                return result
            raise ValueError('Redirect limit reached')
        except (ValueError,UnicodeError,httpx.HTTPError,TimeoutError):
            return {'requested_url':requested,'final_url':current,'redirects':chain,'state':'inconclusive','reason':'connection-redirect-or-budget'}

    def check(self, sources):
        grouped={}
        for source in sources[:200]:
            value=source.get('baseUrl') if isinstance(source,dict) else None
            if not isinstance(value,str) or not value:continue
            try:key=public_url(value)
            except (ValueError,UnicodeError):continue
            if key not in grouped and len(grouped)>=MAX_SITES:continue
            sid=str(source.get('id',''))
            if re.fullmatch(r'-?[0-9]{1,20}',sid):grouped.setdefault(key,[]).append(sid)
        result=[]
        for value,ids in grouped.items():
            item=self.one(value);item['source_ids']=ids[:200]
            targets=item.pop('_api_targets',[])
            item['apis']=[]
            for target in targets:
                api=self.one(target,allowed_origin=origin(item.get('final_url',value)));api.pop('_api_targets',None)
                # An API redirect is observation only; never authorize another
                # origin from an advertised link on a page.
                if api.get('final_url') and origin(api['final_url'])!=origin(item.get('final_url',value)):
                    api['state']='inconclusive';api['reason']='api-origin-changed'
                item['apis'].append(api)
            result.append(item)
        return result


def main():
    path=Path('/work/targets.json')
    with path.open(encoding='utf-8') as stream: raw=stream.read(65537)
    if len(raw)>65536:raise ValueError('Website target limit')
    sources=json.loads(raw)
    if not isinstance(sources,list) or len(sources)>200:raise ValueError('Invalid website targets')
    with httpx.Client(proxy='http://127.0.0.1:8080',trust_env=False,verify=True,
                      headers={'User-Agent':'AutoSourcarr source repair website check'},
                      limits=httpx.Limits(max_connections=1,max_keepalive_connections=1)) as client:
        result=WebsiteProbe(client).check(sources)
    print(json.dumps(result,separators=(',',':')))


if __name__=='__main__':
    try:main()
    except (ValueError,OSError,UnicodeError):
        print('Website targets could not be checked safely.',file=sys.stderr);raise SystemExit(1)
