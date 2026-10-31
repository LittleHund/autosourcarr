/* Tabs hide live panels; they never recreate forms or save user input. */
(()=>{
  'use strict';
  function mount(body,{onSelect=()=>{}}={}){
    const title=body.querySelector('h3'),content=[...body.childNodes].filter(node=>node!==title);
    const root=document.createElement('section'),list=document.createElement('div');list.className='riManageTabs';list.setAttribute('role','tablist');list.setAttribute('aria-label','Manage work');root.append(list);
    const panels={},buttons={},keys=['overview','serving','metadata','ids'];
    for(const key of keys){
      const button=document.createElement('button');button.type='button';button.id='riManageTab-'+key;button.textContent={overview:'Overview',serving:'Serving',metadata:'Metadata',ids:'IDs'}[key];button.setAttribute('role','tab');button.setAttribute('aria-controls','riManagePanel-'+key);
      const pane=document.createElement('section');pane.id='riManagePanel-'+key;pane.className='riManagePane';pane.setAttribute('role','tabpanel');pane.setAttribute('aria-labelledby',button.id);pane.tabIndex=0;panels[key]=pane;buttons[key]=button;list.append(button);root.append(pane);
      button.onclick=()=>select(key);
    }
    for(const node of content){
      const section=node.id==='riProfileForm'||node.id==='riRetainedProfiles'?'serving':node.classList?.contains('riMediaIdentity')?'ids':'overview';panels[section].append(node);
    }
    body.append(root);
    function select(key){
      if(!keys.includes(key))return;
      for(const name of keys){const active=name===key;panels[name].hidden=!active;buttons[name].tabIndex=active?0:-1;buttons[name].setAttribute('aria-selected',String(active));}
      root.dataset.selected=key;
      onSelect(key);
    }
    list.addEventListener('keydown',event=>{
      const key=keys.find(k=>buttons[k]===event.target);if(!key)return;
      let index=keys.indexOf(key);
      if(event.key==='ArrowRight')index=(index+1)%keys.length;else if(event.key==='ArrowLeft')index=(index+keys.length-1)%keys.length;else if(event.key==='Home')index=0;else if(event.key==='End')index=keys.length-1;else return;
      event.preventDefault();buttons[keys[index]].focus();
    });
    select('overview');return {panels,select,selected:()=>root.dataset.selected};
  }
  window.AutoSourcarrManageTabs={mount};
})();
