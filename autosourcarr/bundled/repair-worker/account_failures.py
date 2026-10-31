"""Safe account error classification, independent of Linux process supervision."""
import errno
import re

ANSI = re.compile(r'\x1b\[[0-?]*[ -/]*[@-~]')


class AccountFailure(RuntimeError):
    """Only fixed messages and selected numeric/enum facts cross the account API."""
    def __init__(self, message, reason, *, exit_code=None, http_status=None,
                 phase='requesting-code', code_received=False):
        super().__init__(message)
        self.diagnostics = {'reason': reason, 'phase': phase, 'exit_code': exit_code,
                            'http_status': http_status, 'code_received': code_received}


def launcher_account_failure(value, exit_code):
    """Only metadata received through the supervisor's private pipe reaches here."""
    stage=value.get('stage') if isinstance(value,dict) else None
    number=value.get('errno') if isinstance(value,dict) else None
    if not isinstance(stage,str) or stage not in {'exec','parent'} or type(number) is not int or not 0 <= number <= 4095:
        raise ValueError('Invalid account launcher failure')
    if stage=='parent':
        reason='runtime-error'
        message='The account supervisor lost its expected owner before starting sign-in. OpenAI was not contacted. Retry after this AutoSourcarr instance is stable.'
    elif number in {errno.EAGAIN,errno.ENOMEM,errno.ENFILE,errno.EMFILE}:
        reason='resource-limit'
        message='The local account executable could not start within this instance’s available process, memory or file limits (OS error '+str(number)+'). OpenAI sign-in has not started. Let startup/build activity settle, then retry.'
    else:
        reason='tool-startup'
        message='The bundled native account executable could not start (OS error '+str(number)+'). OpenAI sign-in has not started. Rebuild the current image and check this instance’s tool/data permissions; source-repair namespace permissions are separate.'
    failure=AccountFailure(message,reason,exit_code=exit_code,phase='starting')
    failure.diagnostics.update(launcher_stage=stage,os_error=number)
    if 'resources' in value:
        from account_resources import validate_resources
        failure.diagnostics['resources'] = validate_resources(value['resources'])
    return failure


def account_failure(output, exit_code=None, *, action='login', code_received=False, launcher_failure=None):
    """Never infer account permissions from an initial code request or unknown exit."""
    if launcher_failure is not None:
        return launcher_account_failure(launcher_failure, exit_code)
    clean = ANSI.sub('', output[-32768:]).lower()
    phase = ('authorizing' if code_received else 'requesting-code') if action=='login' else 'checking-account' if action=='login-status' else 'signing-out'
    status = re.search(r'\b(?:request|auth) failed with status(?: code)?\s+(\d{3})\b', clean)
    http_status = int(status[1]) if status and 400 <= int(status[1]) <= 599 else None
    reason = 'unclassified'
    label = 'sign-in' if action=='login' else 'account check' if action=='login-status' else 'sign-out'
    message = 'The bundled '+label+' process stopped '+('before receiving a code' if action=='login' and not code_received else 'before finishing')+'. Expand Account error details for its exit status. The cause has not been identified.'
    if 'login is disabled' in clean or 'enable device code authorization for codex in chatgpt security settings' in clean:
        reason = 'device-login-disabled'
        message = 'OpenAI reported disabled device-code sign-in. Enable device-code login in ChatGPT security settings or ask your workspace administrator, then retry.'
    elif 'device code login is not enabled for this codex server' in clean or http_status==404:
        reason, http_status = 'device-endpoint-unavailable', 404
        message = 'The OpenAI device-code endpoint was unavailable (HTTP 404). Check this instance’s Internet route to auth.openai.com and retry later. This response does not establish an account security-settings problem.'
    elif http_status==429 or 'too many requests' in clean:
        reason = 'rate-limited'
        message = 'OpenAI temporarily limited sign-in requests. Wait a few minutes, then try again.'
    elif http_status in {401,403}:
        reason = 'request-denied'
        message = 'The '+('code request' if not code_received else 'authorization request')+' was rejected (HTTP '+str(http_status)+'). OpenAI or a network gateway denied it; this does not establish that device-code login is disabled. Check outbound access to auth.openai.com.'
    elif http_status is not None and http_status>=500:
        reason = 'service-unavailable'
        message = 'The OpenAI sign-in service is temporarily unavailable (HTTP '+str(http_status)+'). Try again later.'
    elif http_status is not None:
        reason = 'http-error'
        message = 'The OpenAI sign-in request failed (HTTP '+str(http_status)+'). Check outbound access to auth.openai.com and rebuild the current image if the error persists.'
    elif any(text in clean for text in ('certificate verify failed','certificate verification','invalid peer certificate','unknownissuer','tls handshake','ssl certificate')):
        reason = 'tls-error'
        message = 'The bundled account tool could not verify the OpenAI HTTPS connection. Check the server clock and any HTTPS filtering or proxy. Certificate verification remains enabled.'
    elif any(text in clean for text in ('dns error','failed to lookup address','name or service not known','temporary failure in name resolution')):
        reason = 'dns-error'
        message = 'The bundled account tool could not resolve auth.openai.com. Check this instance’s DNS and Internet access.'
    elif any(text in clean for text in ('connection refused','error sending request','network is unreachable','connection reset','connection closed')):
        reason = 'network-error'
        message = 'The bundled account tool could not reach OpenAI. Check this instance’s Internet access, then retry sign-in.'
    elif any(text in clean for text in ('expected value at line','error decoding response body','missing field `device_auth_id`','missing field `user_code`','invalid type:')):
        reason = 'invalid-response'
        message = 'OpenAI returned a response the bundled account tool could not read. Check for an outbound proxy or filtering page, then retry. Device-code account settings have not been checked.'
    elif any(text in clean for text in ('cannot find module','module_not_found','error while loading shared libraries','symbol lookup error','exec format error','permission denied','spawn enoent')):
        reason = 'tool-startup'
        message = 'The bundled account tool could not start. Rebuild the current image and check this instance’s tool/data permissions. No Compose permission change is required for sign-in.'
    elif any(text in clean for text in ('out of memory','failed to reserve virtual memory','cannot allocate memory','resource temporarily unavailable')):
        reason = 'resource-limit'
        message = 'The bundled account tool could not start within the available memory or process limits. Check this instance’s resource use; source repair permissions do not control account sign-in.'
    elif 'error loading configuration' in clean:
        reason = 'configuration-error'
        message = 'Codex could not load its private account configuration. Rebuild the current image and check this instance’s data permissions.'
    elif any(text in clean for text in ('timed out','code expired','code has expired')):
        reason = 'timed-out'
        message = 'The OpenAI sign-in code expired. Select Sign in with ChatGPT for a new code.' if code_received or 'device auth timed out' in clean else 'The OpenAI code request timed out before a code was received. Check this instance’s Internet access and retry.'
    elif exit_code is not None and exit_code<0:
        reason = 'process-signal'
        message = 'The bundled account process was stopped by a signal. Expand Account error details; check this instance’s resource use and rebuild the image if needed.'
    return AccountFailure(message, reason, exit_code=exit_code, http_status=http_status,
                          phase=phase, code_received=code_received)

