/* UI-13 / PERF-8: the shared live-event client (live-events.js) with a fake EventSource, fake clock and fake tab. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const source=fs.readFileSync(path.join(projectRoot,'autosourcarr/app/static/live-events.js'),'utf8');
const tick=()=>new Promise(resolve=>setImmediate(resolve));

function harness({eventSource=true,token='',fetchImpl=null,options={}}={}){
  let now=0,seq=0;const timers=new Map(),sources=[],listeners={};
  const clock={
    setTimeout:(fn,ms)=>{const id=++seq;timers.set(id,{fn,at:now+Math.max(0,ms)});return id;},
    clearTimeout:id=>{timers.delete(id);},
    async advance(ms){
      const end=now+ms;
      for(;;){
        const next=[...timers.entries()].filter(([,t])=>t.at<=end).sort((a,b)=>a[1].at-b[1].at||a[0]-b[0])[0];
        if(!next)break;
        timers.delete(next[0]);now=next[1].at;next[1].fn();await tick();await tick();
      }
      now=end;await tick();
    },
  };
  class FakeEventSource{
    constructor(url){this.url=url;this.listeners={};this.closed=false;sources.push(this);}
    addEventListener(name,fn){(this.listeners[name]||=[]).push(fn);}
    close(){this.closed=true;}
    open(){this.onopen?.();}
    fail(){this.onerror?.();}
    send(name,id,data){for(const fn of this.listeners[name]||[])fn({data:JSON.stringify({id,topic:name,at:'t',data}),lastEventId:String(id)});}
  }
  const document={hidden:false,addEventListener:(type,fn)=>{(listeners[type]||=[]).push(fn);},removeEventListener(){},
    setHidden(value){this.hidden=value;for(const fn of listeners.visibilitychange||[])fn();}};
  const context={console:{debug(){},error(){}},document};
  context.window=context;vm.createContext(context);vm.runInContext(source,context);
  const live=context.AutoSourcarrLive.create({window:context,document,setTimeout:clock.setTimeout,clearTimeout:clock.clearTimeout,
    EventSource:eventSource?FakeEventSource:null,fetch:fetchImpl,ReadableStream:fetchImpl?function(){}:null,
    TextDecoder,AbortController,getToken:()=>token,options});
  return {live,clock,sources,document,last:()=>sources[sources.length-1]};
}
function recorder(){const calls=[];return {calls,refresh:batch=>{calls.push(batch);}};}

const tests=[];const test=(name,fn)=>tests.push({name,fn});

test('subscribe: only subscribed topics reach a view, and only while it is visible',async()=>{
  const h=harness();h.live.start();h.last().open();
  let visible=true;const tasks=recorder(),library=recorder();
  h.live.subscribe({name:'tasks',topics:['tasks','jobs'],refresh:tasks.refresh,when:()=>visible});
  h.live.subscribe({name:'library',topics:['library'],refresh:library.refresh});
  h.last().send('tasks',1,{change:'updated'});await h.clock.advance(300);
  assert.equal(tasks.calls.length,1);assert.equal(library.calls.length,0);
  assert.equal(tasks.calls[0].events[0].topic,'tasks');assert.equal(tasks.calls[0].events[0].id,1);
  visible=false;h.last().send('jobs',2,{change:'completed'});await h.clock.advance(300);
  assert.equal(tasks.calls.length,1,'An invisible view is not refreshed');
  visible=true;await h.clock.advance(5000);assert.equal(tasks.calls.length,1,'What an invisible view missed is dropped (it loads itself when opened)');
  h.live.subscribe({name:'late',topics:['duplicates'],refresh:()=>{}});
  assert.equal(h.last().listeners.duplicates?.length,1,'A topic subscribed after connect is listened to on the open stream');
});

test('coalescing: a burst of events causes one refresh per view, later events one more',async()=>{
  const h=harness();h.live.start();h.last().open();const view=recorder();
  h.live.subscribe({name:'jobs',topics:['jobs'],refresh:view.refresh,delayMs:500});
  for(let id=1;id<=40;id++)h.last().send('jobs',id,{change:'progress'});
  await h.clock.advance(499);assert.equal(view.calls.length,0);
  await h.clock.advance(2);assert.equal(view.calls.length,1);assert.equal(view.calls[0].events.length,40);
  h.last().send('jobs',41,{change:'completed'});await h.clock.advance(600);assert.equal(view.calls.length,2);
});

test('coalescing: events during a running refresh lead to exactly one follow-up refresh',async()=>{
  const h=harness();h.live.start();h.last().open();let release;const calls=[];
  h.live.subscribe({name:'slow',topics:['tasks'],delayMs:100,refresh:batch=>{calls.push(batch);return new Promise(resolve=>{release=resolve;});}});
  h.last().send('tasks',1,{});await h.clock.advance(150);assert.equal(calls.length,1);
  for(let id=2;id<6;id++)h.last().send('tasks',id,{});await h.clock.advance(500);
  assert.equal(calls.length,1,'No overlapping refresh');
  release();await tick();await h.clock.advance(150);assert.equal(calls.length,2);assert.equal(calls[1].events.length,4);
});

test('filter: a view can ignore events it does not need',async()=>{
  const h=harness();h.live.start();h.last().open();const view=recorder();
  h.live.subscribe({name:'done',topics:['jobs'],refresh:view.refresh,filter:e=>e.data?.change!=='progress'});
  h.last().send('jobs',1,{change:'progress'});await h.clock.advance(300);assert.equal(view.calls.length,0);
  h.last().send('jobs',2,{change:'completed'});await h.clock.advance(300);assert.equal(view.calls.length,1);
});

test('resume: reconnect backs off and resumes from the last event id',async()=>{
  const h=harness();h.live.start();assert.equal(h.last().url,'/api/v1/events');h.last().open();
  h.last().send('tasks',17,{});assert.equal(h.live.status().lastId,'17');
  const first=h.last();first.fail();assert.equal(first.closed,true,'Our own back-off replaces the browser auto-reconnect');
  assert.equal(h.sources.length,1);await h.clock.advance(999);assert.equal(h.sources.length,1);
  await h.clock.advance(2);assert.equal(h.sources.length,2);assert.equal(h.last().url,'/api/v1/events?last_event_id=17');
  h.last().fail();await h.clock.advance(1999);assert.equal(h.sources.length,2,'Second failure waits twice as long');
  await h.clock.advance(2);assert.equal(h.sources.length,3);h.last().fail();await h.clock.advance(4001);assert.equal(h.sources.length,4);
  h.last().open();assert.equal(h.live.status().connected,true);assert.equal(h.live.status().failures,0);
});

test('resync: reloads visible views only, once each',async()=>{
  const h=harness();h.live.start();h.last().open();
  const shown=recorder(),hidden=recorder();
  h.live.subscribe({name:'shown',topics:['tasks'],refresh:shown.refresh});
  h.live.subscribe({name:'hidden',topics:['library'],refresh:hidden.refresh,when:()=>false});
  h.last().send('resync',90,{reason:'gap'});await h.clock.advance(10);
  assert.equal(shown.calls.length,1);assert.equal(shown.calls[0].resync,true);assert.equal(hidden.calls.length,0);
  assert.equal(h.live.status().lastId,'90','Resuming continues after the resync point');
});

test('reconnect without any id is treated as a resync of the visible views',async()=>{
  const h=harness();h.live.start();h.last().open();const view=recorder();
  h.live.subscribe({name:'v',topics:['tasks'],refresh:view.refresh});
  h.last().fail();await h.clock.advance(1001);h.last().open();await h.clock.advance(10);
  assert.equal(view.calls.length,1);assert.equal(view.calls[0].resync,true);
});

test('hidden tab: nothing refreshes while hidden; one catch-up refresh on return; stream closes after the grace and resumes',async()=>{
  const h=harness({options:{hiddenCloseMs:20000}});h.live.start();h.last().open();const view=recorder();
  h.live.subscribe({name:'v',topics:['jobs'],refresh:view.refresh});
  h.document.setHidden(true);
  for(let id=1;id<=5;id++)h.last().send('jobs',id,{change:'progress'});
  await h.clock.advance(10000);assert.equal(view.calls.length,0,'Hidden tab does not refresh');
  await h.clock.advance(10001);assert.equal(h.last().closed,true,'Stream closed after the hidden grace');assert.equal(h.live.status().paused,true);
  await h.clock.advance(600000);assert.equal(h.sources.length,1,'No reconnects while hidden');
  h.document.setHidden(false);assert.equal(h.sources.length,2);assert.equal(h.last().url,'/api/v1/events?last_event_id=5');
  await h.clock.advance(10);assert.equal(view.calls.length,1,'One catch-up refresh');assert.equal(view.calls[0].events.length,5);
  h.last().open();h.last().send('jobs',6,{});h.last().send('jobs',7,{});await h.clock.advance(300);assert.equal(view.calls.length,2,'Replayed backlog coalesced');
});

test('busy view: refresh waits while the user edits, shows a hint, then runs once',async()=>{
  const h=harness();h.live.start();h.last().open();const view=recorder();let editing=true;const hints=[];
  h.live.subscribe({name:'form',topics:['tasks'],refresh:view.refresh,busy:()=>editing,hint:on=>hints.push(on)});
  h.last().send('tasks',1,{});h.last().send('tasks',2,{});await h.clock.advance(3000);
  assert.equal(view.calls.length,0);assert.deepEqual(hints,[true]);
  editing=false;await h.clock.advance(1001);assert.equal(view.calls.length,1);assert.deepEqual(hints,[true,false]);
});

test('fallback: without EventSource or fetch streaming a slow poll tick stands in, only while visible',async()=>{
  const h=harness({eventSource:false,options:{fallbackPollMs:30000}});h.live.start();const view=recorder();
  h.live.subscribe({name:'v',topics:['tasks'],refresh:view.refresh});
  assert.equal(h.live.status().mode,'fallback');
  await h.clock.advance(29999);assert.equal(view.calls.length,0);await h.clock.advance(2);assert.equal(view.calls.length,1);assert.equal(view.calls[0].poll,true);
  h.document.setHidden(true);await h.clock.advance(60000);assert.equal(view.calls.length,1,'No poll while hidden');
  h.document.setHidden(false);await h.clock.advance(10);assert.equal(view.calls.length,2,'Catch-up on return');
});

test('fallback: a stream that keeps failing switches to the slow poll and keeps retrying the stream',async()=>{
  const h=harness({options:{failuresBeforeFallback:3,fallbackPollMs:30000,fallbackRetryMs:120000}});h.live.start();const view=recorder();
  h.live.subscribe({name:'v',topics:['tasks'],refresh:view.refresh});
  h.last().fail();await h.clock.advance(1001);h.last().fail();await h.clock.advance(2001);h.last().fail();
  assert.equal(h.live.status().mode,'fallback');
  await h.clock.advance(30001);assert.equal(view.calls.length,1);
  const count=h.sources.length;await h.clock.advance(90000);assert.equal(h.sources.length,count+1,'Stream retried after the long interval');
  h.last().open();assert.equal(h.live.status().mode,'live');const calls=view.calls.length;await h.clock.advance(120000);
  assert.equal(view.calls.length,calls+1,'One resync on reconnect, then no more polling ticks');
});

test('token-only mode reads the stream with fetch, sends the header and Last-Event-ID, never puts the token in a URL',async()=>{
  const requests=[];let push;
  const fetchImpl=async(url,init)=>{requests.push({url,init});
    const chunks=[];let wake=null;push=text=>{chunks.push(new TextEncoder().encode(text));wake?.();};
    return {ok:true,status:200,body:{getReader:()=>({read:async()=>{while(!chunks.length)await new Promise(r=>{wake=r;});wake=null;const value=chunks.shift();return value.length?{value,done:false}:{value:undefined,done:true};}})}};};
  const h=harness({token:'secret-token',fetchImpl});h.live.start();await tick();
  const view=recorder();h.live.subscribe({name:'v',topics:['jobs'],refresh:view.refresh});
  assert.equal(requests.length,1);assert.equal(requests[0].url,'/api/v1/events');assert.equal(requests[0].init.headers['X-Admin-Token'],'secret-token');
  push('retry: 3000\n: connected, last id 41\n\n');await tick();assert.equal(h.live.status().lastId,'41','Initial id learned from the connect comment');
  push('id: 42\nevent: jobs\ndata: {"id":42,"topic":"jobs","at":"t","data":{"change":"completed"}}\n\n');await tick();await h.clock.advance(300);
  assert.equal(view.calls.length,1);assert.equal(view.calls[0].events[0].data.change,'completed');
  push('');await tick();await tick();await h.clock.advance(3001);
  assert.equal(requests.length,2);assert.equal(requests[1].init.headers['Last-Event-ID'],'42');assert.equal(requests[1].url.includes('secret'),false);
  assert.equal(h.sources.length,0,'EventSource is not used when a header is needed');
});

test('precise topics: every server topic is listened to and its keys reach the filter and the refresh',async()=>{
  const h=harness();h.live.start();
  for(const topic of ['catalogue','library','orphans','sources','duplicates','imports','repairs','source-health'])
    assert.equal(h.last().listeners[topic]?.length,1,'EventSource listens to '+topic);
  h.last().open();const view=recorder();
  h.live.subscribe({name:'entries',topics:['catalogue'],refresh:view.refresh,filter:e=>e.data.truncated||e.data.entry_ids.includes(7)});
  h.last().send('catalogue',1,{change:'metadata',entry_ids:[3],truncated:false});await h.clock.advance(300);
  assert.equal(view.calls.length,0,'An entry not on screen is ignored');
  h.last().send('catalogue',2,{change:'titles',entry_ids:[7,8],truncated:false});await h.clock.advance(300);
  assert.equal(view.calls.length,1);assert.deepEqual([...view.calls[0].events[0].data.entry_ids],[7,8]);
});

function domHarness(){
  let now=1000;const listeners={};
  class El{
    constructor(tag,type=''){this.tagName=tag;this.type=type;this.parent=null;this.after=[];this.removed=false;this.clicks=[];this.attributes={};}
    contains(node){for(let n=node;n;n=n.parent)if(n===this)return true;return false;}
    insertAdjacentElement(where,node){assert.equal(where,'afterend');this.after.push(node);node.anchor=this;}
    remove(){this.removed=true;if(this.anchor)this.anchor.after=this.anchor.after.filter(n=>n!==this);}
    addEventListener(type,fn){this.clicks.push(fn);}
    setAttribute(k,v){this.attributes[k]=v;}
    click(){for(const fn of this.clicks)fn();}
  }
  const body=new El('BODY');body.classList={contains:()=>false};
  const document={body,activeElement:body,createElement:tag=>new El(tag.toUpperCase()),
    addEventListener:(type,fn)=>{(listeners[type]||=[]).push(fn);},removeEventListener(){}};
  const context={console:{debug(){},error(){}},document,Date:{now:()=>now}};
  context.window=context;vm.createContext(context);vm.runInContext(source,context);
  const panel=new El('SECTION');const child=(tag,type)=>{const el=new El(tag,type);el.parent=panel;return el;};
  const fire=(type,target)=>{for(const fn of listeners[type]||[])fn({target,isTrusted:true});};
  return {live:context.AutoSourcarrLive,document,panel,child,fire,advance:ms=>{now+=ms;},El};
}

test('UI-12: a focused select or checkbox holds a refresh only while it is being operated',()=>{
  const h=domHarness();const select=h.child('SELECT'),checkbox=h.child('INPUT','checkbox'),text=h.child('INPUT','text'),area=h.child('TEXTAREA');
  h.document.activeElement=text;assert.equal(h.live.userEditing(h.panel),true,'A focused text field holds');
  h.document.activeElement=area;assert.equal(h.live.userEditing(h.panel),true);
  h.document.activeElement=select;assert.equal(h.live.userEditing(h.panel),false,'A select that only kept focus after use does not hold');
  h.fire('pointerdown',select);assert.equal(h.live.userEditing(h.panel),true,'An open select holds');
  h.fire('change',select);h.advance(999);assert.equal(h.live.userEditing(h.panel),true,'Briefly after its change');
  h.advance(2);assert.equal(h.live.userEditing(h.panel),false,'then no longer, although it still has focus');
  h.fire('pointerdown',select);h.advance(10001);assert.equal(h.live.userEditing(h.panel),false,'An abandoned open state expires');
  h.document.activeElement=checkbox;h.fire('keydown',checkbox);assert.equal(h.live.userEditing(h.panel),true);
  h.fire('input',checkbox);h.advance(1001);assert.equal(h.live.userEditing(h.panel),false,'Checkboxes behave like selects');
  const outside=new h.El('INPUT','text');h.document.activeElement=outside;assert.equal(h.live.userEditing(h.panel),false,'Focus outside the panel does not hold it');
});

test('UI-12: the hint button appears after its anchor, refreshes on click and goes away',()=>{
  const h=domHarness();const anchor=new h.El('P');let runs=0;
  const hint=h.live.hintButton(()=>anchor);
  hint(true,()=>{runs++;});hint(true,()=>{runs++;});
  assert.equal(anchor.after.length,1,'One button, placed after the status line (never inside its rewritten text)');
  const button=anchor.after[0];assert.equal(button.tagName,'BUTTON');assert.match(button.textContent,/Updated .* refresh/);
  button.click();assert.equal(runs,1);assert.equal(anchor.after.length,0);
  hint(true,()=>{runs++;});assert.equal(anchor.after.length,1);hint(false);assert.equal(anchor.after.length,0);
});

test('UI-12: a held refresh shows the hint and a click on it refreshes at once',async()=>{
  const h=harness();h.live.start();h.last().open();const view=recorder();let shown=null;
  h.live.subscribe({name:'held',topics:['source-health'],refresh:view.refresh,busy:()=>true,hint:(on,run)=>{shown=on?run:null;}});
  h.last().send('source-health',1,{change:'results',source_ids:['a']});await h.clock.advance(5000);
  assert.equal(view.calls.length,0);assert.equal(typeof shown,'function','The hint is offered');
  await shown();assert.equal(view.calls.length,1,'Clicking it refreshes without waiting for the user to leave the control');
});

test('raw handlers: on(topic) gets each event at once and can be removed',async()=>{
  const h=harness();h.live.start();h.last().open();const seen=[];
  const off=h.live.on('jobs',data=>seen.push(data.id));h.last().send('jobs',1,{id:'a'});off();h.last().send('jobs',2,{id:'b'});
  assert.deepEqual(seen,['a']);
});

(async()=>{
  for(const {name,fn} of tests){await fn();console.log('PASS '+name);}
  console.log(`Live events UI: ${tests.length} scenarios passed`);
})().catch(error=>{console.error(error);process.exitCode=1;});
