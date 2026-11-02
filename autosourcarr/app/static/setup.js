/* Configuration stays a draft until Finish. Explicit account actions take effect immediately. */
(()=>{
  'use strict';
  const $=selector=>document.querySelector(selector);
  const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  // SET-1: everything required comes first and cannot be skipped; optional and opt-in features follow and can be skipped.
  const steps=[['welcome','Your AutoSourcarr','A guided review, one subject at a time.','required'],['library','Connect your library','Connect to the main Suwayomi for this instance.','required'],['sources','Sources and access','Choose where extensions come from and how access challenges are handled.','required'],['readers','Reader extensions','Use different addresses for readers inside Docker and outside your network.','required'],['access','Access and identity','Keep account credentials separate from reader access.','required'],['matching','Matching and discovery','Opt-in automation. Uncertain matches still need review.','optional'],['performance','Speed and capacity','Start with the current limits. Larger limits can overload websites and make requests slower.','optional'],['tools','Translation and tools','Use the included tools or connect an existing translation server.','optional'],['repair','Repair and AI','Choose a provider and connect its account here. API credentials are saved when you finish.','optional'],['links','Your other apps','Optional browser shortcuts. Each application keeps its own login.','optional'],['review','Review and finish','Only changed values will replace their saved settings.','review']];
  const optional=index=>steps[index]?.[3]==='optional';
  const stepIndex=name=>steps.findIndex(([key])=>key===name);
  // SET-2: the unsaved draft survives Cancel and a page reload on this device. Keys and passwords are never kept.
  const DRAFT_KEY='autosourcarr.setup.draft';
  let restoredDraft=null;
  let snapshot=null,draft={},step=0,required=false,busy=false,completed=false,resolveFirst=null,previousFocus=null,transaction=null,opening=false;
  // REP-6: automatic orphan repair is the source maintenance watch switch, not a config.json setting; null hides the question.
  let autoRepair=null,autoRepairSaved=null,autoRepairAsked=false;
  // Setup talks to /api/v1/setup through the generated client; api() keeps auth, timeouts and errors.
  const setupApi=()=>AutoSourcarrApi.createClient({request:(path,options)=>api(path,{timeoutMs:2500,...options,cache:'no-store',noMutationRefresh:true,suppressClientError:true})});
  let cleared=new Set(),accountGeneration=0,accountRevision=0,accountRead=0,accountTimer=null,loginOwned=false,loginOpened=null,accountError='',closing=false;
  let readerGeneration=0,readerChecking=false,readerProblem=false,readerResult=null,readerResultSaved=false;
  const repair=(path,options={})=>api('/api/v1/source-maintenance'+path,{timeoutMs:45000,...options,cache:'no-store',noMutationRefresh:true,suppressClientError:true});
  const rememberComplete=()=>{try{sessionStorage.setItem('autosourcarr.setup.complete','1');}catch(_){} };
  const requestId=()=>globalThis.crypto?.randomUUID?.()||'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g,c=>{const r=Math.random()*16|0;return(c==='x'?r:(r&3|8)).toString(16);});
  const clone=value=>JSON.parse(JSON.stringify(value));
  const changes=()=>Object.fromEntries((snapshot?.fields||[]).filter(f=>cleared.has(f.key)||JSON.stringify(draft[f.key])!==JSON.stringify(f.value)).map(f=>[f.key,cleared.has(f.key)?'':draft[f.key]]));
  const error=message=>{$('#setupError').textContent=message||'';};
  const sameValue=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
  const sameType=(a,b)=>Array.isArray(a)?Array.isArray(b)&&b.every(v=>typeof v==='string'):typeof a===typeof b||typeof a==='number'&&b==='';
  function clearDraft(){try{localStorage.removeItem(DRAFT_KEY);}catch(_){}}
  function storeDraft(){
    if(!snapshot||completed)return;
    const values={},removed=[];
    for(const field of snapshot.fields){
      if(field.kind==='secret'){if(cleared.has(field.key))removed.push(field.key);continue;}
      if(!sameValue(draft[field.key],field.value))values[field.key]={base:field.value,value:draft[field.key]};
    }
    try{
      if(!Object.keys(values).length&&!removed.length)localStorage.removeItem(DRAFT_KEY);
      else localStorage.setItem(DRAFT_KEY,JSON.stringify({version:1,step:steps[step][0],saved_at:new Date().toISOString(),values,removed}));
    }catch(_){}
  }
  // A kept value comes back only while its saved value is still the one it was edited from.
  function restoreDraft(data){
    let saved=null;try{saved=JSON.parse(localStorage.getItem(DRAFT_KEY)||'null');}catch(_){saved=null;}
    if(!saved||saved.version!==1||!saved.values||typeof saved.values!=='object')return null;
    let kept=0,dropped=0;
    for(const [key,entry] of Object.entries(saved.values)){
      const field=data.fields.find(f=>f.key===key);
      if(!field||field.kind==='secret'||!entry||typeof entry!=='object'||!sameValue(field.value,entry.base)||!sameType(field.value,entry.value)){dropped++;continue;}
      draft[key]=clone(entry.value);kept++;
    }
    for(const key of Array.isArray(saved.removed)?saved.removed:[]){
      const field=data.fields.find(f=>f.key===key);
      if(field?.kind==='secret'&&field.configured){cleared.add(key);draft[key]='';kept++;}else dropped++;
    }
    if(!kept&&!dropped)return null;
    if(!kept)clearDraft();
    return {kept,dropped,step:stepIndex(saved.step)};
  }
  function progress(){
    const group=steps[step][3];
    if(group==='review')return `Review · step ${step+1} of ${steps.length}`;
    const list=steps.filter(item=>item[3]===group);
    return `${group==='required'?'Required':'Optional · can be skipped'} · ${list.indexOf(steps[step])+1} of ${list.length} · step ${step+1} of ${steps.length}`;
  }
  function validReaderBuild(value){
    if(!value||typeof value.ready!=='boolean'||!Number.isInteger(value.exit_code)||!Array.isArray(value.errors)||!Array.isArray(value.warnings))return null;
    if([...value.errors,...value.warnings].some(item=>!item||typeof item.key!=='string'||!Number.isInteger(item.code)||typeof item.message!=='string'))return null;
    if(value.errors.some(item=>item.code<=0)||value.ready!==(value.errors.length===0)||value.exit_code!==(value.errors[0]?.code??0))return null;
    return value;
  }
  function readerPanel(check=true){
    return '<section class="setupNotice"><h3>Reader build readiness</h3><p>Reader builds are optional. You can finish setup and return here before building. The builder uses the running settings; saved changes may need an app restart.</p><p id="setupReaderBuildStatus" role="status" aria-live="polite"></p><ul id="setupReaderBuildIssues" hidden></ul>'+(check?'<button id="setupReaderCheck" type="button">Check draft reader settings</button>':'')+'</section>';
  }
  function updateReaderBuildStatus(){
    const status=$('#setupReaderBuildStatus'),issues=$('#setupReaderBuildIssues'),button=$('#setupReaderCheck');
    if(!status)return;
    if(button)button.disabled=busy||closing||readerChecking;
    if(issues){issues.replaceChildren();issues.hidden=true;}
    if(readerChecking){status.textContent='Checking draft reader settings…';return;}
    if(readerProblem){status.textContent='Reader build check unavailable. Use Check draft reader settings to try again.';return;}
    const result=validReaderBuild(readerResult);
    if(!result){status.textContent=completed?'Saved reader settings have not been checked. Open Status for build readiness.':'Reader settings have not been checked for this draft.';return;}
    const ready=result.ready&&result.errors.length===0;
    status.textContent=ready?(readerResultSaved?'Saved reader settings pass the build check.':'Draft reader settings pass the build check. Finish saves the draft.')
      :'Reader build needs attention. You can still finish setup.';
    if(issues){
      for(const [items,warning] of [[result.errors,false],[result.warnings,true]])for(const item of items){
        const row=document.createElement('li');row.textContent=(warning?'Warning · ':'')+(item.key?item.key+': ':'')+item.message;issues.append(row);
      }
      issues.hidden=!issues.children.length;
    }
  }
  function invalidateReaderPreflight(){
    readerGeneration++;readerChecking=false;readerProblem=false;readerResult=null;readerResultSaved=false;updateReaderBuildStatus();
  }
  async function checkReaderDraft(){
    if(busy||closing||completed||readerChecking||!snapshot||!['readers','review'].includes(steps[step][0]))return;
    capture();
    const owner=++readerGeneration,saved=snapshot,status=$('#setupReaderBuildStatus');
    if(!status)return;
    const current=()=>owner===readerGeneration&&snapshot===saved&&!completed&&!closing&&!busy&&!$('#setupModal').hidden&&$('#setupReaderBuildStatus')===status&&['readers','review'].includes(steps[step][0]);
    readerChecking=true;readerProblem=false;readerResult=null;readerResultSaved=false;updateReaderBuildStatus();
    try{
      const data=await setupApi().checkSetupReader({revision:saved.revision,changes:changes()},{timeoutMs:5000});
      if(!current())return;
      const result=validReaderBuild(data.reader_build);
      if(data.revision!==saved.revision||!result)throw new Error('Reader check returned no current result');
      readerResult=result;
    }catch(_error){if(current())readerProblem=true;}
    finally{if(current()){readerChecking=false;updateReaderBuildStatus();}}
  }
  function capture(){
    if(completed)return;
    const previous=JSON.stringify(changes());
    $('#setupBody').querySelectorAll('[data-setup-key]').forEach(input=>{
      const key=input.dataset.setupKey,field=snapshot.fields.find(f=>f.key===key);
      if(field.kind==='boolean')draft[key]=input.checked;
      else if(field.kind==='repositories')draft[key]=input.value.split('\n').map(v=>v.trim()).filter(Boolean);
      else if(field.kind==='integer'||field.kind==='number')draft[key]=input.value===''?'':Number(input.value);
      else draft[key]=input.value;
      if(field.kind==='secret'&&input.value)cleared.delete(key);
    });
    if(previous!==JSON.stringify(changes())){invalidateReaderPreflight();storeDraft();}
  }
  function fieldHtml(field){
    const value=draft[field.key],id='setup-'+field.key,desc=id+'-description',kind=field.kind;
    let input;
    const attrs=`id="${id}" data-setup-key="${field.key}" aria-describedby="${desc}"`;
    if(kind==='boolean') input=`<input ${attrs} type="checkbox" ${value?'checked':''}>`;
    else if(kind==='choice')input=`<select ${attrs}>${field.options.filter(([v])=>field.key!=='REPAIR_AI_PROVIDER'||snapshot.integrated||v==='chatgpt').map(([v,label])=>`<option value="${escape(v)}" ${v===value?'selected':''}>${escape(label)}</option>`).join('')}</select>`;
    else if(kind==='repositories')input=`<textarea ${attrs} rows="3" spellcheck="false">${escape(value.join('\n'))}</textarea>`;
    else if(kind==='translation'&&snapshot.integrated) input=`<select ${attrs}><option value="" ${!value?'selected':''}>Off</option><option value="http://127.0.0.1:5000" ${value==='http://127.0.0.1:5000'?'selected':''}>Built-in translation</option>${value&&value!=='http://127.0.0.1:5000'?`<option value="${escape(value)}" selected>Keep existing address</option>`:''}</select>`;
    else input=`<input ${attrs} type="${kind==='secret'?'password':kind==='integer'||kind==='number'?'number':'text'}" ${kind==='secret'?`autocomplete="new-password" placeholder="${field.configured?'Saved key retained unless replaced':'Optional: leave empty to keep disabled'}"`:''} ${field.minimum!==undefined?`min="${field.minimum}" max="${field.maximum}" step="${kind==='integer'?1:'any'}"`:''} value="${escape(value)}">`;
    return `<div class="setupField ${kind==='boolean'?'setupToggle':''}"><label for="${id}">${escape(field.label)}</label>${input}${field.generate?'<button type="button" id="setupGenerateKey">Generate replacement key</button>':''}${kind==='secret'&&field.configured?`<button type="button" id="setupClear-${field.key}">${cleared.has(field.key)?'Undo removal':'Remove saved credential'}</button>`:''}<p class="muted" id="${desc}">${escape(field.description)}${kind==='secret'?' Leave blank to retain the saved value; removal must be explicit.':''}</p></div>`;
  }
  function link(url,label){
    try{const parsed=new URL(url);if(!['http:','https:'].includes(parsed.protocol)||parsed.username||parsed.password||parsed.search||parsed.hash)return '';}
    catch(_){return '';}
    return `<a class="setupLink" href="${escape(url)}" target="_blank" rel="noopener noreferrer">${escape(label)} ↗</a>`;
  }
  function appLinks(values){return [['SUWAYOMI_BROWSER_URL','Open Suwayomi'],['KOMGA_BROWSER_URL','Open Komga'],['KOMF_BROWSER_URL','Open Komf'],['REPAIR_AI_BROWSER_URL','Open AI dashboard'],['TRANSLATION_BROWSER_URL','Open translation dashboard'],['AUTHELIA_BROWSER_URL','Open authentication portal']].map(([key,label])=>link(values[key],label)).join(' ');}
  const providerPresets={
    openai:['https://api.openai.com/v1','responses','https://platform.openai.com/api-keys'],
    claude:['https://api.anthropic.com/v1','anthropic','https://platform.claude.com'],
    gemini:['https://generativelanguage.googleapis.com/v1beta/openai','chat_completions','https://aistudio.google.com/apikey'],
    openrouter:['https://openrouter.ai/api/v1','chat_completions','https://openrouter.ai/settings/keys'],
    ollama:['','chat_completions','https://ollama.com']
  };
  const providerHelp={
    openai:['https://developers.openai.com/api/docs/guides/function-calling','OpenAI API setup'],
    claude:['https://platform.claude.com/docs/en/manage-claude/authentication','Claude API keys and workspaces'],
    gemini:['https://ai.google.dev/gemini-api/docs/openai','Gemini API setup'],
    openrouter:['https://openrouter.ai/docs/guides/routing/provider-selection','OpenRouter tool-capable routing'],
    ollama:['https://docs.ollama.com/faq','Ollama server and network setup']
  };
  const accountVisible=()=>!closing&&(completed||!!snapshot&&steps[step][0]==='repair'&&draft.REPAIR_AI_PROVIDER==='chatgpt');
  function accountPanel(){return '<section class="setupNotice"><h3>Repair tools and ChatGPT account</h3><p>Sign-in opens the official OpenAI page with a device code; return here when done. AutoSourcarr never asks for your ChatGPT password. Account changes take effect immediately, even if you later cancel setup.</p><p class="muted">If device sign-in is disabled, enable it in your ChatGPT security settings (or ask your workspace administrator).</p><p id="setupAccountStatus" role="status">Checking repair tools…</p><div id="setupAccountControls" class="toolbar wrap"></div><div id="setupAccountDiagnostics" aria-live="polite"></div><div id="setupDeviceCode" role="status"></div><div id="setupIsolation"></div></section>';}
  function render(){
    accountGeneration++;accountError='';loginOpened=null;clearTimeout(accountTimer);readerGeneration++;readerChecking=false;
    const [key,title,description]=steps[step];
    $('#setupTitle').textContent=title;$('#setupProgress').textContent=progress();
    $('#setupBack').hidden=step===0;$('#setupNext').textContent=key==='review'?'Finish setup':'Continue';$('#setupCancel').hidden=required;
    if($('#setupSkip'))$('#setupSkip').hidden=!optional(step);
    error('');$('#setupReload').hidden=true;
    let content=`<p class="muted">${escape(description)}</p>`;
    if(restoredDraft)content+=`<div class="setupNotice" id="setupDraftNotice"><strong>${restoredDraft.kept?'Unsaved setup draft restored':'Saved draft is out of date'}</strong><p>${restoredDraft.kept?`${restoredDraft.kept} unsaved ${restoredDraft.kept===1?'change was':'changes were'} restored from your last visit. Keys and passwords are never kept in a draft; enter them again if needed.`:''}${restoredDraft.dropped?` ${restoredDraft.dropped} ${restoredDraft.dropped===1?'change was':'changes were'} dropped because the saved setting changed in the meantime.`:''}</p>${restoredDraft.kept?'<button id="setupDiscardDraft" type="button">Discard draft</button>':''}</div>`;
    if(optional(step))content+=`<p class="setupNotice" id="setupOptionalNotice">${step===steps.findIndex(item=>item[3]==='optional')?'Required settings are done. The remaining steps are optional and opt-in features. ':''}Skip this step to keep its saved values, or go straight to the review. <button id="setupJumpReview" type="button">Go to review</button></p>`;
    if(key==='welcome') content+=`<div class="setupNotice"><strong>${snapshot.completed?'Review your current setup':'Welcome — let’s check your setup'}</strong><p>${snapshot.completed?'Your saved settings are prefilled. Cancel discards configuration edits. Completed account sign-in or sign-out remains in effect.':'Existing values are prefilled, including settings from older versions. Your library and source data stay in place.'}</p><p>Next and Back only edit a draft. Finish saves changes together; some changes need an app restart. Account actions are available in the Repair and AI step and take effect immediately. Closing cancels an unfinished sign-in.</p></div><details><summary>What will this guide cover?</summary><p>First the required settings: library connection, sources, reader access and app access. These steps cannot be skipped. Then the optional and opt-in features: matching automation and metadata keys, performance, translation, repair AI and app links. Each optional step can be skipped. Advanced options stay collapsed.</p><p>Cancel keeps your unsaved draft on this device (keys and passwords excepted); Run setup again continues from it.</p><p>Appearance and individual source logins have shortcuts at the end. Optional integrations can be skipped and revisited with Run setup again.</p></details>`;
    else if(key==='review'){
      const changed=changes();
      content+=`<div class="setupNotice"><strong>${Object.keys(changed).length} settings changed</strong><ul>${snapshot.fields.filter(f=>f.key in changed).sort((a,b)=>Number(optional(stepIndex(a.step)))-Number(optional(stepIndex(b.step)))).map(f=>`<li><strong>${escape(f.label)}</strong> <small class="muted">${optional(stepIndex(f.step))?'optional':'required'}</small>: ${f.kind==='secret'?(cleared.has(f.key)?'saved credential will be removed':'replacement credential supplied'):escape(Array.isArray(changed[f.key])?changed[f.key].join(', '):typeof changed[f.key]==='boolean'?(changed[f.key]?'On':'Off'):changed[f.key]||'Disabled / empty')}</li>`).join('')||'<li>Keep current settings and complete the guide.</li>'}</ul></div><details><summary>Cleanup after setup</summary><p>A short scan removes recognizable obsolete app-generated reports and bytecode. It keeps databases, source profiles, downloads, signing keys, repair backups and unknown files. Host folders outside the container’s mounts cannot be scanned.</p></details><p>Reader address or key changes require a new extension build and reinstall from Status. Existing connections continue with their old settings until restart.</p>${'ADMIN_TOKEN' in changed?'<p class="setupNotice">Keep a copy of the new admin token before finishing. After restart, use it when the app asks you to authenticate.</p>':''}`;
      if(autoRepair!==null&&autoRepair!==autoRepairSaved)content+=`<p class="setupNotice">Automatic repair of moved sources will be turned ${autoRepair?'on':'off'}.</p>`;
      content+=readerPanel();
    }else{
      const fields=snapshot.fields.filter(f=>f.step===key&&(key!=='repair'||(draft.REPAIR_AI_PROVIDER!=='chatgpt'&&(f.key!=='REPAIR_AI_PROTOCOL'||draft.REPAIR_AI_PROVIDER==='custom')&&(f.key!=='REPAIR_AI_WORKSPACE_ID'||draft.REPAIR_AI_PROVIDER==='claude'||draft.REPAIR_AI_PROVIDER==='custom'&&draft.REPAIR_AI_PROTOCOL==='anthropic'))||['REPAIR_AI_PROVIDER','REPAIR_AI_BROWSER_URL'].includes(f.key)));
      content+=fields.filter(f=>!f.advanced).map(fieldHtml).join('');
      if(fields.some(f=>f.advanced))content+=`<details class="setupAdvanced"><summary>Advanced options</summary>${fields.filter(f=>f.advanced).map(fieldHtml).join('')}</details>`;
      if(key==='readers')content+=readerPanel();
      if(key==='library')content+='<details><summary>Using a second instance?</summary><p>Use its own Suwayomi service address, configuration folder, database and source profile. Both AutoSourcarr containers listen internally on 8282; their host ports may differ.</p></details>';
      if(key==='sources')content+='<details><summary>Install sources and solve access challenges</summary><p>After setup, Sources → Discover lets you install extensions or drop in an APK, open source settings, and inspect orphaned sources. Assistance lists sites needing a human; use their WebView to continue.</p></details>';
      if(key==='library')content+=link(draft.SUWAYOMI_BROWSER_URL,'Open Suwayomi login');
      if(key==='sources')content+=link('https://keiyoushi.github.io/docs/guides/getting-started','Keiyoushi setup guide');
      if(key==='matching')content+=link('https://comicvine.gamespot.com/api/','Comic Vine account and API key');
      if(key==='tools')content+=link('https://libretranslate.com','LibreTranslate')+'<p class="muted">FlareSolverr, the reader builder and repair tools are included in the integrated image. Their internal addresses and tokens are managed automatically.</p>';
      // Asked only when the Repair step is first drawn; the step is drawn again once the answer is in.
      if(key==='repair'&&!autoRepairAsked){autoRepairAsked=true;const shown=step;api('/api/v1/source-maintenance',{timeoutMs:2500,cache:'no-store',noMutationRefresh:true,suppressClientError:true}).then(maintenance=>{if(autoRepairSaved!==null||maintenance?.service?.state!=='ready'||!snapshot)return;autoRepairSaved=!!maintenance.watch?.enabled;autoRepair=autoRepairSaved;if(step===shown&&!$('#setupModal').hidden)render();}).catch(()=>{});}
      if(key==='repair'&&autoRepair!==null)content+=`<div class="setupField setupToggle"><label for="setupAutoRepair">Repair moved sources automatically</label><input id="setupAutoRepair" type="checkbox" aria-describedby="setupAutoRepair-description" ${autoRepair?'checked':''}><p class="muted" id="setupAutoRepair-description">Once a day, installed sources that fail their check and whose website now redirects to a new address are moved to it and checked again; a repair that does not pass is rolled back. At most five sources a day, each at most once a week. Change it later in Sources → Repair.</p></div>`;
      if(key==='repair'){
        if(draft.REPAIR_AI_PROVIDER==='chatgpt') content+=accountPanel()+link('https://developers.openai.com/codex/auth/','Account sign-in help');
        else {
          content+='<div class="setupNotice"><strong>Connect this provider</strong><p>Open its account dashboard, create an API key if required, and enter it above with a model ID. Finish saves this connection; after restart, Test AI connection makes two small model requests to verify a harmless tool call and its result. Cloud requests use that provider’s quota and may be billed separately from a chat subscription.</p><p>API connection checks work while host repair isolation is blocked. Every provider still needs repair isolation before source code can run; compilation and functional checks follow each edit.</p>';
          if(draft.REPAIR_AI_PROVIDER==='ollama')content+='<p>Enter your existing Ollama server’s reachable address, for example http://MODEL-SERVER-IP:11434/v1 or its Docker service name on a shared network. Localhost means this container. TrueNAS/Linux does not automatically supply host.docker.internal. The model server must listen on an address AutoSourcarr can reach; local Ollama usually needs no API key.</p>';
          if(draft.REPAIR_AI_PROVIDER==='claude')content+='<p>A workspace-scoped API key can leave Workspace ID empty. Multi-workspace keys need the ID from Claude Console → Settings → Workspaces.</p>';
          content+='</div>';
          const help=providerHelp[draft.REPAIR_AI_PROVIDER];if(help)content+=link(...help);
        }
        content+=link(draft.REPAIR_AI_BROWSER_URL,'Open provider account / API keys');
      }
      if(key==='repair'&&!snapshot.integrated)content+='<p class="muted">This legacy external-worker installation uses the worker’s own ChatGPT account. Custom AI connections require the integrated AutoSourcarr image.</p>';
      if(key==='access')content+='<p class="muted">Source-site usernames, passwords and cookies stay in each installed source’s settings or WebView. Other apps keep their own login pages. This guide does not collect their passwords.</p>';
      if(key==='access')content+=link(draft.AUTHELIA_BROWSER_URL,'Open authentication portal');
      if(key==='tools')content+=link(draft.TRANSLATION_BROWSER_URL,'Open translation dashboard');
      if(key==='links')content+=`<div class="toolbar wrap">${appLinks(draft)}</div><p class="muted">Use browser-facing addresses, not container-only hostnames. Links open in another tab; AutoSourcarr does not sign in on your behalf.</p>`;
    }
    $('#setupBody').innerHTML=content;$('#setupBody').scrollTop=0;$('#setupTitle').focus();
    const rendered=readerGeneration;
    $('#setupBody').querySelectorAll('[data-setup-key]').forEach(input=>{
      for(const event of ['input','change'])input.addEventListener(event,()=>{
        if(busy||closing||completed||$('#setup-'+input.dataset.setupKey)!==input)return;
        capture();
      });
    });
    $('#setupReaderCheck')?.addEventListener('click',()=>checkReaderDraft());
    $('#setupDiscardDraft')?.addEventListener('click',()=>{if(!busy&&!closing&&!completed&&!loginOwned)discardDraft();});
    $('#setupJumpReview')?.addEventListener('click',()=>{if(busy||closing||completed||loginOwned)return;capture();if(invalidInput())return;restoredDraft=null;step=steps.length-1;render();});
    $('#setupAutoRepair')?.addEventListener('change',event=>{if(!busy&&!closing&&!completed)autoRepair=event.target.checked;});
    updateReaderBuildStatus();storeDraft();
    if(key==='review'&&rendered===readerGeneration)checkReaderDraft();
    $('#setup-REPAIR_AI_PROVIDER')?.addEventListener('change',()=>{
      if(busy||loginOwned)return;capture();const preset=providerPresets[draft.REPAIR_AI_PROVIDER];
      // Changing provider always clears the draft key and explicitly removes a
      // saved key. It must never be forwarded to a different account/server.
      draft.REPAIR_AI_API_KEY='';draft.REPAIR_AI_WORKSPACE_ID='';if(snapshot.fields.find(f=>f.key==='REPAIR_AI_API_KEY')?.configured)cleared.add('REPAIR_AI_API_KEY');
      if(preset){[draft.REPAIR_AI_BASE_URL,draft.REPAIR_AI_PROTOCOL,draft.REPAIR_AI_BROWSER_URL]=preset;draft.REPAIR_AI_MODEL='';}
      invalidateReaderPreflight();render();
    });
    $('#setup-REPAIR_AI_PROTOCOL')?.addEventListener('change',()=>{if(busy||closing)return;capture();render();});
    if(key==='repair'&&draft.REPAIR_AI_PROVIDER==='chatgpt')loadAccount(accountGeneration);
    for(const field of snapshot.fields.filter(f=>f.kind==='secret'))$('#setupClear-'+field.key)?.addEventListener('click',()=>{if(busy||closing)return;capture();if(cleared.has(field.key))cleared.delete(field.key);else{cleared.add(field.key);draft[field.key]='';}invalidateReaderPreflight();render();});
    $('#setupGenerateKey')?.addEventListener('click',()=>{
      if(busy||closing)return;capture();
      if(!globalThis.crypto?.getRandomValues){error('Secure key generation is unavailable in this browser. Enter a key manually.');return;}
      const bytes=globalThis.crypto.getRandomValues(new Uint8Array(32));const token=[...bytes].map(v=>v.toString(16).padStart(2,'0')).join('');
      $('#setup-AUTOSOURCARR_READER_API_TOKEN').value=token;draft.AUTOSOURCARR_READER_API_TOKEN=token;
      cleared.delete('AUTOSOURCARR_READER_API_TOKEN');invalidateReaderPreflight();
      $('#setupGenerateKey').textContent='New key prepared — saved only when you finish';
    });
  }
  async function close(){
    if(closing||busy)return false;storeDraft();closing=true;accountGeneration++;clearTimeout(accountTimer);invalidateReaderPreflight();
    if(loginOwned){const owned=loginOwned;loginOwned=false;try{await repair('/control/cancel-login',{method:'POST',body:JSON.stringify({operation_id:owned})});}catch(_){toast('Sign-in cancellation could not be confirmed. Check Source repairs before starting another login.');}}
    $('#setupModal').hidden=true;syncModalScrollLock();$('#setupBody').innerHTML='';error('');draft={};snapshot=null;transaction=null;completed=false;
    cleared.clear();autoRepair=autoRepairSaved=null;restoredDraft=null;closing=false;try{sessionStorage.removeItem('autosourcarr.setup.accounts');}catch(_){}
    if(resolveFirst){resolveFirst();resolveFirst=null;}previousFocus?.focus();return true;
  }
  function cancel(){
    if(busy||required&&!completed)return;
    close();
  }
  async function open(first=false,section=''){
    if(opening||closing||!$('#setupModal').hidden)return;
    if(!first&&typeof settingsState!=='undefined'&&(settingsState.dirty||settingsState.saving)){
      toast('Save or discard the current Application settings draft before running setup.');return;
    }
    opening=true;
    try{
      const data=await setupApi().getSetup();
      autoRepair=autoRepairSaved=null;autoRepairAsked=false;
      let resume=false;try{resume=sessionStorage.getItem('autosourcarr.setup.accounts')==='1';}catch(_){}
      if(first&&data.completed&&!resume){rememberComplete();return;}
      snapshot=data;required=first&&!data.completed;draft=Object.fromEntries(data.fields.map(f=>[f.key,clone(f.value)]));
      cleared.clear();readerGeneration++;readerChecking=false;readerProblem=false;readerResult=validReaderBuild(data.reader_build);readerResultSaved=true;
      completed=false;restoredDraft=first&&data.completed?null:restoreDraft(data);
      // Before the first Finish the required steps cannot be skipped, so a shortcut into a later step starts at the beginning.
      step=section&&data.completed?Math.max(0,stepIndex(section)):!section&&restoredDraft?.step>0?restoredDraft.step:0;transaction=requestId();
      previousFocus=document.activeElement;$('#setupModal').hidden=false;syncModalScrollLock();render();
      if(first&&data.completed&&resume){completed=true;showCompletion(data,draft);draft={};snapshot=null;checkCleanup(transaction,0);}
      if(required)await new Promise(resolve=>{resolveFirst=resolve;});
    }catch(e){
      // Leave the application usable for diagnostics/authentication; completion is never inferred from a failed request.
      toast('Setup could not load: '+e.message+'. Use Settings → Run setup again to retry.');
    }finally{opening=false;}
  }
  async function finish(){
    invalidateReaderPreflight();busy=true;$('#setupNext').disabled=true;$('#setupBack').disabled=true;$('#setupCancel').disabled=true;
    try{
      const result=await setupApi().finishSetup({revision:snapshot.revision,transaction,changes:changes()},{timeoutMs:15000});
      if(autoRepair!==null&&autoRepair!==autoRepairSaved){
        const enabled=autoRepair;
        try{await api('/api/v1/source-maintenance/watch',{method:'PUT',body:JSON.stringify({enabled}),timeoutMs:15000,noMutationRefresh:true,suppressClientError:true});autoRepairSaved=enabled;}
        catch(e){toast('Automatic repair of moved sources could not be turned '+(enabled?'on':'off')+': '+e.message+' Change it in Sources → Repair.');}
      }
      const links=Object.fromEntries(snapshot.fields.filter(f=>f.key.endsWith('_BROWSER_URL')).map(f=>[f.key,draft[f.key]]));
      completed=true;required=false;draft={};snapshot=null;restoredDraft=null;clearDraft();rememberComplete();$('#setupReload').hidden=true;
      if(typeof settingsState!=='undefined'){settingsState.data=null;settingsState.dirty=false;settingsState.editRevision++;}
      showCompletion(result,links);
      checkCleanup(transaction,0);
      $('#setupTitle').focus();
    }catch(e){error(e.message+' Your draft is retained. Retry Finish, or reload saved settings to start over.');$('#setupReload').hidden=false;}
    finally{busy=false;$('#setupNext').disabled=false;$('#setupBack').disabled=false;$('#setupCancel').disabled=false;updateReaderBuildStatus();}
  }
  function showCompletion(result,values){
    $('#setupTitle').textContent='Setup saved';$('#setupProgress').textContent='Connect accounts · optional';$('#setupBack').hidden=true;
    $('#setupNext').textContent='Open AutoSourcarr';$('#setupCancel').hidden=true;
    $('#setupBody').innerHTML=`<p>Your settings are saved. Account actions below take effect immediately and can be revisited in Sources → Repair AI.</p>${result.restart_required?`<div class="setupNotice"><strong>Restart to use the saved connections</strong><p>Restart this AutoSourcarr instance, then refresh this page to continue account setup.</p>${result.can_restart?'<button id="setupRestart" type="button">Restart AutoSourcarr now</button>':'<p>Use your container manager to restart this instance.</p>'}</div>`:''}<section class="setupNotice"><h3>Repair tools and AI account</h3><p id="setupAccountStatus" role="status">${result.restart_required?'Account checks become available after restart.':'Checking the selected connection…'}</p><div id="setupAccountControls" class="toolbar wrap"></div><div id="setupAccountDiagnostics" aria-live="polite"></div><div id="setupDeviceCode" role="status"></div><div id="setupIsolation"></div></section><div class="toolbar wrap">${appLinks(values)}</div><details><summary>Source logins and other setup</summary><p>Installed source settings expose the logins supported by that extension. WebView handles website sessions and challenges.</p><button id="setupSources" type="button">Open Sources</button><button id="setupRepairs" type="button">Open Repair AI</button><button id="setupAssistance" type="button">Open Assistance</button><button id="setupStatus" type="button">Open Status and reader builds</button><button id="setupCustomization" type="button">Open Settings</button><button id="setupImports" type="button">Open Imports</button></details>${readerPanel(false)}<p id="setupCleanup" role="status">Checking for obsolete app files…</p>`;

    readerGeneration++;readerChecking=false;readerProblem=false;readerResult=validReaderBuild(result.reader_build);readerResultSaved=true;updateReaderBuildStatus();
    $('#setupRestart')?.addEventListener('click',restart);
    for(const [id,tab] of [['setupSources','sources'],['setupRepairs','sources'],['setupAssistance','attention'],['setupStatus','about'],['setupCustomization','settings'],['setupImports','imports']])$('#'+id)?.addEventListener('click',async()=>{if(await close()){switchTab(tab);if(id==='setupRepairs')setSourceSubtab('maintenance');}});
    if(result.restart_required){try{sessionStorage.setItem('autosourcarr.setup.accounts','1');}catch(_){} }
    else{try{sessionStorage.removeItem('autosourcarr.setup.accounts');}catch(_){} loadAccount(++accountGeneration);}
  }
  async function loadAccount(own){
    if(own!==accountGeneration||!accountVisible())return;
    const revision=accountRevision,read=++accountRead,current=()=>read===accountRead&&own===accountGeneration&&revision===accountRevision&&accountVisible();
    try{
      const [provider,state]=await Promise.all([completed?repair('/provider'):Promise.resolve({provider:'chatgpt'}),repair('/control')]);
      if(!current())return;
      const custom=provider.provider&&provider.provider!=='chatgpt',locked=!!(state.busy||state.repair_busy||state.blocked);
      $('#setupAccountStatus').textContent=accountError||`${custom?provider.provider+' · '+provider.model:state.auth_saved?'ChatGPT login saved':'ChatGPT sign-in needed'} · ${state.message||'Prepare the repair tools first.'}`;
      AutoSourcarrRepairLogin.renderDiagnostics($('#setupAccountDiagnostics'),accountError||custom?null:state);
      const actions=custom?[['prepare','Check repair tools'],['test','Test AI connection']]:[['prepare','Check repair tools'],['login','Sign in with ChatGPT'],['login-status','Check saved login'],['logout','Sign out']];
      if(state.blocked)actions.push(['retry-cleanup','Retry account cleanup']);
      if(AutoSourcarrRepairLogin.running(state))actions.push(['cancel-login','Cancel sign-in']);
      $('#setupAccountControls').innerHTML=actions.map(([action,label])=>`<button id="setupAccount-${action}" type="button" ${locked&&!['cancel-login','retry-cleanup'].includes(action)?'disabled':''}>${label}</button>`).join('');
      for(const [action] of actions){
        if(action==='cancel-login')$('#setupAccount-'+action).disabled=!state.operation_id;
        $('#setupAccount-'+action)?.addEventListener('click',()=>accountAction(action,own,state.operation_id));
      }
      $('#setupDeviceCode').innerHTML='';
      if(AutoSourcarrRepairLogin.valid(state)){
        $('#setupDeviceCode').innerHTML=`<p>Enter this code on the official sign-in page: <strong>${escape(state.device_code)}</strong></p>${link(state.verification_url,'Open ChatGPT device sign-in')}<p class="muted">${escape(AutoSourcarrRepairLogin.expiry(state))} Return here after signing in.</p>`;
      }else if(AutoSourcarrRepairLogin.running(state)&&!state.device_code&&!state.verification_url){
        $('#setupDeviceCode').innerHTML=`${link(AutoSourcarrRepairLogin.url,'Open sign-in page')}<p>${escape(AutoSourcarrRepairLogin.hint(state.operation_id===loginOwned&&loginOpened))}</p>`;
      }
      if(!state.busy||loginOwned&&state.operation_id!==loginOwned)loginOwned=false;
      $('#setupNext').disabled=busy||loginOwned;$('#setupBack').disabled=busy||loginOwned;
      if($('#setup-REPAIR_AI_PROVIDER'))$('#setup-REPAIR_AI_PROVIDER').disabled=busy||loginOwned;
      if($('#setupIsolation')&&globalThis.renderRepairIsolation)renderRepairIsolation($('#setupIsolation'),state.isolation);
      clearTimeout(accountTimer);if(state.busy)accountTimer=setTimeout(()=>loadAccount(own),2000);
    }catch(e){if(current()){AutoSourcarrRepairLogin.renderDiagnostics($('#setupAccountDiagnostics'),null);$('#setupAccountStatus').textContent='Repair connection is not ready: '+e.message;$('#setupDeviceCode').innerHTML='';$('#setupAccountControls').innerHTML='<button id="setupAccountRetry" type="button">Retry connection check</button>';$('#setupAccountRetry')?.addEventListener('click',()=>{accountError='';loadAccount(own);});}}
  }
  document.addEventListener('visibilitychange',()=>{
    if(!document.hidden&&!busy&&accountVisible()){clearTimeout(accountTimer);loadAccount(accountGeneration);}
  });
  async function accountAction(action,own,operation){
    if(own!==accountGeneration||busy||closing)return;
    if(action==='logout'&&!confirm('Sign out of this instance’s ChatGPT repair account?'))return;
    if(action==='cancel-login'&&!operation)return;
    const attempt=action==='login'?requestId():null;
    const revision=++accountRevision,current=()=>own===accountGeneration&&revision===accountRevision&&accountVisible();accountError='';
    AutoSourcarrRepairLogin.renderDiagnostics($('#setupAccountDiagnostics'),null);
    if(attempt){loginOpened=AutoSourcarrRepairLogin.open();$('#setupDeviceCode').innerHTML=`${link(AutoSourcarrRepairLogin.url,'Open sign-in page')}<p>${escape(AutoSourcarrRepairLogin.hint(loginOpened))}</p>`;}
    else $('#setupDeviceCode').innerHTML='';
    $('#setupAccountStatus').textContent=attempt?'Starting ChatGPT sign-in…':action==='cancel-login'?'Cancelling sign-in…':'Starting '+action.replaceAll('-',' ')+'…';
    busy=true;$('#setupNext').disabled=true;$('#setupBack').disabled=true;if($('#setup-REPAIR_AI_PROVIDER'))$('#setup-REPAIR_AI_PROVIDER').disabled=true;clearTimeout(accountTimer);
    $('#setupAccountControls').querySelectorAll('button').forEach(button=>button.disabled=true);
    try{
      const result=await repair(action==='test'?'/provider/test':'/control/'+action,{method:'POST',...(attempt||action==='cancel-login'?{body:JSON.stringify({operation_id:attempt||operation})}:{})});
      if(!current())return;
      if(action==='login')loginOwned=result.operation_id===attempt?attempt:false;
      if(action==='cancel-login'||action==='logout')loginOwned=false;
      if(!current())return;
      // The accepted worker operation owns its lifetime. A slow read must not
      // hold the browser mutation lock or prevent foreground refresh/Cancel.
      busy=false;$('#setupNext').disabled=!!loginOwned;$('#setupBack').disabled=!!loginOwned;
      if($('#setup-REPAIR_AI_PROVIDER'))$('#setup-REPAIR_AI_PROVIDER').disabled=!!loginOwned;
      await loadAccount(own);
      if(current()&&action==='test')$('#setupAccountStatus').textContent=result.message;
    }catch(e){if(current()){accountError=e.message;$('#setupAccountStatus').textContent=accountError;if(action==='login'&&!(e.status_code>=400&&e.status_code<500)){try{await repair('/control/cancel-login',{method:'POST',body:JSON.stringify({operation_id:attempt})});}catch(_){if(current())loginOwned=attempt;}}if(current())await loadAccount(own);if(current())$('#setupAccountStatus').textContent=accountError;}}
    finally{if(current()){busy=false;$('#setupNext').disabled=!!loginOwned;$('#setupBack').disabled=!!loginOwned;if($('#setup-REPAIR_AI_PROVIDER'))$('#setup-REPAIR_AI_PROVIDER').disabled=!!loginOwned;}}
  }
  async function checkCleanup(id,attempt){
    try{
      const data=await setupApi().getSetup();if(transaction!==id||!completed)return;
      const result=data.cleanup;
      if(result&&result.status!=='pending'){
        $('#setupCleanup').textContent=result.status==='completed'?`Cleanup checked ${result.checked} files; removed ${result.removed.length} obsolete files.${result.skipped?' Some files could not be accessed.':''}${result.limited?' Scan reached its time or file limit.':''} Retained data and unknown files were kept.`:result?.message||'Setup saved; cleanup could not finish.';
        if(result.removed?.length){const detail=document.createElement('details');const summary=document.createElement('summary');summary.textContent='Removed files';const pre=document.createElement('pre');pre.textContent=result.removed.join('\n');detail.append(summary,pre);$('#setupCleanup').after(detail);}return;
      }
    }catch(e){/* Completion is already durable; cleanup status can be revisited without resubmitting. */}
    if(attempt<8)setTimeout(()=>checkCleanup(id,attempt+1),800);
    else if(transaction===id&&$('#setupCleanup'))$('#setupCleanup').textContent='Setup saved. Cleanup status is not available yet.';
  }
  async function restart(){
    const button=$('#setupRestart');button.disabled=true;
    try{sessionStorage.setItem('autosourcarr.setup.accounts','1');await setupApi().restartAfterSetup();button.textContent='Restart requested — refresh when ready to continue account setup';}
    catch(e){error(e.message);button.disabled=false;}
  }
  function invalidInput(){
    const invalid=[...$('#setupBody').querySelectorAll('input')].find(input=>!input.checkValidity());
    if(!invalid)return false;
    let parent=invalid.parentElement;while(parent&&parent!==$('#setupBody')){if(parent.tagName==='DETAILS')parent.open=true;parent=parent.parentElement;}
    error('Check the highlighted setting before continuing.');invalid.focus();invalid.reportValidity();return true;
  }
  function discardDraft(){
    clearDraft();restoredDraft=null;draft=Object.fromEntries(snapshot.fields.map(f=>[f.key,clone(f.value)]));cleared.clear();autoRepair=autoRepairSaved;
    invalidateReaderPreflight();render();
  }
  $('#setupBack').onclick=()=>{if(busy||loginOwned)return;capture();restoredDraft=null;step--;render();};
  $('#setupNext').onclick=()=>{
    if(busy||loginOwned)return;if(completed){close();return;}
    capture();if(invalidInput())return;
    restoredDraft=null;if(step===steps.length-1)finish();else{step++;render();}
  };
  // Skipping an optional step keeps that step's saved values; required steps have no Skip.
  if($('#setupSkip'))$('#setupSkip').onclick=()=>{
    if(busy||loginOwned||completed||!snapshot||!optional(step))return;
    const key=steps[step][0];
    for(const field of snapshot.fields.filter(f=>f.step===key)){draft[field.key]=clone(field.value);cleared.delete(field.key);}
    if(key==='repair')autoRepair=autoRepairSaved;
    invalidateReaderPreflight();restoredDraft=null;step++;render();
  };
  $('#setupReload').onclick=async()=>{
    if(busy||!confirm('Discard this setup draft and reload saved settings?'))return;
    busy=true;$('#setupReload').disabled=true;invalidateReaderPreflight();
    try{const data=await setupApi().getSetup();clearDraft();restoredDraft=null;snapshot=data;draft=Object.fromEntries(data.fields.map(f=>[f.key,clone(f.value)]));cleared.clear();readerResult=validReaderBuild(data.reader_build);readerResultSaved=true;step=0;transaction=requestId();render();}
    catch(e){error('Could not reload: '+e.message);}
    finally{busy=false;$('#setupReload').disabled=false;}
  };
  $('#setupCancel').onclick=cancel;$('#runSetup').onclick=()=>open(false);
  // UI-5: Escape and a click outside the dialog close it like Cancel (the draft is kept; first-run setup stays open).
  $('#setupModal').addEventListener('click',event=>{if(event.target===$('#setupModal'))cancel();});
  $('#setupModal').addEventListener('keydown',event=>{
    if(event.key==='Escape'){if(!event.defaultPrevented&&!event.target?.closest?.('dialog')){event.preventDefault();cancel();}return;}
    if(event.key!=='Tab')return;
    const controls=[...$('#setupModal').querySelectorAll('button,input,select,textarea,summary,[tabindex="0"]')].filter(el=>!el.disabled&&!el.hidden&&el.getClientRects().length);
    const first=controls[0],last=controls.at(-1);if(!first)return;
    if(event.shiftKey&&(document.activeElement===first||document.activeElement===$('#setupTitle'))){event.preventDefault();last.focus();}
    else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}
  });
  globalThis.AutoSourcarrSetup={initialize:()=>{let saved=false;try{saved=sessionStorage.getItem('autosourcarr.setup.complete')==='1';}catch(_){} const result=open(true);return saved?undefined:result;},open:()=>open(false),openReaders:()=>open(false,'readers'),openRepairAI:()=>open(false,'repair'),cancel};
})();
