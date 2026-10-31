"""Allowlisted repair summaries; never retain prompts, patches or conversations."""
import math
import re

STATES = frozenset(('idle', 'running', 'complete', 'queued', 'checking-history', 'checking-website', 'checking-backup',
    'checking-apk-backup', 'checking-baseline', 'backup-verified', 'backup-saved', 'checking-upstream',
    'checkout', 'building-repair', 'repairing', 'validating', 'ready', 'installed', 'blocked', 'failed', 'cancelled', 'interrupted', 'discarded',
    'awaiting-raws','raws-acquired','raws-retained','reconstructing','checking-bytecode','confirmed','reported-not-working','reverted'))
PROOF_FIELDS = ('source_apk_sha256', 'installed_jar_sha256', 'converted_jar_sha256',
                'payload_sha256', 'validation_envelope_sha256')


def _public_address(value):
    from urllib.parse import urlsplit
    if not isinstance(value,str) or len(value)>2048 or any(c.isspace() for c in value):return None
    try:
        parsed=urlsplit(value)
        return value if parsed.scheme in {'http','https'} and parsed.hostname and not parsed.username and not parsed.password and not parsed.query and not parsed.fragment else None
    except ValueError:return None


def safe_websites(rows, privacy):
    if not isinstance(rows,list):return []
    result=[]
    for row in rows[:16]:
        if not isinstance(row,dict):continue
        state=row.get('state')
        item={'state':state if isinstance(state,str) and state in {'working','inconclusive','verification','rate_limited','not_found'} else 'unknown'}
        for key in ('requested_url','final_url'):
            value=_public_address(row.get(key))
            if value:item[key]=privacy.text(value)
        status=row.get('http_status')
        if type(status) is int and 100<=status<=599:item['http_status']=status
        kind=row.get('content_kind')
        if isinstance(kind,str) and kind in {'html','json','other'}:item['content_kind']=kind
        reason=row.get('reason')
        if isinstance(reason,str) and reason in {'invalid-public-address','connection-redirect-or-budget','api-origin-changed','invalid-or-truncated-json','encoded-response-not-decoded'}:item['reason']=reason
        ids=row.get('source_ids')
        if isinstance(ids,list):item['source_ids']=[sid for sid in ids[:200] if _string(sid,r'-?[0-9]{1,20}')]
        if isinstance(row.get('apis'),list):item['apis']=safe_websites([entry for entry in row['apis'][:2] if isinstance(entry,dict) and 'apis' not in entry],privacy)
        result.append(item)
    return result


def _string(value, pattern):
    return value if isinstance(value, str) and re.fullmatch(pattern, value) else None


def _time(value):
    return value if type(value) in {int, float} and math.isfinite(value) and 0 <= value <= 10**13 else None


def safe_operation(value, privacy):
    if not isinstance(value, dict):
        return None
    state = value.get('state')
    result = {'state': state if isinstance(state, str) and state in STATES else 'unknown'}
    message = value.get('message')
    if isinstance(message, str):
        result['message'] = privacy.text(message[:4096])[:1200]
    for key in ('id', 'repair_job_id'):
        item = _string(value.get(key), r'[0-9a-f]{32}')
        if item:
            result[key] = item
    for key in ('pkg_name', 'pkgName'):
        item = _string(value.get(key), r'[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*){2,}')
        if item:
            result[key] = item
    return result


def safe_job(value, privacy):
    result = safe_operation(value, privacy)
    if result is None or 'id' not in result:
        return None
    for key in ('created_at', 'updated_at', 'validation_time'):
        item = _time(value.get(key))
        if item is not None:
            result[key] = item
    for key, pattern in (('module', r'(?:\.|[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+){0,7})'),
                         ('version', r'[0-9]+\.[0-9]+\.[0-9]+'),
                         ('baseline_sha256', r'[0-9a-f]{64}'), ('candidate_sha256', r'[0-9a-f]{64}'),
                         ('source_apk_sha256', r'[0-9a-f]{64}')):
        item = _string(value.get(key), pattern)
        if item:
            result[key] = item
    if value.get('origin') in ('manual', 'automatic'):
        result['origin'] = value['origin']
    for key in ('raw_first','raw_only','auto_install'):
        if type(value.get(key)) is bool:result[key]=value[key]
    result['raw_code_available']=bool(_string(value.get('raw_sha256'),r'[0-9a-f]{64}'))
    ids=value.get('expected_ids')
    if isinstance(ids,list) and len(ids)<=200 and all(_string(sid,r'-?[0-9]{1,20}') for sid in ids):
        result['expected_ids']=ids[:]
    for key, allowed in (('baseline_format', {'native-jar', 'converted-apk-jar'}),
                         ('baseline_issue', {'converted-jar-without-manifest', 'invalid-native-manifest'}),
                         ('baseline_contract_basis', {'native-manifest', 'recorded-installed-upload', 'user-confirmed-original', 'cold-import-installed-jar'}),
                         ('baseline_metadata_basis', {'recorded-installed-upload', 'user-confirmed-original','installed-bytecode'})):
        item = value.get(key)
        if isinstance(item, str) and item in allowed:
            result[key] = item
    proof = value.get('conversion_proof')
    if isinstance(proof, dict):
        result['conversion_proof'] = {key: proof[key] for key in PROOF_FIELDS if _string(proof.get(key), r'[0-9a-f]{64}')}
    imported = value.get('baseline_import_proof')
    if isinstance(imported, dict):
        result['baseline_import_proof'] = {key: imported[key] for key in (*PROOF_FIELDS, 'source_identity_sha256')
                                          if _string(imported.get(key), r'[0-9a-f]{64}')}
        if type(imported.get('verified')) is bool:
            result['baseline_import_proof']['verified'] = imported['verified']
    comparison = value.get('conversion_comparison')
    if isinstance(comparison, dict):
        safe = {key: comparison[key] for key in ('source_apk_sha256','installed_jar_sha256','converted_jar_sha256')
                if _string(comparison.get(key), r'[0-9a-f]{64}')}
        for key in ('installed_entries','converted_entries','identical_entries','changed_entries','missing_entries','added_entries',
                    'changed_class_entries','changed_resource_entries','missing_class_entries','added_class_entries'):
            item = comparison.get(key)
            if type(item) is int and 0 <= item <= 4096:
                safe[key] = item
        result['conversion_comparison'] = safe
    if 'website_checks' in value:result['website_checks']=safe_websites(value['website_checks'],privacy)
    migrations=value.get('address_migrations')
    if isinstance(migrations,list):
        result['address_migration_count']=min(200,len(migrations))
    else:
        count=value.get('address_migration_count')
        if type(count) is int and 0<=count<=200:result['address_migration_count']=count
    contract = value.get('baseline_contract')
    if isinstance(contract, dict):
        fields = (('package', r'[A-Za-z_$][A-Za-z0-9_.$]{1,240}'),
                  ('entrypoint', r'[A-Za-z_$][A-Za-z0-9_.$]{1,240}'), ('library', r'[0-9]{1,3}\.[0-9]{1,3}'))
        result['baseline_contract'] = {key: contract[key] for key, pattern in fields if _string(contract.get(key), pattern)}
    history = value.get('history')
    if isinstance(history, list):
        result['history'] = []
        for row in history[-8:]:
            safe = safe_operation(row, privacy)
            if safe is not None:
                safe = {key: safe[key] for key in ('state', 'message') if key in safe}
                stamp = _time(row.get('at'))
                if stamp is not None:
                    safe['at'] = stamp
                if 'message' in safe:
                    safe['message'] = safe['message'][:600]
                result['history'].append(safe)
    return result


def safe_isolation(value, privacy):
    if not isinstance(value, dict):
        return None
    result = {'available': value.get('available') if type(value.get('available')) is bool else 'unknown'}
    reason = _string(value.get('reason'), r'[a-z][a-z0-9-]{0,63}')
    if reason:
        result['reason'] = reason
    message = value.get('message')
    if isinstance(message, str):
        result['message'] = privacy.text(message[:4096])[:1200]
    code = value.get('exit_code')
    if type(code) is int and -255 <= code <= 255:
        result['exit_code'] = code
    details = value.get('details')
    if isinstance(details, str):
        result['details'] = privacy.text(details[:4096])[:1800]
    checks = value.get('checks')
    if isinstance(checks, dict):
        result['checks'] = {}
        for key in ('NoNewPrivs', 'Seccomp', 'max_user_namespaces', 'unprivileged_userns_clone',
                    'apparmor_restrict_unprivileged_userns', 'apparmor_restrict_unprivileged_userns_force',
                    'proc_masked_paths', 'proc_readonly_paths'):
            item = _string(checks.get(key), r'(?:[0-9]{1,20}|not reported)')
            if item:
                result['checks'][key] = item
        label = checks.get('apparmor')
        if isinstance(label, str):
            result['checks']['apparmor'] = privacy.text(label[:250])[:250]
    return result
