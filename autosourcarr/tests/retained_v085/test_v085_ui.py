import json
import subprocess
import shutil
from html.parser import HTMLParser
from pathlib import Path

import pytest

pytestmark = pytest.mark.skipif(shutil.which("node") is None, reason="Node.js is required for executable UI checks")

ROOT = Path(__file__).resolve().parents[3]
JS = ROOT / "autosourcarr/app/static/app.js"


def run_node(script):
    result = subprocess.run(["node", "-e", script], check=True, capture_output=True, text=True, timeout=5)
    return json.loads(result.stdout)


def fragment(start, end):
    source = JS.read_text()
    return source[source.index(start):source.index(end, source.index(start))]


def helpers():
    return fragment("function esc(", "function fmtScore(")


class Tags(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.tags = []
    def handle_starttag(self, tag, attrs):
        self.tags.append((tag, dict(attrs)))


@pytest.mark.parametrize("payload", [
    "A normal title", "King's Game", '\" onmouseover=\"evil()\" data-x=\"',
    "');evil();//", "&#039;);evil();//", "</button><img src=x onerror=evil()>",
    "back\\slash and \"quotes\"", "line\nseparator\u2028here",
])
def test_inline_handler_encoding_preserves_literal_data_without_new_attributes(payload):
    html = run_node(helpers() + "\nconst value=" + json.dumps(payload) + ";console.log(JSON.stringify(`<button onclick=\"capture('${jsq(value)}')\">x</button>`));")
    parsed = Tags()
    parsed.feed(html)
    assert len(parsed.tags) == 1 and parsed.tags[0][0] == "button"
    attrs = parsed.tags[0][1]
    assert set(attrs) == {"onclick"}
    result = run_node("let value;function capture(v){value=v;}function evil(){throw Error('injected');}\n" + attrs["onclick"] + ";console.log(JSON.stringify(value));")
    # The retained handler now safely escapes (rather than discards) separators.
    assert result == payload


@pytest.mark.parametrize("href", ["javascript:evil()", "data:text/html,evil", "https://metadata.invalid/work"])
def test_metadata_links_only_allow_http_schemes(href):
    code = helpers() + fragment("function metadataIdentityHtml(", "function inspectorChapterInfo(")
    payload = {"identities": [{"provider": "provider", "external_id": "1", "external_url": href}]}
    html = run_node(code + "console.log(JSON.stringify(metadataIdentityHtml(" + json.dumps(payload) + ")));" )
    parsed = Tags()
    parsed.feed(html)
    links = [attrs for tag, attrs in parsed.tags if tag == "a"]
    assert bool(links) is href.startswith("https:")


def test_metadata_diagnostics_are_escaped_and_unresolved_message_is_honest():
    code = helpers() + fragment("function identityResolutionMessage(", "window.refreshBrowseIdentity=")
    payload = {"matched": 0, "provider_errors": {"upstream": '<img src=x onerror="evil()">'}}
    result = run_node(code + "const value=" + json.dumps(payload) + ";console.log(JSON.stringify({html:identityResolutionDiagnostics(value),message:identityResolutionMessage(value)}));")
    assert "<img" not in result["html"]
    assert result["message"].startswith("Still unresolved")


@pytest.mark.parametrize("failed", [False, True])
def test_preference_save_does_not_overwrite_edits_made_during_request(failed):
    code = fragment("function queueUiPreferenceSave(", "function applySourceSectionPreferences(")
    harness = '''
let uiPreferencesState={values:{theme:'blue'},pending:{theme:'blue'},saveFailures:0};
let finish, active=0, maxActive=0;
function setTimeout(){return 1;}function clearTimeout(){}
function writeLocalUiPreference(){}function updateUiPreferenceOwner(){}function rememberClientError(){}
function api(){active++;maxActive=Math.max(active,maxActive);return new Promise((resolve,reject)=>{finish=()=>{active--;FAILED?reject(Error('offline')):resolve({values:{theme:'blue'}});};});}
'''.replace("FAILED", json.dumps(failed))
    tail = '''
(async()=>{const first=flushUiPreferenceSave();await Promise.resolve();queueUiPreferenceSave('theme','red');await flushUiPreferenceSave();finish();await first;console.log(JSON.stringify({values:uiPreferencesState.values,pending:uiPreferencesState.pending,maxActive}));})().catch(e=>{console.error(e);process.exit(1)});
'''
    result = run_node(harness + code + tail)
    assert result["values"]["theme"] == "red"
    assert result["pending"]["theme"] == "red"
    assert result["maxActive"] == 1


def test_api_releases_abort_listener_after_successful_request():
    code = fragment("async function api(", "window.addEventListener('unhandledrejection'")
    harness = '''
let token='', clientErrors=[], added=0, removed=0;
function scheduleMutationRefresh(){}function rememberClientError(){}
async function fetch(){return {status:200,ok:true,json:async()=>({ok:true})};}
const signal={aborted:false,addEventListener(){added++;},removeEventListener(){removed++;}};
'''
    result = run_node(harness + code + "api('/test',{signal,timeoutMs:0}).then(()=>console.log(JSON.stringify({added,removed}))).catch(e=>{console.error(e);process.exit(1)});")
    assert result == {"added": 1, "removed": 1}


def test_token_prompt_works_when_browser_storage_is_blocked():
    code = fragment("async function api(", "window.addEventListener('unhandledrejection'")
    harness = '''
let token='',clientErrors=[],calls=0,observed='',adminAuthDismissed=false;
const localStorage={setItem(){throw Error('blocked storage')}};
function prompt(){return 'new-token'}function scheduleMutationRefresh(){}function rememberClientError(){}
async function fetch(url,opts){calls++;observed=opts.headers['X-Admin-Token']||'';return {status:calls===1?401:200,ok:calls>1,json:async()=>({ok:true})};}
'''
    result = run_node(harness + code + "api('/test',{timeoutMs:0}).then(()=>console.log(JSON.stringify({token,calls,observed}))).catch(e=>{console.error(e);process.exit(1)});")
    assert result == {"token": "new-token", "calls": 2, "observed": "new-token"}
