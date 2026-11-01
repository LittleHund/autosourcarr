// Phase 10 reader setup (READ-5) and installed-source downloads (READ-2).
// System -> Reader setup: per server slot, a QR code + link that adds AutoSourcarr's extension repository to Mihon
// (mihon://extension-store?url=..., tachiyomi://add-repo?url=... for older forks) and a QR code with the slot's
// address and reader key to paste into "AutoSourcarr (n)" settings (readers have no link that fills source settings).
// The key never leaves this page: the server does not store or return it; it only goes into the drawn QR code.
(()=>{
  'use strict';
  const byId=id=>document.getElementById(id);
  const state={setup:null,loading:null};
  function qrSvg(text){
    if(typeof window.qrcode!=='function')throw new Error('QR encoder missing');
    window.qrcode.stringToBytes=window.qrcode.stringToBytesFuncs['UTF-8'];
    const code=window.qrcode(0,'M');code.addData(text);code.make();
    return code.createSvgTag({cellSize:4,margin:4,scalable:true});
  }
  function element(tag,props={},children=[]){
    const node=document.createElement(tag);
    for(const [key,value] of Object.entries(props)){if(key==='text')node.textContent=value;else node.setAttribute(key,value);}
    for(const child of children)node.append(child);
    return node;
  }
  function codeCard(title,text,link,linkLabel,note){
    const figure=element('figure',{class:'readerSetupCode'});
    const art=element('div',{class:'readerSetupQr',role:'img','aria-label':title});
    try{art.innerHTML=qrSvg(text);}catch(error){art.textContent='QR code unavailable: '+error.message;}
    figure.append(art,element('figcaption',{},[element('strong',{text:title})]));
    if(link)figure.append(element('a',{href:link,class:'readerSetupLink',text:linkLabel}));
    if(note)figure.append(element('p',{class:'muted',text:note}));
    const copy=element('button',{type:'button',text:'Copy text'});
    copy.onclick=async()=>{try{await navigator.clipboard.writeText(text);copy.textContent='Copied';}catch(_error){copy.textContent='Copy failed';}setTimeout(()=>{copy.textContent='Copy text';},1500);};
    figure.append(element('pre',{class:'readerSetupText',text}),copy);
    return figure;
  }
  function render(){
    const setup=state.setup,codes=byId('readerSetupCodes'),status=byId('readerSetupStatus'),select=byId('readerSetupSlot');
    if(!setup||!codes)return;
    const slots=setup.slots||[];
    if(select.options.length!==slots.length){
      select.replaceChildren(...slots.map(slot=>element('option',{value:String(slot.slot),text:`Server ${slot.slot} (${slot.source_name})`})));
    }
    const slot=slots.find(row=>String(row.slot)===select.value)||slots[0];
    const extension=setup.extension||{};
    status.textContent=[extension.available?`Reader extension ${extension.version_name||''}, ${extension.slots} server slots${extension.signed_for_release?'':' (UNSIGNED-FOR-RELEASE build)'}`:'',...(setup.warnings||[])].filter(Boolean).join(' · ')||'Ready.';
    codes.replaceChildren();
    if(!setup.repository_url||!slot)return;
    codes.append(codeCard('1. Add the AutoSourcarr repository',setup.mihon_link,setup.mihon_link,'Open in Mihon',
      'Scan with the phone camera or tap the link on the phone. Older Tachiyomi forks: '));
    const legacy=codes.lastChild.querySelector('p');
    if(legacy)legacy.append(element('a',{href:setup.tachiyomi_link,text:'tachiyomi://add-repo'}),document.createTextNode('. Then install AutoSourcarr from Browse → Extensions.'));
    const key=byId('readerSetupKey').value.trim();
    codes.append(codeCard(`2. ${slot.source_name} settings`,slot.settings_text+(key||'<paste a reader device key>'),'','',
      key?`Open Browse → Extensions → AutoSourcarr → ${slot.source_name} settings and paste the address and key.`:'Create a reader device key under Keys (one per phone) and paste it above to include it in this code.'));
  }
  async function load(){
    if(state.loading)return state.loading;
    const status=byId('readerSetupStatus');status.textContent='Loading reader setup…';
    state.loading=(async()=>{
      try{state.setup=await api('/api/v1/reader-extension/setup',{timeoutMs:10000,suppressClientError:true,noMutationRefresh:true});render();}
      catch(error){status.textContent='Reader setup could not be loaded: '+error.message;}
      finally{state.loading=null;}
    })();
    return state.loading;
  }
  // READ-2: APK and JAR downloads for installed sources (Sources -> Installed); resolved by the server at request time.
  function escapeAttribute(value){return String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c]);}
  window.readerExtensionDownloads=ext=>{
    if(!ext||!ext.isInstalled||!/^[A-Za-z0-9._]{1,200}$/.test(ext.pkgName||''))return '';
    const pkg=escapeAttribute(ext.pkgName);
    return `<button type="button" class="sourceExtensionDownload" data-package="${pkg}" data-kind="apk" onclick="downloadSourceExtension(this)">APK</button><button type="button" class="sourceExtensionDownload" data-package="${pkg}" data-kind="jar" onclick="downloadSourceExtension(this)">JAR</button>`;
  };
  window.downloadSourceExtension=async button=>{
    const pkg=button.dataset.package,kind=button.dataset.kind;if(button.disabled||!pkg||!['apk','jar'].includes(kind))return;
    button.disabled=true;const label=button.textContent;button.textContent='…';
    try{
      const blob=await api(`/api/v1/source-extensions/${encodeURIComponent(pkg)}/${kind}`,{responseType:'blob',cache:'no-store',timeoutMs:120000,noMutationRefresh:true});
      if(!blob||!blob.size)throw new Error('No file returned');
      downloadBlob(blob,`${pkg}.${kind}`);
    }catch(error){button.title=error.message;button.textContent='Failed';setTimeout(()=>{button.textContent=label;},2500);return;}
    finally{button.disabled=false;}
    button.textContent=label;
  };
  const STYLE='.readerSetupCodes{display:flex;flex-wrap:wrap;gap:16px;margin-top:12px}.readerSetupCode{margin:0;max-width:320px}'
    +'.readerSetupQr svg{width:220px;height:220px;background:#fff;border-radius:6px}.readerSetupText{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}'
    +'.readerSetupLink{display:inline-block;margin:6px 0;font-weight:600}.sourceExtensionDownload{min-width:3.5em}';
  document.addEventListener('DOMContentLoaded',()=>{
    const panel=byId('readerSetupPanel');if(!panel)return;
    document.head.append(element('style',{text:STYLE}));
    panel.addEventListener('toggle',()=>{if(panel.open)load();});
    byId('readerSetupSlot').addEventListener('change',render);
    byId('readerSetupKey').addEventListener('input',render);
    byId('readerSetupRefresh').addEventListener('click',()=>{state.setup=null;load();});
  });
  window.loadReaderSetup=load;
})();
