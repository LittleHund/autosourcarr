// SRC-5 "Open in WebView": shows the source host's off-screen Chromium page and sends clicks, keys, scrolling and
// navigation back. Opened by openSourceWebview() (workbench.js) with #session=&ticket=&url= after
// POST /api/v1/source-webview/session; the one-time ticket is the WebSocket credential (offered as a subprotocol).
(function(){
  'use strict';
  const params=new URLSearchParams(location.hash.slice(1));
  const session=params.get('session')||'',ticket=params.get('ticket')||'';
  // Keep the ticket out of history and bookmarks.
  try{history.replaceState(null,'',location.pathname);}catch(_){}
  const canvas=document.querySelector('#webviewCanvas'),context=canvas.getContext('2d');
  const status=document.querySelector('#webviewStatus'),address=document.querySelector('#webviewUrl');
  let socket=null,ended=false,lastMove=0,lastWheel=0;
  address.value=params.get('url')||'';
  const say=text=>{status.textContent=text;};
  if(!/^[0-9a-f]{8,64}$/.test(session)||!/^[A-Za-z0-9_-]{16,128}$/.test(ticket)){say('Open WebView from AutoSourcarr (Sources, Assistance or an entry) to start a session.');return;}
  function send(message){if(socket&&socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify(message));}
  function point(event){
    const box=canvas.getBoundingClientRect();
    return {x:Math.round((event.clientX-box.left)*canvas.width/box.width),y:Math.round((event.clientY-box.top)*canvas.height/box.height)};
  }
  function finish(reason){if(ended)return;ended=true;say((reason||'The WebView session ended.')+' Cookies from this session were saved for the source.');}
  const scheme=location.protocol==='https:'?'wss:':'ws:';
  socket=new WebSocket(scheme+'//'+location.host+'/api/v1/source-webview/socket?session='+encodeURIComponent(session),['autosourcarr.webview','ticket.'+ticket]);
  socket.binaryType='blob';
  socket.addEventListener('open',()=>{say('Loading the page… Click the page to interact; type while it is focused.');canvas.focus();});
  socket.addEventListener('message',async event=>{
    if(typeof event.data!=='string'){
      try{
        const image=await createImageBitmap(event.data);
        if(canvas.width!==image.width||canvas.height!==image.height){canvas.width=image.width;canvas.height=image.height;}
        context.drawImage(image,0,0);image.close();
      }catch(_){/* a damaged frame is skipped; the next one replaces it */}
      return;
    }
    let data;try{data=JSON.parse(event.data);}catch(_){return;}
    if(data.type==='address'&&data.url&&document.activeElement!==address)address.value=data.url;
    else if(data.type==='title')document.title=(data.title?data.title+' · ':'')+'WebView · AutoSourcarr';
    else if(data.type==='load')say(data.error?'Could not load the page: '+data.error:'Loaded'+(data.status?' (HTTP '+data.status+')':'')+'. Complete any verification on the page, then press Done.');
    else if(data.type==='cookies'&&!ended)status.dataset.cookies=String(data.count||0);
    else if(data.type==='error')say(data.message||'That input was not accepted.');
    else if(data.type==='closed')finish(data.reason);
  });
  socket.addEventListener('close',()=>finish(''));
  socket.addEventListener('error',()=>say('The WebView connection failed. Close this tab and open WebView again.'));
  canvas.addEventListener('contextmenu',event=>event.preventDefault());
  canvas.addEventListener('mousedown',event=>{canvas.focus();event.preventDefault();send({type:'mousedown',button:event.button,...point(event)});});
  canvas.addEventListener('mouseup',event=>{event.preventDefault();send({type:'mouseup',button:event.button,...point(event)});});
  canvas.addEventListener('mousemove',event=>{const now=Date.now();if(now-lastMove<100)return;lastMove=now;send({type:'mousemove',...point(event)});});
  canvas.addEventListener('wheel',event=>{event.preventDefault();const now=Date.now();if(now-lastWheel<50)return;lastWheel=now;
    const scale=event.deltaMode===1?40:event.deltaMode===2?800:1;send({type:'wheel',deltaY:Math.round(event.deltaY*scale),...point(event)});},{passive:false});
  canvas.addEventListener('keydown',event=>{
    if(event.isComposing)return;
    // Ctrl/Cmd+V is left to the browser so the paste event below types the clipboard text.
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='v')return;
    event.preventDefault();
    send({type:'key',key:event.key,ctrl:event.ctrlKey,shift:event.shiftKey,meta:event.metaKey});
  });
  canvas.addEventListener('paste',event=>{const text=event.clipboardData?.getData('text')||'';if(text){event.preventDefault();send({type:'text',text:text.slice(0,2000)});}});
  document.querySelector('#webviewPaste').addEventListener('click',async()=>{
    try{const text=await navigator.clipboard.readText();if(text)send({type:'text',text:text.slice(0,2000)});canvas.focus();}
    catch(_){say('The browser did not allow reading the clipboard; click the page and press Ctrl+V instead.');}
  });
  document.querySelector('#webviewBack').addEventListener('click',()=>send({type:'back'}));
  document.querySelector('#webviewForward').addEventListener('click',()=>send({type:'forward'}));
  document.querySelector('#webviewReload').addEventListener('click',()=>send({type:'reload'}));
  document.querySelector('#webviewAddress').addEventListener('submit',event=>{
    event.preventDefault();
    let url=address.value.trim();if(url&&!/^https?:\/\//i.test(url))url='https://'+url;
    try{const parsed=new URL(url);if(!['http:','https:'].includes(parsed.protocol))throw new Error();send({type:'navigate',url:parsed.href});canvas.focus();}
    catch(_){say('Enter an http or https address.');}
  });
  document.querySelector('#webviewClose').addEventListener('click',()=>{send({type:'close'});setTimeout(()=>window.close(),300);});
})();
