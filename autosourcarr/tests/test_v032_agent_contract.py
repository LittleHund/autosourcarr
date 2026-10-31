"""Optional actual pinned-class test: set SOURCE_BROWSER_TEST_RUNTIME_JAR and JAVA_HOME.

The JAR is the checksum-verified official v2.3.2243 release, not a fixture that
imitates our hooks. No downloads, source traffic or native Chromium are started.
"""
import hashlib
import os
from pathlib import Path
import subprocess

import pytest


def test_real_pinned_classes_accept_custom_ua_and_restore_defaults(tmp_path):
    jar_value = os.environ.get('SOURCE_BROWSER_TEST_RUNTIME_JAR')
    if not jar_value:
        pytest.skip('Set SOURCE_BROWSER_TEST_RUNTIME_JAR to run the actual pinned JVM class contract')
    runtime = Path(jar_value)
    assert hashlib.sha256(runtime.read_bytes()).hexdigest() == '821141b32e170d4a02d3cbdfed577ed8f07bd22383ff5f4132ebb5ae40e98dd5'
    jdk = Path(os.environ['JAVA_HOME']) / 'bin'
    root = Path(__file__).resolve().parents[1]
    classes = tmp_path/'classes';classes.mkdir()
    def run(*args, env=None):
        result = subprocess.run([str(x) for x in args], env=env, capture_output=True, text=True, timeout=40)
        assert result.returncode == 0, result.stdout+result.stderr
        return result.stdout
    run(jdk/'javac','--add-exports','java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED','-d',classes,root/'source-browser-agent/SourceBrowserAgent.java')
    agent = tmp_path/'browser-agent.jar'
    run(jdk/'jar','cfm',agent,root/'source-browser-agent/MANIFEST.MF','-C',classes,'.')
    run(jdk/'javac','-cp',runtime,'-d',classes,root/'tests/fixtures/source-browser/AgentRuntimeContract.java')
    from app.source_browser import DEFAULT_UA
    env = {k:v for k,v in os.environ.items() if not k.startswith('AUTOSOURCARR_SOURCE_UA') and k not in {'AUTOSOURCARR_SOURCE_USER_AGENT','JAVA_TOOL_OPTIONS','_JAVA_OPTIONS','JDK_JAVA_OPTIONS'}}
    status = tmp_path/'status.properties'
    ua = 'Mozilla/5.0 AutoSourcarr contract identity'
    custom = {**env,'AUTOSOURCARR_SOURCE_USER_AGENT':ua,'AUTOSOURCARR_SOURCE_UA_REVISION':'contract','AUTOSOURCARR_SOURCE_UA_STATUS':str(status)}
    args = [jdk/'java','-Xverify:all','-javaagent:'+str(agent),'-cp',str(classes)+os.pathsep+str(runtime),'AgentRuntimeContract']
    assert 'custom=true' in run(*args,ua,'true',env=custom)
    assert 'network=applied' in status.read_text() and 'webview=applied' in status.read_text()
    assert 'custom=false' in run(*args,DEFAULT_UA,'false',env=env)
