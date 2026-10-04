export const $ = (selector, root = document) => root.querySelector(selector);
export const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export const state = { config: null, page: '', tasks: [], selected: new Set(), filter: 'all', search: '' };
export const bytes = value => { let n=Number(value)||0; const units=['B','KB','MB','GB','TB']; let i=0; while(n>=1024&&i<4){n/=1024;i++;} return (i?n.toFixed(1):n)+' '+units[i]; };
export const time = value => value ? new Date(value).toLocaleString('zh-CN', {month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'}) : '尚未检查';
export const statusName = value => ({active:'下载中',waiting:'等待中',paused:'已暂停',complete:'已完成',error:'出错',removed:'已移除'}[value] || value);
export const nameOf = task => task.bittorrent?.info?.name || task.files?.[0]?.path?.split(/[\\/]/).pop() || task.files?.[0]?.uris?.[0]?.uri || task.gid;
export async function api(path, method='GET', body) {
  const options = {method, headers:{'X-Aria2Fast':'1'}};
  if(body!==undefined){if(body instanceof FormData) options.body=body; else {options.headers['Content-Type']='application/json'; options.body=JSON.stringify(body);}}
  const response=await fetch('/api'+path,options);
  const text=await response.text(); let result; try{result=text?JSON.parse(text):null;}catch{throw new Error('服务返回了无法识别的内容');}
  if(!response.ok){if(response.status===401 && path!='/auth/login') window.dispatchEvent(new Event('auth-expired')); throw new Error(result?.error || (response.status===429?'操作太频繁，请稍后再试':'请求失败：'+response.status));}
  return result;
}
export function toast(message,error=false){const item=document.createElement('div');item.className='toast'+(error?' error':'');item.textContent=message;$('#toasts').append(item);while($('#toasts').children.length>2)$('#toasts').firstElementChild.remove();setTimeout(()=>item.remove(),error?9000:4500);}
export async function run(action,button){if(button)button.disabled=true;try{return await action();}catch(error){toast(error.message,true);}finally{if(button)button.disabled=false;}}
export function modal(title,html){$('#modal-title').textContent=title;$('#modal-body').innerHTML=html;if(!$('#modal').open)$('#modal').showModal();}
export const closeModal=()=>$('#modal').close();
export const heading=(title,subtitle,actions='')=>'<div class="page-heading"><div><div class="eyebrow">ARIA2FAST / WORKSPACE</div><h1>'+esc(title)+'</h1><p>'+esc(subtitle)+'</p></div><div class="toolbar">'+actions+'</div></div>';
export const empty=(title,description,symbol='↓')=>'<div class="empty"><div class="empty-symbol">'+symbol+'</div><h3>'+esc(title)+'</h3><p>'+esc(description)+'</p></div>';
export const field=(label,name,value='',type='text',extra='')=>'<label>'+esc(label)+'<input name="'+esc(name)+'" type="'+type+'" value="'+esc(value)+'" '+extra+'></label>';
export const check=(label,name,value=false)=>'<label class="check"><input name="'+name+'" type="checkbox" '+(value?'checked':'')+'>'+esc(label)+'</label>';
export const nodeOptions=(selected)=>state.config.nodes.map(n=>'<option value="'+esc(n.id)+'" '+(n.id===selected?'selected':'')+'>'+esc(n.name)+'</option>').join('');
export function formData(form){const out=Object.fromEntries(new FormData(form));form.querySelectorAll('input[type=checkbox][name]').forEach(x=>out[x.name]=x.checked);form.querySelectorAll('input[type=number][name]').forEach(x=>out[x.name]=Number(x.value));return out;}
export function bindForm(id,handler){const form=$(id);form.addEventListener('submit',e=>{e.preventDefault();run(()=>handler(formData(form),form),form.querySelector('[type=submit]'));});}
export async function refreshConfig(){state.config=await api('/state');$('#node-select').innerHTML=nodeOptions(state.config.selectedNodeId);return state.config;}
export function reportResults(results){if(!Array.isArray(results))return;const errors=results.filter(x=>x.error);if(errors.length)throw new Error(errors.map(x=>(x.url||x.gid)+': '+x.error).join('\n'));}
