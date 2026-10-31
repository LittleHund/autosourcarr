"""Current-instance repair setup remains usable before the process is ready."""
from html.parser import HTMLParser
from test_v086_ui import node
from test_v0315_repair_ui import DOM, SOURCE, STATIC


def test_setup_button_opens_account_section_without_waiting_for_worker():
    node(DOM+SOURCE+r'''
(async()=>{
 connected=false;
 await click('openRepairSetup');
 assert(ids.repairSetupPanel.open);
 assert.equal(document.activeElement,ids.configureRepairAI);
 assert.equal(requests.length,0);
 active=true;await loadSourceRepairs();
 assert(!ids.openRepairSetup.disabled&&!ids.configureRepairAI.disabled);
 assert(!/Compose|worker address|select.*instance/i.test(ids.sourceRepairStatus.textContent));
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_setup_is_available_while_status_request_is_pending():
    fixture=DOM.replace("return {mode,service:serviceState,configured:connected,reachable:connected,web_control:true,jobs:[],watch:{}};",
                        "return new Promise(resolve=>{globalThis.finishStatus=resolve});")
    node(fixture+SOURCE+r'''
(async()=>{
 active=true;const pending=loadSourceRepairs();await tick();
 await click('openRepairSetup');assert(ids.repairSetupPanel.open);
 let opened=0;window.AutoSourcarrSetup={openRepairAI:()=>{opened++;}};
 await click('configureRepairAI');assert.equal(opened,1);
 finishStatus({mode:'integrated',configured:false,service:{state:'starting',message:'Starting internal repair'}});await pending;
 assert.equal(ids.sourceRepairStatus.textContent,'Starting internal repair');
 assert(!ids.configureRepairAI.disabled);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_unbundled_install_does_not_offer_external_deployment():
    node(DOM+SOURCE+r'''
(async()=>{
 mode='external';connected=false;active=true;await loadSourceRepairs();
 assert.match(ids.sourceRepairStatus.textContent,/Rebuild and restart/);
 assert(!/generate|Compose|token|select.*service/i.test(ids.sourceRepairStatus.textContent));
 await click('openRepairSetup');assert.equal(document.activeElement,ids.configureRepairAI);
 assert(!requests.some(r=>r.path.endsWith('/connection')||r.path.endsWith('/setup-preview')));
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_html_has_accessible_account_action_and_no_worker_forms():
    class Page(HTMLParser):
        def __init__(self):
            super().__init__();self.details=[];self.elements={}
        def handle_starttag(self,tag,attrs):
            attrs=dict(attrs)
            if tag=='details': self.details.append(attrs.get('id'))
            if attrs.get('id'): self.elements[attrs['id']]=(tag,attrs,list(self.details))
        def handle_endtag(self,tag):
            if tag=='details': self.details.pop()
    page=Page();page.feed((STATIC/'index.html').read_text())
    tag,attrs,ancestors=page.elements['openRepairSetup']
    assert tag=='button' and attrs['type']=='button' and 'disabled' not in attrs
    assert attrs['aria-controls']=='repairSetupPanel' and 'repairSetupPanel' not in ancestors
    assert not page.details
    assert page.elements['configureRepairAI'][1]['type']=='button'
    assert not {'repairComposeForm','repairFirstSetup','repairConnectionForm','repairNormalService','repairPrivateService'} & page.elements.keys()
