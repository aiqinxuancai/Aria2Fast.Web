import {esc,state,api} from './core.js';

const key=id=>'aria2fast.directories.'+id;
function history(id){
  try{const items=JSON.parse(localStorage.getItem(key(id))||'[]');return Array.isArray(items)?items.filter(x=>typeof x==='string'&&x.trim()).slice(0,20):[];}catch{return[];}
}
export function rememberDirectory(id,value){
  if(!value?.trim())return;
  try{localStorage.setItem(key(id),JSON.stringify([value,...history(id).filter(x=>x!==value)].slice(0,20)));}catch{}
}
export function directoryField(label,value=''){
  return '<label>'+esc(label)+'<input name="directory" value="'+esc(value)+'" autocomplete="off"><small class="hint" data-directory-default></small><select data-directory-history aria-label="选择历史下载目录"><option value="">选择历史目录…</option></select></label>';
}
export function bindDirectory(form,subscription=false){
  const input=form.elements.directory;let root='';let version=0;
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
    const renderDefault=()=>{input.placeholder=root||'留空使用节点默认目录';form.querySelector('[data-directory-default]').textContent=root?'留空时使用：'+root:'正在获取节点默认目录…';update();};
    const select=form.querySelector('[data-directory-history]');
    select.innerHTML='<option value="">选择历史目录…</option>'+history(id).map(x=>'<option value="'+esc(x)+'">'+esc(x)+'</option>').join('');
    select.disabled=!history(id).length;
    renderDefault();
    if(!root)try{
      const result=await api('/nodes/'+encodeURIComponent(id)+'/directory');
      if(current!==version||!form.isConnected)return;
      root=result.directory;renderDefault();
      if(!root)form.querySelector('[data-directory-default]').textContent='节点未返回默认目录，可填写下载目录';
    }catch{
      if(current===version&&form.isConnected)form.querySelector('[data-directory-default]').textContent='暂时无法获取默认目录，可手动填写或稍后重试';
    }
  }
  form.querySelector('[data-directory-history]').onchange=e=>{if(e.target.value){input.value=e.target.value;update();}};
  form.elements.nodeId.addEventListener('change',changeNode);
  form.addEventListener('input',update);form.addEventListener('change',update);
  changeNode();
}

export function previewDialog(title,html){
  const dialog=document.createElement('dialog');dialog.id='feed-preview';
  dialog.setAttribute('aria-label',title);
  dialog.innerHTML='<div class="dialog-header"><h2>'+esc(title)+'</h2><button class="icon-button" aria-label="关闭预览">×</button></div><div class="preview-body">'+html+'<div class="form-actions"><button data-back>返回编辑</button></div></div>';
  document.body.append(dialog);
  dialog.querySelector('.icon-button').onclick=()=>dialog.close();
  dialog.querySelector('[data-back]').onclick=()=>dialog.close();
  dialog.addEventListener('close',()=>dialog.remove(),{once:true});
  dialog.showModal();
}
