"""Small trusted boundary between an editable source tree and the repair runner."""
import base64
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import stat
import zipfile
from baseline_manifest import manifest_contract

MAX_PACKAGE = 32 * 1024 * 1024
MAX_REQUEST = MAX_PACKAGE * 4 + 1024 * 1024  # Baseline, optional APK and raw ZIP (base64).
REPOSITORY = 'https://github.com/keiyoushi/extensions-source.git'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    with temporary.open('w') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def package_bytes(data):
    if not 100 < len(data) <= MAX_PACKAGE:
        raise ValueError('Extension package is empty or exceeds 32 MiB')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        if 'AndroidManifest.xml' not in names or not any(n.endswith('.class') for n in names):
            raise ValueError('Expected a compiled JVM extension JAR with its Android manifest')
        if sum(i.file_size for i in archive.infolist()) > 160 * 1024 * 1024:
            raise ValueError('Expanded extension package exceeds the validation limit')
    return data


def _source_rows(sources):
    if not isinstance(sources, list) or not 1 <= len(sources) <= 200:
        raise ValueError('Source identities are required for this repair input')
    normalized = []
    for row in sources:
        if not isinstance(row, dict) or not re.fullmatch(r'-?[0-9]{1,20}', str(row.get('id', ''))):
            raise ValueError('Invalid source identity')
        if not isinstance(row.get('name'), str) or not row['name'] or not re.fullmatch(r'[a-zA-Z0-9_-]{2,12}', str(row.get('lang', ''))):
            raise ValueError('Source name/language is missing')
        normalized.append({key: str(row.get(key, ''))[:2048] for key in ('id', 'name', 'lang', 'baseUrl')})
    return normalized


def request_contract(value):
    if not isinstance(value, dict):
        raise ValueError('Expected a private repair request')
    pkg = str(value.get('pkg_name', ''))
    module = str(value.get('module', ''))
    revision = str(value.get('revision', '')).strip()
    raw_first=value.get('raw_first') is True
    if raw_first:
        from source_tools.archive import package_name, module_name
        package_name(pkg);module_name(module)
    elif not re.fullmatch(r'eu\.kanade\.tachiyomi\.extension\.[a-z0-9_.]+', pkg):
        raise ValueError('Select an installed Tachiyomi extension package')
    if not raw_first and not re.fullmatch(r'src/[a-z0-9]+/[a-z0-9_]+', module):
        raise ValueError('Source module must have the form src/en/mangafire')
    if revision and not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Optional source revision must be a full 40-character commit hash')
    issue = str(value.get('issue', '')).strip()
    if not 10 <= len(issue) <= 6000:
        raise ValueError('Describe the source failure in 10–6000 characters; omit private data')
    origin = value.get('origin', 'manual')
    if origin not in ('manual', 'automatic'):
        raise ValueError('Invalid repair task origin')
    result = {'pkg_name': pkg, 'module': module, 'revision': revision, 'issue': issue, 'origin': origin}
    if raw_first:
        version=value.get('installed_version')
        if not isinstance(version,str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}',version):raise ValueError('Installed version is required')
        library=value.get('installed_library') or version.rsplit('.',1)[0]
        if library not in {'1.3','1.4','1.5','1.6'}:raise ValueError('Installed compatible source library is required')
        raw_only=value.get('raw_only') is True
        if raw_only and value.get('reconstruct') is not True:raise ValueError('A raw-only worker request must explicitly choose reconstruction')
        rows=[] if raw_only and value.get('installed_sources')==[] else _source_rows(value.get('installed_sources'))
        result.update(raw_first=True,installed_version=version,installed_library=library,
                      installed_sources=rows,auto_install=value.get('auto_install') is True,
                      reconstruct=value.get('reconstruct') is True,raw_only=value.get('raw_only') is True)
        if value.get('raw_meta') is not None:
            from source_tools.archive import metadata
            info=metadata(value['raw_meta'])
            if info['package']!=pkg:raise ValueError('Raw code belongs to a different package')
            result['raw_meta']=info;result['module']=info['module']
        if value.get('reconstruction_origin') is not None:
            from source_tools.archive import metadata
            info=metadata(value['reconstruction_origin'])
            if info['package']!=pkg or info['kind']!='reconstructed' or info['recipe']!='jvm':raise ValueError('Invalid reconstruction provenance')
            result['reconstruction_origin']=info
        previous=value.get('previous_attempt')
        if previous:
            if not isinstance(previous,str) or not re.fullmatch(r'[0-9a-f]{32}',previous):raise ValueError('Invalid previous attempt')
            result['previous_attempt']=previous
    if value.get('recovery') is True:
        if not revision:
            raise ValueError('Orphan recovery requires a pinned original source commit')
        result.update(recovery=True, expected_sources=_source_rows(value.get('expected_sources')))
    if 'source_apk' in value:
        if result.get('recovery'):
            raise ValueError('Installed APK proof cannot be supplied for uninstalled orphan recovery')
        version = value.get('installed_version')
        if not isinstance(version, str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}', version):
            raise ValueError('Installed source version is required for converted APK proof')
        basis = value.get('original_apk_basis')
        if basis not in ('recorded-installed-upload', 'user-confirmed-original'):
            raise ValueError('Original APK loading metadata needs recorded installation or explicit original-file confirmation')
        result.update(installed_version=version, installed_sources=_source_rows(value.get('installed_sources')), original_apk_basis=basis)
    return result


def request_payload(body):
    values = request_contract(body)
    def decode(key):
        value = body.get(key)
        if not isinstance(value, str) or len(value) > MAX_PACKAGE * 4 // 3 + 4:
            raise ValueError('Invalid or oversized ' + key + ' package input')
        data = base64.b64decode(value, validate=True)
        if not 100 < len(data) <= MAX_PACKAGE:
            raise ValueError(key + ' package is empty or exceeds 32 MiB')
        return data
    baseline = b'' if values.get('recovery') else decode('baseline')
    apk = decode('source_apk') if 'source_apk' in body else b''
    if apk:
        values['source_apk_sha256'] = digest(apk)
    return values, baseline, apk


def tree_manifest(root):
    """Never trust git status after an agent has written into the checkout."""
    root = Path(root)
    manifest = {}
    for path in root.rglob('*'):
        rel = path.relative_to(root).as_posix()
        if rel == '.git' or rel.startswith('.git/'):
            continue
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode) or not (stat.S_ISREG(mode) or stat.S_ISDIR(mode)):
            raise ValueError('Source tree contains a symbolic link or special file: '+rel)
        if path.is_file():
            if path.stat().st_size > 32 * 1024 * 1024:
                raise ValueError('Source file exceeds the size limit: '+rel)
            manifest[rel] = [digest(path.read_bytes()), stat.S_IMODE(mode)]
    return manifest


def approved_changes(before, after, module, *, reconstructed=False):
    changed = sorted(p for p in before.keys() | after.keys() if before.get(p) != after.get(p))
    if not changed:
        raise ValueError('The repair produced no source-code change')
    if len(changed) > 30:
        raise ValueError('Repair changed too many files; split this repair for review')
    for name in changed:
        # Keep build scripts, dependencies, package layout, and unrelated modules intact.
        prefix=('' if module=='.' else module+'/')+'src/'
        if not name.startswith(prefix) or not name.endswith(('.kt','.java') if reconstructed else ('.kt',)):
            raise ValueError('Repair changed a file outside the approved Kotlin source module: '+name)
        if name not in after:
            raise ValueError('Repair removed a source file: '+name)
        if name not in before:
            raise ValueError('Repair added a source file; keep the existing file structure: '+name)
        if name in before and before[name][1] != after[name][1]:
            raise ValueError('Repair changed source file permissions: '+name)
    return changed


def source_identity(rows):
    return sorted((str(x['id']), str(x.get('lang', '')), str(x.get('name', ''))) for x in rows)


def public_job(job):
    result = {key: job[key] for key in (
        'id', 'pkg_name', 'module', 'state', 'message', 'created_at', 'updated_at', 'version', 'origin',
        'revision', 'baseline_sha256', 'candidate_sha256', 'candidate_kind',
        'session_id', 'changed_files', 'checks', 'baseline_checks', 'history',
        'expected_ids', 'source_revision', 'validation_time', 'recovery', 'discarded_at',
        'baseline_format', 'baseline_issue', 'baseline_contract_basis', 'baseline_contract', 'source_apk_sha256', 'conversion_proof', 'conversion_comparison',
        'website_checks', 'address_migrations', 'baseline_import_proof', 'baseline_metadata_basis',
        'raw_first', 'raw_only', 'raw_meta', 'raw_sha256', 'candidate_raw_sha256', 'auto_install', 'previous_attempt',
        'raw_id','candidate_raw_id','candidate_raw_meta','automatic_address_trust','address_research',
        'controlled_address_changes',
    ) if key in job}
    if isinstance(job.get('address_migrations'),list):result['address_migration_count']=min(200,len(job['address_migrations']))
    return result


def status_job(job):
    """Bound the hundred-job status wire; details retain full review evidence."""
    if (not isinstance(job,dict) or not re.fullmatch(r'[0-9a-f]{32}',str(job.get('id','')))
            or type(job.get('created_at')) not in (int,float) or not math.isfinite(job['created_at'])):
        raise ValueError('Invalid retained job summary')
    result={key:value for key,value in public_job(job).items() if key not in {'checks','baseline_checks','history','address_migrations','website_checks'}}
    def site(row):
        from website_probe import public_url
        if not isinstance(row,dict):return None
        value={key:row[key] for key in ('state','http_status','content_kind','reason') if key in row and isinstance(row[key],(str,int)) and len(str(row[key]))<100}
        for key in ('requested_url','final_url'):
            try:value[key]=public_url(row.get(key))
            except (ValueError,UnicodeError):pass
        ids=row.get('source_ids')
        if isinstance(ids,list):value['source_ids']=[str(sid) for sid in ids[:8] if re.fullmatch(r'-?[0-9]{1,20}',str(sid))]
        return value
    sites=[]
    rows=job.get('website_checks')
    for row in (rows if isinstance(rows,list) else [])[:8]:
        item=site(row)
        if item is None:continue
        if isinstance(row.get('apis'),list):item['apis']=[item for item in (site(entry) for entry in row['apis'][:2]) if item is not None]
        sites.append(item)
    if sites:result['website_checks']=sites
    return result


def safe_text(value, limit=5000):
    text = str(value)
    text = re.sub(r'(https?://)[^\s/@]+:[^\s/@]+@', r'\1[redacted]@', text, flags=re.I)
    text = re.sub(r'([?&](?:token|auth|key|api_key|access_token)=)[^\s&#"\']+', r'\1[redacted]', text, flags=re.I)
    text = re.sub(r'(?i)((?:[\w-]*(?:token|password|secret|api[_-]?key)|authorization|cookie)["\']?\s*[:=]\s*)(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s,;&}]+)', r'\1[redacted]', text)
    text = re.sub(r'\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b', '[redacted JWT]', text)
    text = re.sub(r'(?i)(bearer\s+)[\w.\-]+', r'\1[redacted]', text)
    text = re.sub(r'(?i)(api[_-]?key|access[_-]?token|refresh[_-]?token|password|cookie)(["\s:=]+)[^\s,}]+', r'\1\2[redacted]', text)
    text = re.sub(r'\bsk-[A-Za-z0-9_-]+', '[redacted]', text)
    return text[-limit:]
