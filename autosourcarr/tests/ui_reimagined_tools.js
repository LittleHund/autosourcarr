'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../app/static/instance-tools.js'),'utf8');

async function scenario(kind,{downloads=false}={}){
  let legacyBrowse=0,closedNavigation=0;const calls=[],nodes=new Map();
  function node(id){
    if(!nodes.has(id))nodes.set(id,{id,open:false,disabled:false,textContent:'',innerHTML:'',children:[],
      replaceChildren(...children){this.children=children;this.innerHTML='';},append(...children){this.children.push(...children);},insertAdjacentHTML(_where,html){this.innerHTML+=html;},
      addEventListener(){},showModal(){this.open=true;},close(){this.open=false;},focus(){}});
    return nodes.get(id);
  }
  const nav=node('nav-tool');nav.dataset={instanceTool:kind};nav.onclick=()=>legacyBrowse++;
  const document={activeElement:nav,getElementById:node,createElement:tag=>node('created-'+nodes.size),
    querySelectorAll:selector=>selector==='.appNav [data-instance-tool]'?[nav]:[],addEventListener(){}};
  const api=async url=>{
    calls.push(url);
    if(url.startsWith('/api/v1/activity/history')){
      const older=url.includes('downloads_before=');
      const page=older?{items:[{id:7,entry_id:3,chapter_id:30,event:'failed',detail:'Older',created_at:'2026-10-17T12:00:00Z'}],has_more:false,next_before:null}
        :{items:[{id:9,entry_id:3,chapter_id:31,event:'downloaded',detail:'',created_at:'2026-10-18T12:00:00Z'}],has_more:true,next_before:9};
      return {items:[{name:'Retained task',state:'completed',updated_at:'2026-10-18T12:00:00Z'}],total:1,note:'Saved history',...(downloads?{downloads:page}:{})};
    }
    if(url==='/api/admin/instance/backups')return {items:[],pending_restore:false};
    if(url==='/api/v1/downloads')return {state:'STOPPED',queue:[],revision:'fixture'};
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
  assert.equal(calls[0],{history:'/api/v1/activity/history?offset=0&limit=50',queue:'/api/v1/downloads',backups:'/api/admin/instance/backups'}[kind]);
  if(!downloads)return;
  // PERF-5: download events page by keyset; the older page is appended below the rows already shown.
  const rows=node('riDownloadRows');assert.match(node('riInstanceBody').innerHTML,/downloaded/);
  const pager=node('riDownloadPager');assert.equal(pager.children.length,1);assert.equal(pager.children[0].textContent,'Older downloads');
  await pager.children[0].onclick();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls.at(-1),'/api/v1/activity/history?offset=0&limit=50&downloads_before=9');
  assert.match(rows.innerHTML,/Older/);assert.equal(pager.children.length,0,'No more pages: no button');
}
(async()=>{for(const kind of ['history','queue','backups'])await scenario(kind);await scenario('history',{downloads:true});console.log('Reimagined tools: no legacy Browse side effect; correct read, modal and navigation ownership');})().catch(error=>{console.error(error);process.exitCode=1;});
