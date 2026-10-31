"""Same repair contracts, builds and functional gates, using isolated local jobs."""
import asyncio
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import subprocess
import shutil
import stat
import tempfile
import time
import zipfile

import httpx

from control import Control
from contracts import safe_text
from internal_sandbox import Sandbox, SyncPipe, terminate_group, owned_directory
from runner import Runner
from resource_budget import Watchdog
from validate import SourceBlocked, Validator

PROXY_JAVA = '-Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=8080 -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=8080 -Dhttp.nonProxyHosts=localhost|127.*'
ACCOUNT_COMMAND = ('/opt/node/bin/codex-native',)


class AccountLifetime(SyncPipe):
    """Receipt plus EOF from a trusted supervisor, not a namespace witness."""
    def attach(self, args):
        tools = Path(__file__).resolve().parent
        return ['/usr/bin/python3', '-I', str(tools/'resource_budget.py'), '--trusted-account', '--',
                '/usr/bin/python3', '-I', str(tools/'account_process.py'),
                str(self.write_fd), str(os.getpid()), '--', *args]

    def ready(self, timeout=5.0):
        import select
        from account_process import READY
        deadline=time.monotonic()+timeout
        received=bytearray()
        while len(received)<len(READY):
            remaining=deadline-time.monotonic()
            if remaining<=0 or not select.select([self.read_fd],[],[],remaining)[0]:
                raise RuntimeError('Account supervisor did not become ready')
            byte=os.read(self.read_fd,1)
            if not byte: raise RuntimeError('Account supervisor could not start')
            received.extend(byte)
        if received!=READY: raise RuntimeError('Invalid account supervisor handshake')

    def wait_eof(self, timeout=10.0):
        import select
        from account_process import MAX_RECEIPT, parse_receipt
        if getattr(self,'_confirmed',False):return
        if not self._spawned or self.read_fd is None:
            raise RuntimeError('Account process lifetime was not handed off')
        deadline = time.monotonic()+timeout
        received = bytearray()
        while True:
            remaining = deadline-time.monotonic()
            if remaining <= 0 or not select.select([self.read_fd], [], [], remaining)[0]:
                raise RuntimeError('Account processes could not be confirmed stopped')
            chunk = os.read(self.read_fd, 256)
            if not chunk:
                self.failure=parse_receipt(bytes(received))
                self._confirmed=True
                return
            received.extend(chunk)
            if len(received) > MAX_RECEIPT:
                raise RuntimeError('Invalid account cleanup receipt')


def private_account_auth(saved):
    """Copy only validated file credentials, never persistent Codex settings."""
    saved = owned_directory(saved)
    try:
        fd = os.open(saved/'auth.json', os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    except FileNotFoundError:
        return None
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= 1048576:
            raise ValueError('Invalid saved credential file')
        value = json.loads(stream.read(1048577))
    tokens = value.get('tokens') if isinstance(value, dict) else None
    if not isinstance(tokens, dict) or value.get('auth_mode', 'chatgpt') != 'chatgpt' or value.get('OPENAI_API_KEY'):
        raise ValueError('Saved credentials are not a ChatGPT account')
    if not all(isinstance(tokens.get(key), str) and tokens[key].strip() for key in ('id_token', 'access_token', 'refresh_token')):
        raise ValueError('Incomplete saved credentials')
    # Codex file schema; drop unknown top-level/nested settings from prior work.
    copied = {'auth_mode': 'chatgpt', 'OPENAI_API_KEY': None,
              'tokens': {key: tokens[key] for key in ('id_token', 'access_token', 'refresh_token')}}
    if isinstance(tokens.get('account_id'), str): copied['tokens']['account_id'] = tokens['account_id']
    if isinstance(value.get('last_refresh'), str): copied['last_refresh'] = value['last_refresh']
    return json.dumps(copied).encode()


def private_login_errors(home):
    """Read only this operation's bounded, owned regular login log for classification.

    Text stays private: account_failure publishes fixed categories, never this log.
    Never follow a symlink, open a FIFO or inspect the persistent account's files.
    """
    snippets = []
    try:
        home = owned_directory(home)
        root = os.open(home, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    except (OSError, ValueError):
        return ''
    try:
        for nested in (True, False):
            folder = root
            try:
                if nested: folder = os.open('log', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=root)
                fd = os.open('codex-login.log', os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=folder)
                with os.fdopen(fd, 'rb') as stream:
                    info = os.fstat(stream.fileno())
                    if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid(): continue
                    stream.seek(max(0, info.st_size-8192))
                    snippets.append(stream.read(8192).decode(errors='replace'))
            except OSError:
                continue
            finally:
                if folder!=root: os.close(folder)
    finally:
        os.close(root)
    return '\n'.join(snippets)


def read_converted_jar(profile, extension):
    """Read only an ordinary converted file below this isolated scratch profile."""
    name = extension.get('apkName')
    if not isinstance(name, str) or len(name) > 240 or not re.fullmatch(r'[A-Za-z0-9_.+\-]+\.apk', name):
        raise ValueError('Isolated source engine returned an invalid package filename')
    descriptors = []
    try:
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
        root = os.open(profile, flags); descriptors.append(root)
        directory = os.open('extensions', flags, dir_fd=root); descriptors.append(directory)
        descriptor = os.open(name[:-4]+'.jar', os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=directory)
        descriptors.append(descriptor)
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 100 < info.st_size <= 32*1024*1024:
            raise ValueError('Isolated converted package is unsafe or oversized')
        with os.fdopen(descriptor, 'rb') as stream:
            descriptors.pop()  # stream owns the file; directories remain pinned.
            data = stream.read(32*1024*1024 + 1)
        if len(data) > 32*1024*1024:
            raise ValueError('Isolated converted package exceeded its size limit')
        return data
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


class InternalRunner(Runner):
    converted_baseline_supported = True
    raw_snapshot_supported = True
    def __init__(self, root):
        super().__init__(root, root, '', owner=None)
        self.sandbox = Sandbox(Path(__file__).resolve().parent,
            tool_roots=('/opt/java','/opt/build-jdk','/opt/android-sdk-linux','/opt/autosourcarr-venv','/opt/node','/opt/source-jvm'))
        self.socket_temp = tempfile.TemporaryDirectory(prefix='asr-')
        self.sockets = Path(self.socket_temp.name)
        self.cleanup_failed = False
        self.isolation = {'available': False, 'reason':'not-checked', 'message':'Select Check repair tools before starting a repair'}

    def probe(self):
        self.isolation = self.sandbox.probe()
        return self.isolation

    def require_isolation(self):
        if self.cleanup_failed: raise SourceBlocked("A prior sandbox could not be confirmed stopped. Restart this AutoSourcarr instance before another repair.")
        if not self.isolation.get('available'): self.probe()
        if not self.isolation.get('available'): raise SourceBlocked(self.isolation['message'])

    def cleanup_interrupted(self):
        # Child PID namespaces die with the old worker. Persisted jobs never replay.
        for path in (self.root/'jobs').glob('*/job.json'):
            job = json.loads(path.read_text())
            if job['state'] not in {'ready','installed','failed','blocked','cancelled','interrupted','discarded','awaiting-raws','raws-retained'}:
                self.save(job,'interrupted','AutoSourcarr restarted. No automatic replay; start a new repair when ready.')

    def command(self, job, command, *, timeout=900, cwd=None):
        self.check_cancelled(job)
        log = self.path(job['id'])/'command.log'
        lifetime = SyncPipe() if Path(command[0]).name=='bwrap' else None
        try:
            with log.open('wb') as output:
                process = subprocess.Popen(lifetime.attach(command) if lifetime else command,cwd=cwd,stdout=output,stderr=subprocess.STDOUT,
                    env={'PATH':os.environ['PATH'],'HOME':'/tmp','GIT_TERMINAL_PROMPT':'0','GIT_CONFIG_NOSYSTEM':'1'},
                    pass_fds=lifetime.pass_fds if lifetime else (),start_new_session=True)
                if lifetime: lifetime.spawned()
                watchdog = Watchdog(process.pid, self.path(job['id'])) if lifetime else None
                deadline = time.monotonic()+timeout
                try:
                    while process.poll() is None:
                        self.check_cancelled(job)
                        if watchdog:
                            try: watchdog.check()
                            except Exception:
                                if process.poll() is not None: break
                                raise
                        if time.monotonic()>deadline: raise TimeoutError('Repair step timed out; live source unchanged')
                        if log.stat().st_size > 16*1024*1024: raise RuntimeError('Repair step exceeded its log limit')
                        time.sleep(.2)
                finally:
                    terminate_group(process)
                    if lifetime:
                        try: lifetime.wait_eof()
                        except RuntimeError:
                            self.cleanup_failed=True;raise
        finally:
            if lifetime: lifetime.close()
        raw = log.read_text(errors='replace')
        if process.returncode: raise RuntimeError(safe_text(raw,6000) or 'Repair step failed')
        return raw

    def compile_tree(self, job, build):
        self.require_isolation()
        if job.get('raw_meta',{}).get('recipe')=='jvm':
            shutil.copyfile(self.path(job['id'])/'validation-baseline.jar',build/'.repair-input.jar')
            (build/'.repair-build.json').write_text(json.dumps({'changed_files':job.get('changed_files')}))
            args=self.sandbox.command(build,['/opt/autosourcarr-venv/bin/python','/sandbox-tools/jvm_compile.py'],
                env={'JAVA_HOME':'/opt/build-jdk','PATH':'/opt/build-jdk/bin:/usr/bin:/bin'},bridge=False)
            self.command(job,args,timeout=660)
            return
        args = self.sandbox.command(build,['/bin/bash','./gradlew','--no-daemon','--max-workers=2','--no-parallel','-Dorg.gradle.jvmargs=-Xmx1536m -XX:ActiveProcessorCount=2',('' if job['module']=='.' else ':'+job['module'].replace('/',':'))+':assembleRelease'],
            sockets=self.sockets,env={'JAVA_HOME':'/opt/build-jdk','ANDROID_HOME':'/opt/android-sdk-linux',
                'ANDROID_SDK_ROOT':'/opt/android-sdk-linux','GRADLE_USER_HOME':'/work/.gradle-cache',
                'JAVA_TOOL_OPTIONS':'-XX:ActiveProcessorCount=2 -Xmx1536m '+PROXY_JAVA,
                'GRADLE_OPTS':PROXY_JAVA,'PATH':'/opt/build-jdk/bin:/usr/bin:/bin'})
        self.command(job,args,timeout=1200)

    def native_from_apk(self,job,apk):
        from baseline_manifest import installed_envelope,manifest_contract
        from contracts import digest
        identity=manifest_contract(apk)
        if identity[0]!=job['pkg_name']:raise SourceBlocked('Compiled original APK belongs to another package')
        with self.validation_engine(job) as validator:
            self.wait_engine(job,validator);validator.install(apk,job['pkg_name'],apk=True)
            converted=validator.converted_jar_reader(validator.installed_extension)
            envelope,actual,proof=installed_envelope(converted,apk)
        if actual!=identity:raise SourceBlocked('Compiled APK conversion changed its source loading contract')
        job['built_apk_sha256']=digest(apk);job['built_conversion']=proof
        return envelope

    def reconstruct_code(self,job,baseline,original):
        self.require_isolation()
        from baseline_manifest import _payloads
        _payloads(baseline)
        with tempfile.TemporaryDirectory(prefix='decompile-',dir=self.path(job['id'])) as temporary:
            work=Path(temporary);(work/'source.jar').write_bytes(baseline)
            args=self.sandbox.command(work,['/opt/build-jdk/bin/java','-Xmx1024m','-jar','/opt/source-jvm/cfr-0.152.jar',
                '/work/source.jar','--outputdir','/work/src','--silent','true'],bridge=False)
            self.command(job,args,timeout=600)
            from contracts import tree_manifest
            tree_manifest(work/'src')
            original.mkdir();shutil.copytree(work/'src',original/'src')
            with zipfile.ZipFile(__import__('io').BytesIO(baseline)) as archive:
                for info in archive.infolist():
                    if info.is_dir() or info.filename.endswith(('.class','.dex')) or info.filename=='AndroidManifest.xml' or info.filename.startswith('META-INF/'):continue
                    target=original/'resources'/info.filename;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(archive.read(info))
            (original/'RECONSTRUCTION.txt').write_text('Derived from installed JVM bytecode using CFR 0.152. Comments, Kotlin syntax and debug names may be lost. Unchanged classes/resources remain in the private binary baseline; a successful compile and functional checks are still required.\n')

    def check_websites(self, job, sources):
        from website_probe import public_url
        targets=[]
        for row in sources[:200]:
            if not isinstance(row,dict):continue
            try:address=public_url(row.get('baseUrl'))
            except (ValueError,UnicodeError):continue
            targets.append({'id':str(row.get('id','')),'baseUrl':address})
        if not targets:return []
        self.require_isolation()
        with tempfile.TemporaryDirectory(prefix='website-',dir=self.path(job['id'])) as temporary:
            work=Path(temporary)
            raw=json.dumps(targets)
            if len(raw.encode())>65536:raise SourceBlocked('Website target observations exceed the bounded input limit')
            (work/'targets.json').write_text(raw,encoding='utf-8')
            command=self.sandbox.command(work,['/opt/autosourcarr-venv/bin/python','/sandbox-tools/website_probe.py'],sockets=self.sockets)
            output=self.command(job,command,timeout=90)
            if len(output)>128*1024:raise SourceBlocked('Website observation output exceeds its supported limit')
            result=json.loads(output)
            if not isinstance(result,list) or len(result)>8:raise SourceBlocked('Website observations were invalid')
            return result

    def research_addresses(self,job,sources,original):
        from address_research import base_addresses
        self.require_isolation();files=[]
        module=original if job['module']=='.' else original/job['module']
        for path in sorted(module.rglob('*')):
            if path.suffix not in {'.kt','.java','.kts','.gradle'} or not path.is_file() or path.stat().st_size>512*1024:continue
            addresses=base_addresses(path.read_text(encoding='utf-8',errors='replace'))
            if addresses:
                name=path.relative_to(original).as_posix()
                upstream=job['raw_meta'].get('upstream_module')
                if job['raw_meta']['recipe']=='jvm' and upstream and upstream!='.' and name.startswith('src/'):
                    name=upstream+'/'+name.removesuffix('.java')+'.kt'
                files.append({'path':name,'addresses':addresses})
            if len(files)>=6:break
        with tempfile.TemporaryDirectory(prefix='research-',dir=self.path(job['id'])) as temporary:
            work=Path(temporary)
            (work/'research.json').write_text(json.dumps({'package':job['pkg_name'],'raw_meta':job['raw_meta'],
                'sources':sources,'website_checks':job['website_checks'],'files':files}),encoding='utf-8')
            args=self.sandbox.command(work,['/opt/autosourcarr-venv/bin/python','/sandbox-tools/address_research.py'],sockets=self.sockets)
            output=self.command(job,args,timeout=95)
            if len(output)>128*1024:raise SourceBlocked('Address research exceeded its bounded output limit')
            result=json.loads(output)
            if not isinstance(result,dict) or not isinstance(result.get('targets'),list) or len(result['targets'])>16:raise SourceBlocked('Address research returned invalid evidence')
            job['address_research']=result
            job['address_targets']=result['targets']
            revision=job['raw_meta'].get('origin',{}).get('revision')
            if revision:job['source_revision']=revision

    def prepare_edit(self, edit):
        pass  # Everything is already owned by this instance's runtime UID.

    def edit_command(self, job, edit, prompt):
        self.require_isolation()
        from ai_provider import read_provider, relay, protocol, RelayCleanupUncertain
        provider = read_provider(self.root)
        command = ['/opt/node/bin/codex','--ask-for-approval','never']
        if provider['provider'] == 'chatgpt': command += ['--search']
        command += ['exec','--sandbox','workspace-write','--skip-git-repo-check','--json',
                    '-c','sandbox_workspace_write.network_access=false','-c','project_doc_max_bytes=0']
        try:
            if provider['provider'] != 'chatgpt':
                if protocol(provider) != 'responses':
                    with relay(provider) as socket:
                        editor=['/usr/bin/python3','-I','/sandbox-tools/native_editor.py',protocol(provider),provider['model'],prompt,('' if job['module']=='.' else job['module']+'/')+'src']
                        if job.get('raw_meta',{}).get('recipe')=='jvm':editor+=['java']
                        return self.command(job,self.sandbox.command(edit,editor,sockets=self.sockets,ai_socket=socket),timeout=900)
                config = {'model_provider':'autosourcarr', 'model':provider['model'], 'web_search':'disabled',
                    'model_providers.autosourcarr.name':'Configured AI',
                    'model_providers.autosourcarr.base_url':'http://127.0.0.1:8081/v1',
                    'model_providers.autosourcarr.wire_api':'responses',
                    'model_providers.autosourcarr.requires_openai_auth':False,
                    'model_providers.autosourcarr.supports_websockets':False,
                    'model_providers.autosourcarr.request_max_retries':0,
                    'model_providers.autosourcarr.stream_max_retries':0}
                for key, value in config.items(): command += ['-c',key+'='+json.dumps(value)]
                with relay(provider) as socket:
                    return self.command(job,self.sandbox.command(edit,command+[prompt],sockets=self.sockets,ai_socket=socket),timeout=900)
            return self.command(job,self.sandbox.command(edit,command+[prompt],sockets=self.sockets,auth=self.root/'auth'),timeout=900)
        except RelayCleanupUncertain:
            self.cleanup_failed = True
            raise
        except RuntimeError as exc:
            raise SourceBlocked('Repair AI could not finish. Check its connection, model or account and sandbox readiness in setup or Source repairs. No candidate was installed.') from exc

    @contextmanager
    def validation_engine(self, job):
        self.require_isolation()
        # Fresh profile for every artifact. Neither live source data nor account
        # credentials are mounted into this namespace.
        with tempfile.TemporaryDirectory(prefix='test-',dir=self.path(job['id'])) as temporary:
            root=Path(temporary); work=root/'work'; profile=root/'profile'
            work.mkdir();profile.mkdir()
            with zipfile.ZipFile('/home/suwayomi/startup/tachidesk_latest.jar') as jar:
                info=jar.getinfo('server-reference.conf')
                if info.file_size>1024*1024: raise ValueError('Invalid bundled engine configuration')
                (profile/'server.conf').write_bytes(jar.read(info))
            gateway=root/'gateway';gateway.mkdir()
            socket=gateway/'engine.sock'
            args=self.sandbox.command(work,['/home/suwayomi/startup_script.sh'],sockets=self.sockets,engine_profile=profile,
                env={'HOME':'/home/suwayomi','JAVA_HOME':'/opt/java/openjdk',
                    'BIND_IP':'127.0.0.1','BIND_PORT':'4567','AUTH_MODE':'none','WEB_UI_ENABLED':'false',
                    'UPDATE_INTERVAL':'0','EXTENSION_STORES':'[]','MAX_SOURCES_IN_PARALLEL':'1',
                    'JAVA_TOOL_OPTIONS':'-XX:ActiveProcessorCount=1 -Xmx768m '+PROXY_JAVA,
                    'KCEF_ENABLED':'false','FLARESOLVERR_ENABLED':'false'})
            with SyncPipe() as lifetime, (root/'engine.log').open('wb') as output:
                process=subprocess.Popen(lifetime.attach(args),pass_fds=lifetime.pass_fds,stdout=output,stderr=subprocess.STDOUT,start_new_session=True,env={'PATH':'/usr/bin:/bin'})
                lifetime.spawned()
                # Watch independently of the validator's next HTTP request: a
                # loaded extension must not monopolize this instance while idle.
                import threading
                watchdog = Watchdog(process.pid, self.path(job['id']))
                stop_watch = threading.Event()
                failures = []
                def watch():
                    while not stop_watch.wait(.2):
                        if process.poll() is not None: return
                        try: watchdog.check()
                        except Exception as exc:
                            if process.poll() is not None: return
                            failures.append(exc)
                            terminate_group(process)
                            return
                monitor = threading.Thread(target=watch, name='repair-resource-watch', daemon=True)
                monitor.start()
                def cancelled():
                    self.check_cancelled(job)
                    if failures: raise SourceBlocked(str(failures[0]))
                validator=Validator('http://isolated-engine',client=httpx.Client(base_url='http://isolated-engine',transport=httpx.HTTPTransport(uds=str(socket)),timeout=45,trust_env=False),cancel=cancelled,
                                    converted_jar_reader=lambda extension: read_converted_jar(profile, extension))
                try:
                    yield validator
                    cancelled()
                finally:
                    stop_watch.set();monitor.join()
                    validator.close();terminate_group(process)
                    try: lifetime.wait_eof()
                    except RuntimeError:
                        self.cleanup_failed=True;raise
                    socket.unlink(missing_ok=True)


class InternalControl(Control):
    def start(self, action, operation_id=None):
        if self.runner.cleanup_failed: raise RuntimeError('Repair cleanup could not be confirmed. Restart this AutoSourcarr instance before using repair tools or its account.')
        return super().start(action,operation_id)

    async def prepare_tools(self):
        if self.runner.cleanup_failed: raise RuntimeError("Restart this AutoSourcarr instance after failed sandbox cleanup")
        self.images_ready=False
        result=await asyncio.to_thread(self.runner.probe)
        if not result['available']: raise RuntimeError(result['message'])
        for path in ('/opt/node/bin/codex','/opt/build-jdk/bin/javac','/opt/android-sdk-linux','/home/suwayomi/startup/tachidesk_latest.jar'):
            if not Path(path).exists(): raise RuntimeError('Integrated toolchain is incomplete. Rebuild this release image.')

    async def account_command(self, action):
        if self.runner.cleanup_failed: raise RuntimeError("Restart this AutoSourcarr instance after failed sandbox cleanup")
        if action not in {'login', 'login-status', 'logout'}: raise ValueError('Unknown account action')
        if not ACCOUNT_COMMAND or not all(Path(path).is_file() for path in ACCOUNT_COMMAND):
            raise RuntimeError('Bundled ChatGPT sign-in tools are missing. Rebuild this release image; no Compose permission change is needed for sign-in.')
        owned_directory(self.runner.root)
        owned_directory(self.runner.root/'auth')
        self._account_run_dir=Path(tempfile.mkdtemp(prefix='account-run-',dir=self.runner.root))
        home=self._account_run_dir/'home';home.mkdir(mode=0o700)
        temporary=self._account_run_dir/'tmp';temporary.mkdir(mode=0o700)
        if action=='login':
            auth=self.account_auth_path(action)
        else:
            auth=self._account_run_dir/'codex';auth.mkdir(mode=0o700)
            try: data=await asyncio.to_thread(private_account_auth,self.runner.root/'auth')
            except (OSError,ValueError,RecursionError):
                raise RuntimeError('Saved ChatGPT credentials could not be read safely. Your saved account is retained; sign in again or check this instance’s data permissions.') from None
            if data is not None:
                descriptor=os.open(auth/'auth.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
                with os.fdopen(descriptor,'wb') as stream: stream.write(data)
        self._account_env={'PATH':'/opt/node/bin:/usr/bin:/bin','HOME':str(home),
            'CODEX_HOME':str(auth),'TMPDIR':str(temporary),'LANG':'C.UTF-8','LC_ALL':'C.UTF-8',
            'GIT_TERMINAL_PROMPT':'0','GIT_CONFIG_NOSYSTEM':'1','PYTHONDONTWRITEBYTECODE':'1',
            'TOKIO_WORKER_THREADS':'2'}
        command=[*ACCOUNT_COMMAND,'-c','cli_auth_credentials_store="file"',
                 '-c','forced_login_method="chatgpt"']
        command += ['login','--device-auth'] if action=='login' else ['login','status'] if action=='login-status' else ['logout']
        self._account_expected=tuple(command)
        self._account_codex_home=auth
        self._account_action=action
        self._pending_logout=False
        return command

    async def failure_output(self, output):
        read = asyncio.create_task(asyncio.to_thread(private_login_errors, self._account_codex_home))
        try:
            detail = await asyncio.shield(read)
        except asyncio.CancelledError:
            while not read.done():
                try: await asyncio.shield(read)
                except asyncio.CancelledError: continue
            await asyncio.gather(read, return_exceptions=True)
            raise
        return output[-16384:]+'\n'+detail

    async def command(self, args, *, timeout=60, login=False):
        if tuple(args)!=getattr(self,'_account_expected',None):
            raise ValueError('Only the fixed bundled account command may run outside repair isolation')
        lifetime=AccountLifetime()
        self._lifetime=lifetime
        self._launcher_failure=None
        process=None
        spawn=asyncio.create_task(asyncio.create_subprocess_exec(*lifetime.attach(args),pass_fds=lifetime.pass_fds,
            stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.STDOUT,start_new_session=True,
            env=self._account_env,cwd=self._account_env['HOME']))
        output=bytearray()
        async def lifetime_call(function):
            check=asyncio.create_task(asyncio.to_thread(function))
            try: await asyncio.shield(check)
            except asyncio.CancelledError:
                while not check.done():
                    try: await asyncio.shield(check)
                    except asyncio.CancelledError: continue
                await asyncio.gather(check,return_exceptions=True)
                raise
        try:
            try: process=await asyncio.shield(spawn)
            except asyncio.CancelledError:
                # Spawn can already own a running child before its Process is
                # delivered. Repeated cancellation must not discard that owner.
                while not spawn.done():
                    try: await asyncio.shield(spawn)
                    except asyncio.CancelledError: continue
                process=spawn.result()
                lifetime.spawned()
                await lifetime_call(lifetime.ready)
                raise
            lifetime.spawned()
            await lifetime_call(lifetime.ready)
            async def read():
                while chunk:=await process.stdout.read(4096):
                    output.extend(chunk)
                    if len(output)>65536: del output[:-32768]
                    if login:
                        self._capture_code(output.decode(errors='replace'))
                return await process.wait()
            watchdog = Watchdog(process.pid, self._account_run_dir)
            async def watch():
                while process.returncode is None:
                    check = asyncio.create_task(asyncio.to_thread(watchdog.check))
                    try: await asyncio.shield(check)
                    except RuntimeError as exc:
                        # /proc can disappear before asyncio publishes exit.
                        # Confirm exit briefly; a still-running unaccountable
                        # process remains a failure rather than a silent pass.
                        try: await asyncio.wait_for(process.wait(), .1)
                        except asyncio.TimeoutError: raise exc
                        return
                    except asyncio.CancelledError:
                        while not check.done():
                            try: await asyncio.shield(check)
                            except asyncio.CancelledError: continue
                        await asyncio.gather(check, return_exceptions=True)
                        raise
                    await asyncio.sleep(.2)
            reader = asyncio.create_task(read())
            monitor = asyncio.create_task(watch())
            deadline = asyncio.get_running_loop().time()+timeout
            try:
                done, _ = await asyncio.wait({reader, monitor}, timeout=timeout,
                                             return_when=asyncio.FIRST_COMPLETED)
                if not done: raise asyncio.TimeoutError()
                if monitor in done:
                    await monitor
                code = await asyncio.wait_for(reader, max(0, deadline-asyncio.get_running_loop().time()))
                await lifetime_call(lifetime.wait_eof)
                self._launcher_failure=getattr(lifetime,'failure',None)
                if self._account_action=='logout' and code==0:
                    self._pending_logout=True
                return code, output.decode(errors='replace')
            finally:
                monitor.cancel(); reader.cancel()
                drained = asyncio.gather(monitor, reader, return_exceptions=True)
                while not drained.done():
                    try: await asyncio.shield(drained)
                    except asyncio.CancelledError: continue
                await drained
        finally:
            async def cleanup():
                if process is not None and process.returncode is None:
                    from contextlib import suppress
                    with suppress(ProcessLookupError): process.terminate()
                    try: await asyncio.wait_for(process.wait(),5)
                    except asyncio.TimeoutError:
                        with suppress(ProcessLookupError): process.kill()
                        await process.wait()
                elif process is None:
                    lifetime.close();self._lifetime=None
            task=asyncio.create_task(cleanup())
            while not task.done():
                try: await asyncio.shield(task)
                except asyncio.CancelledError: continue
            await task

    async def _remove_container(self):
        lifetime=getattr(self,'_lifetime',None)
        if lifetime:
            # _finish keeps the account lock on failure; retry checks this same
            # lifetime pipe. No later job can race unfinished credential writes.
            await asyncio.to_thread(lifetime.wait_eof)
            lifetime.close();self._lifetime=None
        if getattr(self,'_pending_logout',False):
            # Codex revokes/removes the private copy. Remove the saved file only
            # after successful sign-out AND confirmed owned process teardown.
            owned_directory(self.runner.root/'auth')
            (self.runner.root/'auth/auth.json').unlink(missing_ok=True)
            self._pending_logout=False
        directory=getattr(self,'_account_run_dir',None)
        if directory:
            await asyncio.to_thread(shutil.rmtree,directory)
            self._account_run_dir=None
        self._account_expected=None
        self._account_codex_home=None
        self._account_env=None
        self.container_name=None

    def cleanup_failure_message(self):
        return 'Account cleanup could not be confirmed. Retry account cleanup; if it stays blocked, restart this AutoSourcarr instance. Saved credentials are retained until cleanup is confirmed.'

    def runtime_failure_message(self):
        return 'The bundled account action could not run. Check this instance’s data permissions and rebuild the current image if needed. Sign-in does not require repair namespace permissions.'
