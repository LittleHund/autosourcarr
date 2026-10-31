/* Typed destructive confirmations keep the phrase outside the scrollable preview. */
(()=>{
  let active=false;
  function request({title,rows=[],details=[],phrase,acceptLabel='Confirm'}){
    if(active)throw new Error('Another confirmation is already open');
    if(typeof phrase!=='string'||!phrase||phrase.length>128)throw new Error('A confirmation phrase is required');
    const dialog=document.createElement('dialog');
    if(typeof dialog.showModal!=='function')throw new Error('This browser cannot show the confirmation. Update the browser and retry.');
    active=true;
    const previous=document.activeElement;
    const element=(tag,text,className)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=String(text);if(className)node.className=className;return node;};
    dialog.className='confirmationDialog';dialog.setAttribute('aria-labelledby','confirmationTitle');
    const layout=element('div',undefined,'confirmationLayout');
    const header=element('header',undefined,'confirmationHeader'),heading=element('h2',title||'Confirm action');
    heading.id='confirmationTitle';header.append(heading);
    const preview=element('div',undefined,'confirmationPreview');
    const counts=element('ul');for(const row of rows)counts.append(element('li',row));preview.append(counts);
    for(const detail of details)if(detail)preview.append(element('p',detail));
    const footer=element('div',undefined,'confirmationFooter');
    footer.append(element('p','Type this phrase exactly to continue:'));
    const required=element('code',phrase,'confirmationRequired');required.id='confirmationPhrase';footer.append(required);
    const label=element('label','Confirmation phrase');label.htmlFor='confirmationInput';
    const input=element('input');input.id='confirmationInput';input.type='text';input.autocomplete='off';input.spellcheck=false;
    input.setAttribute('aria-describedby','confirmationPhrase');
    const actions=element('div',undefined,'confirmationActions');
    const cancel=element('button','Cancel');cancel.type='button';
    const accept=element('button',acceptLabel,'dangerAction');accept.type='button';accept.id='confirmationAccept';accept.disabled=true;
    actions.append(cancel,accept);footer.append(label,input,actions);layout.append(header,preview,footer);dialog.append(layout);
    return new Promise((resolve,reject)=>{
      const cleanup=()=>{dialog.remove();active=false;if(previous?.isConnected&&!previous.disabled)previous.focus?.();};
      const confirm=()=>{if(input.value===phrase)dialog.close('confirmed');};
      input.addEventListener('input',()=>{accept.disabled=input.value!==phrase;});
      input.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();confirm();}else if(event.key==='Escape')event.stopPropagation?.();});
      accept.addEventListener('click',confirm);cancel.addEventListener('click',()=>dialog.close('cancelled'));
      dialog.addEventListener('cancel',event=>{event.preventDefault();event.stopPropagation?.();dialog.close('cancelled');});
      dialog.addEventListener('close',()=>{const value=dialog.returnValue==='confirmed'&&input.value===phrase?phrase:null;cleanup();resolve(value);});
      try{document.body.append(dialog);dialog.showModal();input.focus();}catch(error){cleanup();reject(error);}
    });
  }
  window.AutoSourcarrConfirm={request};
})();
