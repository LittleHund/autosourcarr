/* UI-13 / PERF-8 live updates: one server-sent event stream (/api/v1/events) per browser tab.
 *
 * Pages do not poll. They subscribe to topics ("tasks", "jobs", "errors", "library", "connections", ...) and get one
 * coalesced refresh call per burst, only while their view is visible (UI-12: an update never moves the page, steals
 * focus or replaces a form being edited; a view that is busy waits, optionally behind a small "Updated" hint).
 *
 * Transport: native EventSource when no admin token header is needed (accounts / reverse-proxy sign-in send
 * cookies); fetch streaming when the token-only mode needs the X-Admin-Token header. Reconnects back off and resume
 * with the last event id; a "resync" event (the server no longer holds the gap) reloads the visible views only. While
 * the tab is hidden nothing is refreshed; after a short grace the stream is closed and on return it resumes from the
 * last id, so everything missed arrives as one catch-up refresh per view. Only when neither transport exists, or the
 * stream keeps failing, a slow poll tick stands in (and the stream is still retried now and then).
 *
 * Public: AutoSourcarrLive.subscribe({name,topics,refresh,when,filter,busy,hint,delayMs}) -> {unsubscribe,refreshNow},
 * on(topic,handler) -> off, start(), stop(), configure({getToken}), status(), userEditing(container), create(env).
 */
(function(root){
  'use strict';
  const KNOWN_TOPICS=['tasks','jobs','errors','library','connections','catalogue','orphans','sources','duplicates','imports','repairs','source-health'];
  const DEFAULTS={coalesceMs:250,backoffMinMs:1000,backoffMaxMs:30000,failuresBeforeFallback:4,fallbackPollMs:30000,
    fallbackRetryMs:120000,hiddenCloseMs:20000,busyRecheckMs:1000,url:'/api/v1/events'};

  function create(env={}){
    const win=env.window||root;
    const doc=env.document||win.document||null;
    const setT=env.setTimeout||((fn,ms)=>win.setTimeout(fn,ms));
    const clearT=env.clearTimeout||(id=>win.clearTimeout(id));
    const log=env.console||win.console||{debug(){},error(){}};
    const opts={...DEFAULTS,...(env.options||{})};
    let getToken=env.getToken||(()=>{try{return win.localStorage?.getItem('sc_admin_token')||'';}catch(_){return '';}});
    const subs=new Set(),raw=new Map(),listened=new Set();
    const state={started:false,mode:'idle',transport:null,connected:false,lastId:null,failures:0,serverRetryMs:0,
      everOpened:false,paused:false,source:null,controller:null,reconnectTimer:null,hiddenTimer:null,pollTimer:null,generation:0};
    const hidden=()=>!!doc?.hidden;

    // ---- delivery -------------------------------------------------------------------------------------------------
    function emitRaw(topic,event){
      for(const handler of [...(raw.get(topic)||[]),...(raw.get('*')||[])]){
        try{handler(event.data,topic,event);}catch(error){log.error?.('Live update handler failed',error);}
      }
    }
    function wants(sub,event){
      if(event.kind!=='event')return true;
      if(!sub.topics.has(event.topic)&&!sub.topics.has('*'))return false;
      try{return !sub.filter||sub.filter(event)!==false;}catch(_){return false;}
    }
    function deliver(sub,event){
      if(!sub.active||!wants(sub,event))return;
      const pending=sub.pending||(sub.pending={events:[],resync:false,poll:false,visible:false});
      if(event.kind==='event'){if(pending.events.length<200)pending.events.push(event);}
      else pending[event.kind]=true;
      schedule(sub,event.kind==='event'?sub.delayMs:0);
    }
    function schedule(sub,delay){
      if(sub.timer!==null||!sub.active)return;
      // While the tab is hidden nothing runs; visibility return flushes once.
      if(hidden())return;
      sub.timer=setT(()=>{sub.timer=null;flush(sub);},Math.max(0,delay));
    }
    function visibleNow(sub){try{return sub.when();}catch(_){return false;}}
    function flush(sub){
      if(!sub.active||!sub.pending)return;
      if(hidden())return;
      // A view that is not on screen loads itself when it is opened again: drop what it missed.
      if(!visibleNow(sub)){sub.pending=null;showHint(sub,false);return;}
      let busy=false;try{busy=!!sub.busy?.();}catch(_){busy=false;}
      if(busy){
        showHint(sub,true);
        if(sub.timer===null)sub.timer=setT(()=>{sub.timer=null;flush(sub);},opts.busyRecheckMs);
        return;
      }
      if(sub.running){sub.again=true;return;}
      const batch=sub.pending;sub.pending=null;showHint(sub,false);
      sub.running=Promise.resolve().then(()=>sub.refresh(batch)).catch(error=>log.debug?.('Live refresh failed: '+(sub.name||'view'),error?.message||error))
        .finally(()=>{sub.running=null;if(sub.again||sub.pending){sub.again=false;if(sub.pending)schedule(sub,sub.delayMs);}});
    }
    function showHint(sub,on){
      if(!sub.hint||sub.hintShown===on)return;
      sub.hintShown=on;
      try{sub.hint(on,()=>{sub.hintShown=false;sub.hint(false);const batch=sub.pending||{events:[],resync:false,poll:false,visible:false};sub.pending=null;clearTimer(sub);return sub.refresh(batch);});}catch(_){}
    }
    function clearTimer(sub){if(sub.timer!==null){clearT(sub.timer);sub.timer=null;}}
    function broadcast(kind){for(const sub of subs)deliver(sub,{kind});}
    function dispatch(topic,payload,id){
      if(id!=null&&/^\d+$/.test(String(id)))state.lastId=String(id);
      const body=payload&&typeof payload==='object'&&'topic' in payload&&'data' in payload?payload:{id:id==null?null:Number(id),topic,data:payload};
      const event={kind:'event',topic,id:body.id??(id==null?null:Number(id)),at:body.at||null,data:body.data??null};
      if(topic==='resync'){emitRaw('resync',event);broadcast('resync');return;}
      emitRaw(topic,event);
      for(const sub of subs)deliver(sub,event);
    }

    // ---- transports -----------------------------------------------------------------------------------------------
    const EventSourceCtor=()=>env.EventSource!==undefined?env.EventSource:win.EventSource;
    const fetchFn=()=>env.fetch!==undefined?env.fetch:(typeof win.fetch==='function'?win.fetch.bind(win):null);
    const canStream=()=>!!fetchFn()&&(env.ReadableStream!==undefined?!!env.ReadableStream:typeof win.ReadableStream==='function')&&typeof (env.TextDecoder||win.TextDecoder)==='function';
    function chooseTransport(){
      const token=String(getToken()||'');
      if(token)return canStream()?'fetch':null; // EventSource cannot send the header; never put a token in a URL.
      if(typeof EventSourceCtor()==='function')return 'eventsource';
      return canStream()?'fetch':null;
    }
    function connect(){
      clearT(state.reconnectTimer);state.reconnectTimer=null;
      if(!state.started||state.paused||state.source||state.controller)return;
      const transport=chooseTransport();state.transport=transport;
      if(!transport){enterFallback(false);return;}
      const generation=++state.generation;
      if(transport==='eventsource')openEventSource(generation);else openFetch(generation);
    }
    function opened(generation){
      if(generation!==state.generation)return;
      // A reconnect, or a first connect after failed attempts, may have missed events.
      const reconnect=state.everOpened||state.failures>0;
      state.connected=true;state.everOpened=true;state.failures=0;
      if(state.mode==='fallback')leaveFallback();
      state.mode='live';
      emitRaw('connected',{kind:'event',topic:'connected',id:null,data:{reconnect}});
      // Without an id the gap cannot be replayed: reload what is visible once.
      if(reconnect&&state.lastId===null)broadcast('resync');
    }
    function dropped(generation){
      if(generation!==state.generation)return;
      closeTransport();
      const wasConnected=state.connected;state.connected=false;
      if(!state.started||state.paused)return;
      // A stream that opened and then ended (server restart, proxy timeout) is not a failure streak by itself.
      state.failures=wasConnected?1:state.failures+1;
      if(state.failures>=opts.failuresBeforeFallback&&state.mode!=='fallback')enterFallback(true);
      const base=Math.max(opts.backoffMinMs,state.serverRetryMs||0);
      const delay=state.mode==='fallback'?opts.fallbackRetryMs:Math.min(opts.backoffMaxMs,base*2**Math.max(0,state.failures-1));
      state.reconnectTimer=setT(connect,delay);
    }
    function listen(topic){
      if(!state.source||listened.has(topic))return;
      listened.add(topic);
      const generation=state.generation;
      state.source.addEventListener(topic,message=>{
        if(generation!==state.generation)return;
        let payload=null;try{payload=message.data?JSON.parse(message.data):null;}catch(_){payload=null;}
        dispatch(topic,payload,message.lastEventId||null);
      });
    }
    function openEventSource(generation){
      const Ctor=EventSourceCtor();
      const url=opts.url+(state.lastId?'?last_event_id='+encodeURIComponent(state.lastId):'');
      let source;try{source=new Ctor(url);}catch(_){state.failures=opts.failuresBeforeFallback;dropped(generation);return;}
      state.source=source;listened.clear();
      for(const topic of topicsToListen())listen(topic);
      source.onopen=()=>opened(generation);
      // The browser would reconnect by itself without our back-off or fallback; take over instead.
      source.onerror=()=>dropped(generation);
    }
    function topicsToListen(){
      const all=new Set([...KNOWN_TOPICS,'resync','message']);
      for(const sub of subs)for(const topic of sub.topics)if(topic!=='*')all.add(topic);
      for(const topic of raw.keys())if(!['*','connected'].includes(topic))all.add(topic);
      return all;
    }
    async function openFetch(generation){
      const controller=new (env.AbortController||win.AbortController)();state.controller=controller;
      try{
        const headers={Accept:'text/event-stream'};
        const token=String(getToken()||'');if(token)headers['X-Admin-Token']=token;
        if(state.lastId)headers['Last-Event-ID']=state.lastId;
        const response=await fetchFn()(opts.url,{headers,cache:'no-store',signal:controller.signal,credentials:'same-origin'});
        if(generation!==state.generation)return;
        if(!response.ok||!response.body)throw new Error('HTTP '+response.status);
        opened(generation);
        const reader=response.body.getReader();const decoder=new (env.TextDecoder||win.TextDecoder)();let buffer='';
        for(;;){
          const {value,done}=await reader.read();if(done||generation!==state.generation)break;
          buffer+=decoder.decode(value,{stream:true}).replace(/\r\n?/g,'\n');
          let cut;
          while((cut=buffer.indexOf('\n\n'))>=0){
            const block=buffer.slice(0,cut);buffer=buffer.slice(cut+2);
            const item=parseBlock(block);if(!item)continue;
            let payload=null;try{payload=item.data?JSON.parse(item.data):null;}catch(_){payload=null;}
            dispatch(item.event,payload,item.id);
          }
        }
      }catch(error){if(error?.name!=='AbortError')log.debug?.('Live updates disconnected',error?.message||error);}
      dropped(generation);
    }
    function parseBlock(block){
      let id=null,event='message';const data=[];
      for(const line of block.split('\n')){
        if(!line)continue;
        if(line.startsWith(':')){const known=/last id (\d+)/.exec(line);if(known&&state.lastId===null&&known[1]!=='0')state.lastId=known[1];continue;}
        const colon=line.indexOf(':');const field=colon<0?line:line.slice(0,colon);let value=colon<0?'':line.slice(colon+1);if(value.startsWith(' '))value=value.slice(1);
        if(field==='id')id=value;else if(field==='event')event=value;else if(field==='data')data.push(value);
        else if(field==='retry'&&/^\d+$/.test(value))state.serverRetryMs=Number(value);
      }
      if(!data.length&&id===null)return null;
      return {id,event,data:data.join('\n')};
    }
    function closeTransport(){
      if(state.source){try{state.source.onerror=null;state.source.onopen=null;state.source.close();}catch(_){}state.source=null;}
      if(state.controller){try{state.controller.abort();}catch(_){}state.controller=null;}
      listened.clear();
    }

    // ---- fallback: a slow tick, only when live updates are impossible or keep failing --------------------------------
    function enterFallback(retry){
      if(state.mode!=='fallback'){state.mode='fallback';emitRaw('fallback',{kind:'event',topic:'fallback',id:null,data:{retry}});}
      if(state.pollTimer===null)tick();
      if(!retry){clearT(state.reconnectTimer);state.reconnectTimer=null;}
    }
    function tick(){
      state.pollTimer=setT(()=>{state.pollTimer=null;if(state.mode!=='fallback'||!state.started)return;if(!hidden())broadcast('poll');tick();},opts.fallbackPollMs);
    }
    function leaveFallback(){if(state.pollTimer!==null){clearT(state.pollTimer);state.pollTimer=null;}}

    // ---- tab visibility -------------------------------------------------------------------------------------------
    function onVisibility(){
      if(!state.started)return;
      if(hidden()){
        clearT(state.hiddenTimer);
        state.hiddenTimer=setT(()=>{state.hiddenTimer=null;if(!hidden())return;state.paused=true;state.generation++;closeTransport();state.connected=false;clearT(state.reconnectTimer);state.reconnectTimer=null;},opts.hiddenCloseMs);
        return;
      }
      clearT(state.hiddenTimer);state.hiddenTimer=null;
      if(state.paused){state.paused=false;connect();}
      if(state.mode==='fallback')broadcast('poll');
      for(const sub of subs)if(sub.pending)schedule(sub,0);
    }

    // ---- public API -----------------------------------------------------------------------------------------------
    function subscribe(config){
      const topics=new Set([].concat(config.topics||[]).map(String));
      const sub={name:String(config.name||''),topics,refresh:config.refresh,when:config.when||(()=>true),filter:config.filter||null,
        busy:config.busy||null,hint:config.hint||null,delayMs:Math.max(0,Number(config.delayMs??opts.coalesceMs)),
        pending:null,timer:null,running:null,again:false,active:true,hintShown:false};
      if(typeof sub.refresh!=='function')throw new TypeError('subscribe needs a refresh function');
      subs.add(sub);
      for(const topic of topics)if(topic!=='*')listen(topic);
      return {
        unsubscribe(){sub.active=false;clearTimer(sub);sub.pending=null;subs.delete(sub);},
        refreshNow(){deliver(sub,{kind:'resync'});},
      };
    }
    function on(topic,handler){
      topic=String(topic);
      if(!raw.has(topic))raw.set(topic,[]);
      raw.get(topic).push(handler);
      if(!['*','connected','fallback'].includes(topic))listen(topic);
      return ()=>{const list=raw.get(topic)||[];const at=list.indexOf(handler);if(at>=0)list.splice(at,1);};
    }
    function start(){
      if(state.started)return;
      state.started=true;
      doc?.addEventListener?.('visibilitychange',onVisibility);
      if(hidden())onVisibility();
      connect();
    }
    function stop(){
      state.started=false;state.generation++;closeTransport();state.connected=false;state.mode='idle';
      for(const timer of ['reconnectTimer','hiddenTimer','pollTimer'])if(state[timer]!==null){clearT(state[timer]);state[timer]=null;}
      doc?.removeEventListener?.('visibilitychange',onVisibility);
    }
    function configure(config={}){if(typeof config.getToken==='function')getToken=config.getToken;}
    function status(){return {started:state.started,mode:state.mode,transport:state.transport,connected:state.connected,lastId:state.lastId,failures:state.failures,paused:state.paused,subscriptions:subs.size};}
    return {subscribe,on,start,stop,configure,status,userEditing,
      // Test hook: what the server would deliver; never needed by pages.
      _dispatch:dispatch};
  }

  /** UI-12: true while the user is typing in, or holding a control of, the given container (or a modal is open). */
  function userEditing(container){
    const doc=root.document;if(!doc)return false;
    if(doc.body?.classList?.contains('modalOpen'))return true;
    const active=doc.activeElement;
    if(!active||active===doc.body||!container?.contains?.(active))return false;
    return active.isContentEditable||['INPUT','TEXTAREA','SELECT'].includes(active.tagName);
  }

  const shared=root.document?create():null;
  root.AutoSourcarrLive=shared?{...shared,create}:{create,userEditing};
  if(typeof module==='object'&&module.exports)module.exports={create,userEditing};
})(typeof window!=='undefined'?window:globalThis);
