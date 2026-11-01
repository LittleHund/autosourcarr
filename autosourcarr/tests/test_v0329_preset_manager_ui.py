"""Controlled-DOM behaviors for the shipped preset editor and independent drafts."""
import json
import os
from pathlib import Path
import subprocess

import pytest


ROOT = Path(os.environ.get('AUTOSOURCARR_REPRO_ROOT', Path(__file__).resolve().parents[2]))


DOM_AND_API = r'''
// Extend the existing rendered-rule DOM with actual static preset form controls.
const html=fs.readFileSync(root+'/autosourcarr/app/static/index.html','utf8');
const presetHtml=/<form id="matchingPresetForm"[\s\S]*?<\/form>/.exec(html)?.[0];
assert(presetHtml,'The shipped HTML contains the preset manager form');
for(const id of html.matchAll(/\bid="(matchingPreset[^\"]+)"/g))nodes.set(id[1],new Node(id[1]));
const presetForm=nodes.get('matchingPresetForm');presetForm.elements={};presetForm.children=[];
for(const tag of presetHtml.matchAll(/<(input|select|textarea|button|fieldset)\b([^>]*)>/g)){
 const [,kind,attrs]=tag,id=/\bid="([^\"]+)"/.exec(attrs)?.[1],name=/\bname="([^\"]+)"/.exec(attrs)?.[1];
 const node=id?nodes.get(id):new Node();node.kind=kind;node.name=name||'';node.value=/\bvalue="([^\"]*)"/.exec(attrs)?.[1]||'';node.checked=/\bchecked\b/.test(attrs);
 if(name)presetForm.elements[name]=node;presetForm.children.push(node);
}
const baseQuery=Node.prototype.querySelector,baseQueries=Node.prototype.querySelectorAll;
Node.prototype.querySelector=function(selector){if(selector==='.rulePresetSave'&&this.dataset.direction)return this.presetButton??=new Node();return baseQuery.call(this,selector);};
Node.prototype.querySelectorAll=function(selector){
 if(this.id==='matchingPresetForm')return this.children.filter(node=>selector.split(',').map(s=>s.trim()).includes(node.kind));
 if(this.id==='matchingPolicyForms'&&selector==='button')return this.children.flatMap(form=>[form.button,form.querySelector('.rulePresetSave')]);
 return baseQueries.call(this,selector);
};
let server={revision:'rules-1',preset_revision:'presets-1',groups:[{id:7,name:'Group'}],sources:[{id:'a',name:'Source A',lang:'en',group_id:7}],rules:[],presets:[
 {id:'title',builtin:true,name:'Title template',description:'A built-in template',rule:{enabled:true,checks:{title:.97}}},
 {id:'custom-current',builtin:false,name:'My template',description:'An editable template',rule:{enabled:true,checks:{title:.98,author:.91}}},
 {id:'custom-disabled',builtin:false,name:'Disabled template',description:'No automatic matching',rule:{enabled:false,checks:{}}}
]};
const catalog=()=>structuredClone({presets:server.presets,preset_revision:server.preset_revision});
let heldRules=null,heldMutation=null,mutationCount=0;
const context={console,Number,String,Date,JSON,Promise,encodeURIComponent,confirm:()=>true,document:{hidden:false,getElementById:id=>nodes.get(id)},activeSurface:()=>true,
 esc:value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;'),
 openBrowseEntry:()=>{},openUnmatchDuplicatesForEntry:()=>{},
 api:async(path,options={})=>{
  calls.push([path,options]);
  if(path==='/api/v1/matching-policies')return heldRules||structuredClone(server);
  if(path.startsWith('/api/v1/matching-policies/')){const body=JSON.parse(options.body);assert.equal(body.revision,server.revision);const direction=path.split('/').at(-1);server.rules.push({scope:'source',subject:'a',direction,rule:body.rule});server.revision+='x';return structuredClone(server);}
  if(path.startsWith('/api/v1/matching-presets')){
   if(!options.method)return catalog();
   const body=JSON.parse(options.body);assert.equal(body.revision,server.preset_revision);
   if(heldMutation)await heldMutation;
   const id=options.method==='POST'?'custom-new-'+(++mutationCount):decodeURIComponent(path.split('/').at(-1));
   server.presets=server.presets.filter(p=>p.id!==id);
   if(options.method!=='DELETE')server.presets.push({id,builtin:false,name:body.name,description:body.description,rule:structuredClone(body.rule)});
   server.preset_revision+='x';return catalog();
  }
  throw Error('Unexpected request '+path);
 }};
context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync(root+'/autosourcarr/app/static/matching-review.js','utf8'),context);
const node=id=>nodes.get(id);
const event=(target,type)=>target.events[type]({target,preventDefault(){}});
const click=id=>{assert(!node(id).disabled,'The clicked control is enabled: '+id);return event(node(id),'click');};
const submitPreset=()=>event(presetForm,'submit');
const choose=id=>{node('matchingPresetTarget').value=id;event(node('matchingPresetTarget'),'change');};
const text=(id,value)=>{assert(!node(id).disabled);node(id).value=value;event(node(id),'input');};
const open=async()=>{context.openMatchingPolicy('source','a');await tick();};
const policies=()=>calls.filter(([path,opts])=>path.startsWith('/api/v1/matching-policies/')&&opts.method==='PUT');
const writes=()=>calls.filter(([path,opts])=>path.startsWith('/api/v1/matching-presets')&&opts.method);
'''


CASES = {
    'create_edit_and_apply_copy': r'''
 await open();const [same,cross]=node('matchingPolicyForms').children;
 same.elements.mode.value='custom';event(same.elements.mode,'change');same.elements.title.checked=true;same.elements.titlePercent.value='99';
 cross.elements.summaryPercent.value='88';
 event(same.querySelector('.rulePresetSave'),'click');
 assert.equal(Number(presetForm.elements.titlePercent.value),99,'Save draft as preset seeds the actual submitted direction');assert(presetForm.elements.title.checked);
 assert.equal(policies().length,0);assert.equal(writes().length,0,'Opening the editor does not save rules or presets');
 text('matchingPresetName','My <creator> draft');text('matchingPresetDescription','Use this when data is available.');
 submitPreset();await tick();
 const created=server.presets.find(p=>p.name==='My <creator> draft');assert(created);assert.equal(created.rule.checks.title,.99);
 assert.equal(policies().length,0,'Creating a preset never applies a source rule');assert.equal(cross.elements.summaryPercent.value,'88');
 assert(node('matchingPresetTarget').innerHTML.includes('My &lt;creator&gt; draft'),'Saved names are escaped as option text');assert(!node('matchingPresetTarget').innerHTML.includes('<creator>'));
 same.elements.preset.value=created.id;event(same.elements.preset,'change');
 assert.equal(policies().length,0,'Loading a custom preset fills a draft only');event(same,'submit');await tick();
 assert.deepEqual(JSON.parse(policies()[0][1].body).rule,{enabled:true,checks:{title:.99}});
 choose(created.id);text('matchingPresetName','Renamed template');presetForm.elements.titlePercent.value='98';event(presetForm.elements.titlePercent,'input');
 submitPreset();await tick();assert.equal(writes().at(-1)[1].method,'PUT');
 assert.equal(server.presets.find(p=>p.id===created.id).rule.checks.title,.98);assert.equal(server.rules[0].rule.checks.title,.99,'Editing the reusable template does not rewrite applied rule copies');
 assert.equal(cross.elements.summaryPercent.value,'88','Independent cross-source draft survives both editor saves');
''',
    'built_in_copy_and_disabled_template': r'''
 await open();choose('title');assert.equal(node('matchingPresetSave').textContent,'Save as new');assert(node('matchingPresetDelete').hidden);
 text('matchingPresetName','Copied title template');submitPreset();await tick();assert.equal(writes().at(-1)[1].method,'POST');assert(server.presets.find(p=>p.id==='title').builtin,'Built-in remains available');
 choose('custom-disabled');assert.equal(presetForm.elements.mode.value,'off');assert(node('matchingPresetChecks').disabled);
 text('matchingPresetDescription','Keep automatic matching off.');submitPreset();await tick();
 assert.deepEqual(JSON.parse(writes().at(-1)[1].body).rule,{enabled:false,checks:{}});assert(node('matchingPresetChecks').disabled,'Disabled template keeps evidence controls disabled after Save');
 const same=node('matchingPolicyForms').children[0];same.elements.preset.value='custom-disabled';event(same.elements.preset,'change');assert.equal(same.elements.mode.value,'off');assert(same.fieldset.disabled);
 assert.equal(policies().length,0);event(same,'submit');await tick();assert.deepEqual(JSON.parse(policies()[0][1].body).rule,{enabled:false,checks:{}});
''',
    'pending_preset_save_navigation_ownership': r'''
 await open();choose('custom-current');text('matchingPresetName','Pending renamed template');let finish;heldMutation=new Promise(resolve=>finish=resolve);
 submitPreset();await tick();assert(node('matchingPresetName').disabled&&node('matchingPresetTarget').disabled,'Submitted editor is frozen');
 context.AutoSourcarrMatchingReview.restoreViewState({open:false});assert(!node('matchingPresetName').disabled,'A new navigation view releases controls');
 const before=node('matchingPresetStatus').textContent;finish();await tick();
 assert.equal(node('matchingPresetStatus').textContent,before,'An obsolete save cannot paint the new view');assert.equal(node('matchingPolicyForms').children.length,0,'An obsolete save cannot restore old rule forms');
''',
    'old_rule_get_preserves_new_preset_catalog': r'''
 await open();const old=structuredClone(server);let finish;heldRules=new Promise(resolve=>finish=resolve);
 click('matchingPolicyReload');await tick();choose('custom-current');text('matchingPresetName','Newest saved name');submitPreset();await tick();
 assert(node('matchingPresetTarget').innerHTML.includes('Newest saved name'));const savedRevision=server.preset_revision;
 finish(old);heldRules=null;await tick();
 assert(node('matchingPresetTarget').innerHTML.includes('Newest saved name'),'A full rules GET begun before preset Save cannot replace its newest catalogue');
 choose('custom-current');assert.equal(node('matchingPresetName').value,'Newest saved name');
 text('matchingPresetDescription','Second edit after delayed rules response');submitPreset();await tick();
 assert.equal(JSON.parse(writes().at(-1)[1].body).revision,savedRevision,'The next preset save uses its newest independent revision');
''',
}


@pytest.mark.parametrize('case', list(CASES))
def test_matching_preset_manager_behavior(case):
    baseline = (ROOT / 'autosourcarr/tests/ui_matching_review.js').read_text()
    fixture = baseline[:baseline.index('const config=')]
    program = fixture + 'const root=' + json.dumps(str(ROOT)) + ';\n' + DOM_AND_API
    program += '(async()=>{\n' + CASES[case] + '\nconsole.log("PASS ' + case + '");})().catch(error=>{console.error(error);process.exitCode=1});'
    result = subprocess.run(['node', '-e', program], cwd=ROOT, text=True, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stdout + result.stderr
