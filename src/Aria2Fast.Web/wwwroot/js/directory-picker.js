import {esc,state,api} from './core.js';

const key=id=>'aria2fast.directories.'+id;
function history(id){
  try{const items=JSON.parse(localStorage.getItem(key(id))||'[]');return Array.isArray(items)?items.filter(x=>typeof x==='string'&&x.trim()).slice(0,20):[];}catch{return[];}
}
export function rememberDirectory(id,value){
  if(!value?.trim())return;
  try{localStorage.setItem(key(id),JSON.stringify([value,...history(id).filter(x=>x!==value)].slice(0,20)));}catch{}
}
let directoryListId=0;
export function directoryField(label,value=''){
  const id='directory-history-'+(++directoryListId);
  return '<label>'+esc(label)+'<input name="directory" value="'+esc(value)+'" list="'+id+'" autocomplete="off"><datalist id="'+id+'" data-directory-history></datalist></label>';
}
export function bindDirectory(form,subscription=false){
  const input=form.elements.directory;let root='';let version=0;let initial=true;
  const output=subscription?form.querySelector('[data-final-directory]'):null;
  function update(){
    if(!output)return;
    const base=input.value.trim()?input.value:root;
    let value=base||'节点默认目录（暂未获取）';
    const segment=s=>s.trim().replace(/[\x00-\x1f<>:"/\\|?*]/g,'_').replace(/^[. ]+|[. ]+$/g,'').slice(0,160);
    const parts=[form.elements.namePath.value.trim()?segment(form.elements.namePath.value):'',form.elements.autoDir.checked?'{AI 识别的作品名}':'',Number(form.elements.season.value)>0?'Season '+Number(form.elements.season.value):''].filter(Boolean);
    for(const part of parts)value=value.replace(/[\\/]+$/,'')+'/'+part;
    output.textContent=value;
    form.querySelector('[data-ai-path-note]').hidden=!form.elements.autoDir.checked;
  }
  async function changeNode(){
    const current=++version;const id=form.elements.nodeId.value;
    const node=state.config.nodes.find(x=>x.id===id);
    root=id==='local'?state.config.settings.downloadDirectory:node?.downloadDirectory||'';
    const items=history(id);
    if(!initial||!input.value.trim())input.value=items[0]||'';
    initial=false;
    const renderDefault=()=>{input.placeholder=root||'路径';update();};
    form.querySelector('[data-directory-history]').innerHTML=items.map(x=>'<option value="'+esc(x)+'"></option>').join('');
    renderDefault();
    if(!root)try{
      const result=await api('/nodes/'+encodeURIComponent(id)+'/directory');
      if(current!==version||!form.isConnected)return;
      root=result.directory;renderDefault();
    }catch{}
  }
  form.elements.nodeId.addEventListener('change',changeNode);
  form.addEventListener('input',update);form.addEventListener('change',update);
  changeNode();
}

export function previewDialog(title,html){
  const dialog=document.createElement('dialog');dialog.id='feed-preview';
  dialog.setAttribute('aria-label',title);
  dialog.innerHTML='<div class="dialog-header"><h2>'+esc(title)+'</h2><button class="icon-button" aria-label="关闭预览"><svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" focusable="false"><path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg></button></div><div class="preview-body">'+html+'<div class="form-actions"><button data-back>返回编辑</button></div></div>';
  document.body.append(dialog);
  dialog.querySelector('.icon-button').onclick=()=>dialog.close();
  dialog.querySelector('[data-back]').onclick=()=>dialog.close();
  dialog.addEventListener('close',()=>dialog.remove(),{once:true});
  dialog.showModal();
}
