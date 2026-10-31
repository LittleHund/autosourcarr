'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../app/static/instance-tools.js'),'utf8');

async function scenario(kind){
  let legacyBrowse=0,closedNavigation=0;const calls=[],nodes=new Map();
  function node(id){
    if(!nodes.has(id))nodes.set(id,{id,open:false,disabled:false,textContent:'',innerHTML:'',children:[],
      replaceChildren(...children){this.children=children;this.innerHTML='';},append(...children){this.children.push(...children);},
      addEventListener(){},showModal(){this.open=true;},close(){this.open=false;},focus(){}});
    return nodes.get(id);
  }
  const nav=node('nav-tool');nav.dataset={instanceTool:kind};nav.onclick=()=>legacyBrowse++;
  const document={activeElement:nav,getElementById:node,createElement:tag=>node('created-'+nodes.size),
    querySelectorAll:selector=>selector==='.appNav [data-instance-tool]'?[nav]:[],addEventListener(){}};
  const api=async url=>{
    calls.push(url);
    if(url.startsWith('/api/admin/activity/history'))return {items:[{name:'Retained task',state:'completed',updated_at:'2026-10-18T12:00:00Z'}],total:1,note:'Saved history'};
    if(url==='/api/admin/instance/backups')return {items:[],pending_restore:false};
    if(url==='/api/admin/downloads')return {state:'STOPPED',queue:[],revision:'fixture'};
    throw Error('Unexpected source/network action '+url);
  };
  const window={AutoSourcarrReimagined:{closeNavigation:()=>closedNavigation++}};
  vm.runInNewContext(source,{window,document,api,AbortController,TextEncoder,URL,setTimeout,confirm:()=>false,
    esc:value=>String(value??''),matchMedia:()=>({matches:false})});
  nav.onclick({preventDefault(){},stopPropagation(){}});
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(legacyBrowse,0,'A tool must replace the legacy nav handler, which otherwise opens Browse');
  assert.equal(closedNavigation,1,'Close mobile navigation through the shell without changing the selected page');
  assert.equal(node('riInstanceDialog').open,true);
  assert.equal(calls.length,1,'Opening a tool performs only its read endpoint');
  assert.equal(calls[0],{history:'/api/admin/activity/history?offset=0&limit=50',queue:'/api/admin/downloads',backups:'/api/admin/instance/backups'}[kind]);
}
(async()=>{for(const kind of ['history','queue','backups'])await scenario(kind);console.log('Reimagined tools: no legacy Browse side effect; correct read, modal and navigation ownership');})().catch(error=>{console.error(error);process.exitCode=1;});
