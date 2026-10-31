"""Fixed-destination model capability, separate from public repair egress.

Only AI edit jobs receive this per-job Unix socket. Provider credentials remain
in the controller. Build/test/account sandboxes have neither the socket nor key.
"""
from contextlib import contextmanager
import http.client
from http.server import BaseHTTPRequestHandler
import ipaddress
import json
from pathlib import Path
import secrets
import socket
import socketserver
import ssl
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import urlsplit, unquote

MAX_BODY = 8 * 1024 * 1024
PROVIDERS = {'chatgpt','openai','claude','gemini','openrouter','ollama','custom'}

def protocol(config):
    kind = {'openai':'responses','claude':'anthropic','gemini':'chat_completions','openrouter':'chat_completions','ollama':'chat_completions'}.get(config.get('provider'), config.get('protocol', 'responses'))
    if kind not in {'responses','chat_completions','anthropic'}: raise ValueError('Unsupported AI API format.')
    return kind

def routes(config):
    return {'responses':{'/v1/responses','/v1/responses/compact'},'anthropic':{'/v1/messages'},'chat_completions':{'/v1/chat/completions'}}[protocol(config)]

class RelayCleanupUncertain(RuntimeError): pass


def read_provider(root):
    path = Path(root)/'ai-provider.json'
    if not path.exists(): return {'provider': 'chatgpt'}
    if path.is_symlink() or path.stat().st_size > 32768:
        raise ValueError('Repair AI configuration is invalid. Review setup.')
    value = json.loads(path.read_text())
    if not isinstance(value, dict) or value.get('provider') not in PROVIDERS:
        raise ValueError('Choose a supported repair AI connection in setup.')
    if value['provider'] != 'chatgpt':
        target(value); protocol(value)
    return value


def snapshot(root):
    value = read_provider(root)
    return {k:value.get(k, '') for k in ('provider', 'base_url', 'model', 'protocol', 'workspace_id')} | {'key_saved': bool(value.get('api_key'))}


def target(config):
    url = urlsplit(config.get('base_url', ''))
    raw = config.get('base_url', '')
    if (url.scheme not in {'http', 'https'} or not url.hostname or url.username or url.password or url.query or url.fragment
            or any(c.isspace() or ord(c)<32 for c in raw) or any(p in {'.','..'} for p in unquote(url.path).split('/'))):
        raise ValueError('Use an HTTP(S) AI base address without credentials, query or traversal.')
    model = config.get('model', '')
    if not isinstance(model, str) or not model.strip() or len(model)>256 or any(ord(c)<32 for c in model):
        raise ValueError('Enter the model identifier supplied by your AI server.')
    key = config.get('api_key', '')
    if not isinstance(key, str) or len(key)>4096 or any(ord(c)<32 or ord(c)>126 for c in key):
        raise ValueError('The model API key must be a printable single-line value.')
    workspace=config.get('workspace_id','')
    if not isinstance(workspace,str) or len(workspace)>128 or any(not(c.isascii() and (c.isalnum() or c in '_-')) for c in workspace):
        raise ValueError('Use the Claude workspace ID from the provider console, without spaces or header characters.')
    return url, url.port or (443 if url.scheme == 'https' else 80)


def pinned_address(host, port):
    # DNS must also have a deadline. An owned short-lived process can be stopped,
    # unlike an abandoned getaddrinfo thread. No credentials enter its arguments.
    result = subprocess.run([sys.executable, '-I', '-c',
        'import json,socket,sys;print(json.dumps([r[4][0] for r in socket.getaddrinfo(sys.argv[1],int(sys.argv[2]),type=socket.SOCK_STREAM)][:64]))',
        host, str(port)], capture_output=True, timeout=5, check=True)
    addresses = json.loads(result.stdout)
    if not addresses: raise ValueError('AI server address could not be resolved.')
    for address in addresses:
        ip = ipaddress.ip_address(address)
        ip = ip.ipv4_mapped if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped else ip
        if ip.is_link_local or ip.is_multicast or ip.is_unspecified or ip.is_reserved or str(ip) in {'100.100.100.200', 'fd00:ec2::254'}:
            raise ValueError('This AI address is not a supported server destination.')
    return addresses[0]


class Target:
    def __init__(self, config):
        self.config = config
        self.url, self.port = target(config)
        self.address = pinned_address(self.url.hostname, self.port)

    def connection(self, register=lambda stream: None, unregister=lambda stream: None):
        connection = http.client.HTTPConnection(self.url.hostname, self.port, timeout=90)
        def connect():
            stream = socket.socket(socket.AF_INET6 if ':' in self.address else socket.AF_INET, socket.SOCK_STREAM)
            try:
                register(stream)
                stream.settimeout(15)
                stream.connect((self.address, self.port))
                if self.url.scheme == 'https':
                    raw = stream
                    stream = ssl.create_default_context().wrap_socket(raw, server_hostname=self.url.hostname, do_handshake_on_connect=False)
                    register(stream); unregister(raw)
                    stream.do_handshake()
                stream.settimeout(90)
                connection.sock = stream
            except BaseException:
                unregister(stream); stream.close(); raise
        connection.connect = connect
        return connection

    def request(self, connection, route, body):
        headers = {'Content-Type':'application/json', 'Accept':'application/json, text/event-stream'}
        if protocol(self.config) == 'anthropic':
            headers['anthropic-version'] = '2023-06-01'
            if self.config.get('api_key'): headers['x-api-key'] = self.config['api_key']
            if self.config.get('workspace_id'): headers['anthropic-workspace-id'] = self.config['workspace_id']
        elif self.config.get('api_key'): headers['Authorization'] = 'Bearer '+self.config['api_key']
        # All client headers and destinations are discarded. No redirects.
        connection.request('POST', self.url.path.rstrip('/')+route, body, headers)
        return connection.getresponse()


class Relay(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True
    block_on_close = False
    def __init__(self, path, target):
        self.target = target
        self.capacity = threading.BoundedSemaphore(2)
        self.lock = threading.Lock()
        self.active = set()
        self.threads = set()
        self.requests = 0
        self.stopping = False
        super().__init__(str(path), Handler)

    def process_request(self, request, client_address):
        if not self.capacity.acquire(blocking=False):
            request.close(); return
        with self.lock:
            if self.stopping:
                self.capacity.release();request.close();return
            self.active.add(request)
            thread=threading.Thread(target=self.process_request_thread,args=(request,client_address),daemon=True)
            self.threads.add(thread)
            thread.start()

    def process_request_thread(self, request, client_address):
        try: super().process_request_thread(request, client_address)
        finally:
            with self.lock:
                self.active.discard(request)
                self.threads.discard(threading.current_thread())
            self.capacity.release()

    def register(self, stream):
        with self.lock:
            if self.stopping: raise OSError('AI relay stopped')
            self.active.add(stream)

    def unregister(self, stream):
        with self.lock: self.active.discard(stream)

    def stop(self):
        self.shutdown()
        with self.lock:
            self.stopping = True
            for stream in self.active:
                try: stream.shutdown(socket.SHUT_RDWR)
                except OSError: pass
                stream.close()
            threads=list(self.threads)
        self.server_close()
        deadline=time.monotonic()+3
        for thread in threads: thread.join(timeout=max(0,deadline-time.monotonic()))
        if any(thread.is_alive() for thread in threads):
            raise RelayCleanupUncertain('AI connection cleanup could not be confirmed. Restart AutoSourcarr before another repair.')


def error_info(status):
    category,message={
        400:('request','The provider rejected the API format, model or tool parameters. Review setup; Claude multi-workspace keys also need their workspace ID.'),
        401:('credentials','The provider rejected the API credential. Create or replace its API key in setup.'),
        402:('billing','The provider reports missing API credit or a billing requirement. Check its API billing dashboard; a chat subscription may have separate billing.'),
        403:('permission','The API credential lacks permission for this model or request. Check provider access and restrictions.'),
        404:('model-or-endpoint','The model, API endpoint or Claude workspace was not found. Check the exact identifiers and base address.'),
        429:('quota','The provider reports a rate or quota limit. Check its quota dashboard and retry when capacity is available.'),
    }.get(status,('provider','The AI server did not complete the request. Check server availability, network access and the selected API format.'))
    return {'category':category,'message':message,'http_status':status}


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.0'
    def log_message(self, *_): pass
    def log_error(self, *_): pass
    def error(self, status):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        # Never forward provider error text: it may echo prompts or credentials.
        self.wfile.write(json.dumps({'error':error_info(status)}).encode())

    def do_POST(self):
        connection = None
        response = None
        registered = None
        started = False
        try:
            self.connection.settimeout(30)
            if self.path not in routes(self.server.target.config) or self.headers.get('Transfer-Encoding'):
                self.error(400); return
            lengths = self.headers.get_all('Content-Length', [])
            if len(lengths)!=1 or not lengths[0].isdigit() or not 0<int(lengths[0])<=MAX_BODY:
                self.error(413); return
            raw = self.rfile.read(int(lengths[0]))
            if len(raw)!=int(lengths[0]): self.error(400); return
            try: body=json.loads(raw)
            except (ValueError,RecursionError): self.error(400); return
            if not isinstance(body, dict): self.error(400); return
            body['model'] = self.server.target.config['model']
            if self.server.target.config.get('provider')=='openrouter' and body.get('tools'):
                preferences=body.get('provider',{})
                if not isinstance(preferences,dict): self.error(400); return
                body['provider']={**preferences,'require_parameters':True}
            with self.server.lock:
                self.server.requests += 1
                if self.server.stopping or self.server.requests>128: self.error(429); return
            connection = self.server.target.connection(self.server.register, self.server.unregister)
            connection.connect()
            registered = connection.sock
            response = self.server.target.request(connection, self.path.removeprefix('/v1'), json.dumps(body).encode())
            if response.status != 200:
                self.error(response.status if response.status in {400,401,402,403,404,429,500,502,503,504,529} else 502); return
            content_type = response.getheader('Content-Type', '').split(';')[0]
            if content_type not in {'text/event-stream', 'application/json'}: self.error(502); return
            self.send_response(200)
            self.send_header('Content-Type', content_type)
            self.send_header('Connection', 'close')
            self.end_headers(); started=True
            total=0
            while chunk := response.read1(65536):
                total+=len(chunk)
                if total>32*1024*1024: break
                self.wfile.write(chunk); self.wfile.flush()
        except (OSError, ValueError, RecursionError, http.client.HTTPException):
            if not started:
                try: self.error(502)
                except OSError: pass
        finally:
            if response: response.close()
            if connection:
                self.server.unregister(registered)
                connection.close()


@contextmanager
def relay(config):
    destination = Target(config)
    with tempfile.TemporaryDirectory(prefix='asr-ai-') as directory:
        path=Path(directory)/'ai.sock'
        server=Relay(path, destination)
        path.chmod(0o600)
        thread=threading.Thread(target=server.serve_forever, kwargs={'poll_interval':.1}, daemon=True)
        thread.start()
        try: yield path
        finally:
            server.stop(); thread.join(timeout=2)


PROBE_NAME='autosourcarr_connection_token'
PROBE_PROMPT=('Call autosourcarr_connection_token exactly once with no arguments. '
              'It only returns a random diagnostic token; it cannot access files. '
              'After receiving its result, reply with that token exactly, without using another tool.')
PROBE_SCHEMA={'type':'object','properties':{},'required':[],'additionalProperties':False}
CHECK_TIMEOUT=30


def probe_body(kind,model):
    definition={'name':PROBE_NAME,'description':'Return a random connection token. No file, shell or network access.','parameters':PROBE_SCHEMA}
    if kind=='responses':
        return '/v1/responses',{'model':model,'input':[{'role':'user','content':PROBE_PROMPT}],
            'tools':[{'type':'function',**definition}],'tool_choice':'auto','max_output_tokens':2048,'stream':False,'store':False}
    body={'model':model,'messages':[{'role':'user','content':PROBE_PROMPT}],'max_tokens':2048,'stream':False}
    if kind=='anthropic':
        body.update(tools=[{'name':PROBE_NAME,'description':definition['description'],'input_schema':PROBE_SCHEMA}],tool_choice={'type':'auto'})
        return '/v1/messages',body
    body.update(tools=[{'type':'function','function':definition}],tool_choice='auto')
    return '/v1/chat/completions',body


def probe_followup(kind,body,value,token):
    """Validate one inert call; preserve provider reasoning/signatures verbatim."""
    if kind=='responses':
        content=value.get('output')
        if value.get('object')!='response' or not isinstance(content,list) or value.get('status') not in (None,'completed'): return None
        if any(not isinstance(item,dict) or item.get('type') not in ('reasoning','message','function_call') for item in content): return None
        calls=[item for item in content if isinstance(item,dict) and item.get('type')=='function_call']
        if len(calls)!=1: return None
        call=calls[0];ident=call.get('call_id');name=call.get('name');args=call.get('arguments')
    elif kind=='anthropic':
        content=value.get('content')
        if value.get('type')!='message' or not isinstance(content,list) or value.get('stop_reason')!='tool_use': return None
        if any(not isinstance(item,dict) or item.get('type') not in ('text','thinking','redacted_thinking','tool_use') for item in content): return None
        calls=[item for item in content if isinstance(item,dict) and item.get('type')=='tool_use']
        if len(calls)!=1: return None
        call=calls[0];ident=call.get('id');name=call.get('name');args=call.get('input')
    else:
        choices=value.get('choices')
        if not isinstance(choices,list) or len(choices)!=1 or not isinstance(choices[0],dict) or choices[0].get('finish_reason')!='tool_calls': return None
        content=choices[0].get('message')
        if not isinstance(content,dict) or content.get('role')!='assistant': return None
        calls=content.get('tool_calls')
        if not isinstance(calls,list) or len(calls)!=1 or not isinstance(calls[0],dict) or calls[0].get('type')!='function': return None
        call=calls[0];fn=call.get('function')
        if not isinstance(fn,dict): return None
        ident=call.get('id');name=fn.get('name');args=fn.get('arguments')
    if not isinstance(ident,str) or not 0<len(ident)<=256 or name!=PROBE_NAME: return None
    try: args=json.loads(args) if isinstance(args,str) else args
    except (ValueError,RecursionError): return None
    if args!={}: return None
    followup=dict(body)
    if kind=='responses': followup['input']=body['input']+content+[{'type':'function_call_output','call_id':ident,'output':token}]
    elif kind=='anthropic': followup['messages']=body['messages']+[{'role':'assistant','content':content},{'role':'user','content':[{'type':'tool_result','tool_use_id':ident,'content':token}]}]
    else: followup['messages']=body['messages']+[content,{'role':'tool','tool_call_id':ident,'content':token}]
    return followup


def probe_finished(kind,value,token):
    if kind=='responses':
        content=value.get('output')
        if value.get('object')!='response' or value.get('status') not in (None,'completed') or not isinstance(content,list): return False
        if any(not isinstance(b,dict) or b.get('type') not in ('reasoning','message') for b in content): return False
        blocks=[]
        for message in content:
            if message.get('type')=='message':
                if not isinstance(message.get('content'),list): return False
                blocks.extend(message['content'])
        if any(not isinstance(b,dict) or (b.get('type')=='output_text' and not isinstance(b.get('text'),str)) for b in blocks): return False
        text=''.join(b['text'] for b in blocks if b.get('type')=='output_text')
    elif kind=='anthropic':
        content=value.get('content')
        if value.get('type')!='message' or value.get('stop_reason')!='end_turn' or not isinstance(content,list): return False
        if any(not isinstance(b,dict) or b.get('type') not in ('text','thinking','redacted_thinking') or (b.get('type')=='text' and not isinstance(b.get('text'),str)) for b in content): return False
        text=''.join(b['text'] for b in content if b.get('type')=='text')
    else:
        choices=value.get('choices')
        if not isinstance(choices,list) or len(choices)!=1 or not isinstance(choices[0],dict) or choices[0].get('finish_reason')!='stop': return False
        message=choices[0].get('message')
        if not isinstance(message,dict) or message.get('role')!='assistant' or message.get('tool_calls'): return False
        text=message.get('content')
    return isinstance(text,str) and text.strip()==token


def relay_connection(path,timeout):
    connection=http.client.HTTPConnection('relay',timeout=timeout)
    def connect():
        stream=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
        connection.sock=stream  # Owned before connect, so the deadline can stop it.
        stream.settimeout(timeout);stream.connect(str(path))
    connection.connect=connect
    return connection


def check(root):
    deadline=time.monotonic()+CHECK_TIMEOUT
    config={};api_ok=False
    try:
        config=read_provider(root)
        if config['provider']=='chatgpt': return {'ok':False,'api_ok':False,'tools_ok':False,'reason':'account','message':'Use ChatGPT sign-in for the selected account connection. Account setup is independent of source repair isolation; check repair tools before running source code.'}
        with relay(config) as path:
            connection=None;active_stream=None;response=None
            def interrupt():
                try:
                    stream=active_stream or (connection.sock if connection is not None else None)
                    if stream is not None: stream.shutdown(socket.SHUT_RDWR)
                except OSError: pass
            timer=threading.Timer(max(.01,deadline-time.monotonic()),interrupt)
            timer.daemon=True;timer.start()
            try:
                kind=protocol(config)
                route,body=probe_body(kind,config['model']);token=secrets.token_hex(16)
                for step in range(2):
                    if time.monotonic()>=deadline: raise TimeoutError('AI check deadline')
                    # The relay closes HTTP/1.0 responses; each turn owns a new stream.
                    active_stream=None;response=None
                    connection=relay_connection(path,max(.01,deadline-time.monotonic()))
                    connection.request('POST',route,json.dumps(body),{'Content-Type':'application/json'})
                    active_stream=connection.sock
                    response=connection.getresponse()
                    raw=response.read(1024*1024+1)
                    if time.monotonic()>=deadline: raise TimeoutError('AI check deadline')
                    response.close()
                    connection.close()
                    if response.status!=200:
                        info=error_info(response.status)
                        return {'ok':False,'api_ok':api_ok,'tools_ok':False,'reason':info['category'],'http_status':response.status,'message':info['message']}
                    try: value=json.loads(raw) if len(raw)<=1024*1024 else {}
                    except (ValueError,RecursionError):
                        return {'ok':False,'api_ok':True,'tools_ok':False,'reason':'api-format','message':'The API answered with an unexpected response. Check the base address and selected API format; no source files were accessed.'}
                    value=value if isinstance(value,dict) else {}
                    api_ok=True
                    if not step:
                        body=probe_followup(kind,body,value,token)
                        if body is None: break
                    elif probe_finished(kind,value,token):
                        return {'ok':True,'api_ok':True,'tools_ok':True,'message':'AI tool call and result round trip passed. No source files were accessed. Check repair tools separately; repairs still require isolation, compilation and functional validation.'}
                return {'ok':False,'api_ok':api_ok,'tools_ok':False,'reason':'tool-support','message':'The API answered, but its tool call and result round trip could not be verified. Choose a tool-capable coding model and the correct API format; no source files were accessed.'}
            finally:
                timer.cancel();timer.join(timeout=1)
                if response is not None: response.close()
                if connection is not None: connection.close()
                if active_stream is not None: active_stream.close()
    except (OSError, ValueError, RecursionError, http.client.HTTPException, subprocess.SubprocessError):
        message='AI connection check could not complete within its limit. Check the server address, DNS/TLS and API format in setup.'
        if config.get('provider')=='ollama':
            message+=' For Ollama, use the model server’s reachable LAN IP or service name and ensure it listens there. Localhost means this container; host.docker.internal is not automatic on TrueNAS/Linux.'
        return {'ok':False,'api_ok':api_ok,'tools_ok':False,'reason':'connection','message':message}
