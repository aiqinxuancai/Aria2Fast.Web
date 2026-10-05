import {directoryField,bindDirectory,rememberDirectory} from './directory-picker.js';
import {$,esc,state,api,bytes,nameOf,statusName,toast,run,modal,closeModal,heading,empty,field,check,bindForm,nodeOptions,reportResults} from './core.js';

export async function downloads(){
  $('#content').innerHTML=heading('下载任务','让每一份期待，有条不紊地抵达。','<button id="refresh-tasks">↻ 刷新</button>')+
    '<div class="stats"><div class="stat featured"><label>下载速度 <span>↙</span></label><strong id="stat-down">—</strong><small>当前节点实时速率</small></div><div class="stat"><label>上传速度 <span>↗</span></label><strong id="stat-up">—</strong><small>分享，也是一种连接</small></div><div class="stat"><label>进行中的任务 <span>↓</span></label><strong id="stat-active">—</strong><small id="stat-waiting">等待同步节点</small></div><div class="stat"><label>已完成任务 <span>✓</span></label><strong id="stat-complete">—</strong><small>本节点的已完成记录</small></div></div><div id="rpc-error"></div>'+
    '<section class="panel"><div class="panel-head"><div class="tabs" id="task-tabs">'+[['all','全部任务'],['active','下载中'],['waiting','等待'],['paused','暂停'],['complete','完成'],['error','失败']].map(([id,title])=>'<button data-filter="'+id+'" class="'+(state.filter===id?'active':'')+'">'+title+'</button>').join('')+'</div><input id="task-search" class="search" type="search" placeholder="搜索任务名称…" aria-label="搜索任务" value="'+esc(state.search)+'"></div><div class="panel-head"><div class="toolbar"><button data-bulk="resume">▶ 继续所选</button><button data-bulk="pause">Ⅱ 暂停所选</button><button data-bulk="remove" class="danger">移除所选</button></div><div class="toolbar"><button data-global="pauseAll">全部暂停</button><button data-global="resumeAll">全部继续</button><button id="global-options">下载设置</button><button data-global="purge">清理记录</button></div></div><div id="task-list"><div class="loading">连接 Aria2…</div></div></section><div class="callout">文件保存在下载节点所在设备。手机可以管理所有节点，也可以取回本地节点中已完成的文件。</div>';
  $('#refresh-tasks').onclick=()=>run(refreshTasks);
  $('#task-tabs').onclick=e=>{const button=e.target.closest('[data-filter]');if(button){state.filter=button.dataset.filter;$('#task-tabs').querySelectorAll('button').forEach(x=>x.classList.toggle('active',x===button));renderList();}};
  $('#task-search').oninput=e=>{state.search=e.target.value;renderList();};
  document.querySelectorAll('[data-bulk]').forEach(b=>b.onclick=()=>run(()=>action(b.dataset.bulk,[...state.selected]),b));
  document.querySelectorAll('[data-global]').forEach(b=>b.onclick=()=>run(()=>action(b.dataset.global,[]),b));
  $('#global-options').onclick=()=>run(()=>options());
  await refreshTasks();
}

export async function refreshTasks(){
  const node=state.config.selectedNodeId;
  try{
    const result=await api('/tasks?node='+encodeURIComponent(node));
    if(state.page!=='downloads'||state.config.selectedNodeId!==node||!$('#task-list'))return;
    state.tasks=[...result.active,...result.waiting,...result.stopped];
    $('#stat-down').textContent=bytes(result.stats.downloadSpeed)+'/s';$('#stat-up').textContent=bytes(result.stats.uploadSpeed)+'/s';
    $('#stat-active').textContent=result.stats.numActive;$('#stat-waiting').textContent=result.stats.numWaiting+' 个任务等待中';$('#stat-complete').textContent=result.stopped.filter(x=>x.status==='complete').length;
    $('#rpc-error').innerHTML='';$('#connection-dot').classList.add('online');$('#connection-label').textContent='节点已连接';renderList();
  }catch(error){if(state.page!=='downloads'||!$('#rpc-error'))return;$('#connection-dot').classList.remove('online');$('#connection-label').textContent='节点未连接';$('#rpc-error').innerHTML='<div class="callout error">'+esc(error.message)+'<br>请在设置中心检查节点地址、RPC 密钥或本地 aria2 执行文件。</div>';$('#task-list').innerHTML=empty('暂时无法连接下载节点','你的订阅与配置仍然保留。连接恢复后，任务会自动同步。','↔');}
}

function renderList(){
  const tasks=state.tasks.filter(x=>(state.filter==='all'||x.status===state.filter)&&nameOf(x).toLowerCase().includes(state.search.toLowerCase()));
  if(!tasks.length){$('#task-list').innerHTML=empty('这里还没有任务','添加一个下载链接、磁力链接或种子文件，即可开始。');return;}
  $('#task-list').innerHTML='<div class="table-scroll"><table><thead><tr><th><input id="select-all" type="checkbox" aria-label="选择所有可见任务"></th><th>文件名称</th><th>进度</th><th>速度</th><th>状态</th><th>操作</th></tr></thead><tbody>'+tasks.map(t=>{
    const percent=Number(t.totalLength)>0?Math.min(100,Number(t.completedLength)/Number(t.totalLength)*100):0;
    return '<tr><td><input type="checkbox" data-select="'+esc(t.gid)+'" '+(state.selected.has(t.gid)?'checked':'')+' aria-label="选择 '+esc(nameOf(t))+'"></td><td><button class="task-name" data-detail="'+esc(t.gid)+'" title="'+esc(nameOf(t))+'">'+esc(nameOf(t))+'</button><span class="file-meta">'+esc(t.gid)+' · '+bytes(t.totalLength)+'</span></td><td><span>'+percent.toFixed(1)+'%</span><div class="progress"><span style="width:'+percent+'%"></span></div><span class="file-meta">'+bytes(t.completedLength)+' / '+bytes(t.totalLength)+'</span></td><td class="nowrap">'+bytes(t.downloadSpeed)+'/s<br><span class="file-meta">↑ '+bytes(t.uploadSpeed)+'/s</span></td><td><span class="badge '+esc(t.status)+'">'+esc(statusName(t.status))+'</span></td><td><div class="row-actions">'+(['active','waiting','paused'].includes(t.status)?'<button data-action="'+(t.status==='paused'?'resume':'pause')+'" data-gid="'+esc(t.gid)+'">'+(t.status==='paused'?'继续':'暂停')+'</button>':'')+'<button class="danger" data-action="'+(['complete','error','removed'].includes(t.status)?'forget':'remove')+'" data-gid="'+esc(t.gid)+'">移除</button></div></td></tr>';
  }).join('')+'</tbody></table></div>';
  $('#select-all').checked=tasks.every(x=>state.selected.has(x.gid));$('#select-all').onchange=e=>{tasks.forEach(t=>e.target.checked?state.selected.add(t.gid):state.selected.delete(t.gid));renderList();};
  $('#task-list').querySelectorAll('[data-select]').forEach(x=>x.onchange=()=>x.checked?state.selected.add(x.dataset.select):state.selected.delete(x.dataset.select));
  $('#task-list').querySelectorAll('[data-detail]').forEach(x=>x.onclick=()=>run(()=>detail(x.dataset.detail)));
  $('#task-list').querySelectorAll('[data-action]').forEach(x=>x.onclick=()=>run(()=>action(x.dataset.action,[x.dataset.gid]),x));
}

async function action(action,gids){
  if(['pause','resume','remove','forget'].includes(action)&&!gids.length){toast('请先选择任务');return;}
  if(['remove','forget','purge'].includes(action)&&!confirm('确认移除任务或清理记录？已下载文件会保留。'))return;
  if(action==='remove'){
    const done=gids.filter(id=>['complete','error','removed'].includes(state.tasks.find(t=>t.gid===id)?.status));
    const live=gids.filter(id=>!done.includes(id));
    if(done.length)reportResults(await api('/tasks/action','POST',{action:'forget',gids:done,nodeId:state.config.selectedNodeId}));
    if(live.length)reportResults(await api('/tasks/action','POST',{action,gids:live,nodeId:state.config.selectedNodeId}));
  }else reportResults(await api('/tasks/action','POST',{action,gids,nodeId:state.config.selectedNodeId}));
  state.selected.clear();toast('操作已完成');await refreshTasks();
}

export function addTask(initial=''){
  modal('新建下载任务','<form id="add-form" class="stack"><label>下载链接 <span class="hint">每行一个，支持 HTTP / FTP / Magnet，最多 200 个</span><textarea name="urls" rows="6" placeholder="https://example.com/file.zip">'+esc(initial)+'</textarea></label><div class="form-grid"><label>下载节点<select name="nodeId">'+nodeOptions(state.config.selectedNodeId)+'</select></label>'+directoryField('保存目录（节点上的路径）')+'</div><label>或上传种子 / Metalink 文件<input name="file" type="file" accept=".torrent,.metalink,.meta4"></label><details><summary>高级参数</summary><div class="form-grid" style="margin-top:15px">'+field('下载限速（0 = 不限制）','limit','0')+field('连接数','connections',8,'number','min="1" max="16"')+field('Referer','referer')+field('输出文件名（单链接）','out')+'</div></details><div class="form-actions"><button type="submit" class="primary">开始下载 →</button></div></form>');
  bindDirectory($('#add-form'));
  bindForm('#add-form',async(data,form)=>{
    const file=form.elements.file.files[0];
    if(file){const body=new FormData();body.append('file',file);body.append('nodeId',data.nodeId);body.append('directory',data.directory);await api('/tasks/upload','POST',body);}
    else{const urls=data.urls.split(/\r?\n/).map(x=>x.trim()).filter(Boolean);if(!urls.length)throw new Error('请填写下载链接或选择种子文件');const opts={'max-download-limit':data.limit,'max-connection-per-server':String(data.connections)};if(data.referer)opts.referer=data.referer;if(data.out&&urls.length===1)opts.out=data.out;const results=await api('/tasks','POST',{urls,directory:data.directory||null,nodeId:data.nodeId,options:opts});const failed=results.filter(x=>x.error);if(failed.length){form.elements.urls.value=failed.map(x=>x.url).join('\n');reportResults(failed);}}
    rememberDirectory(data.nodeId,data.directory);closeModal();toast('任务已添加');if(state.page==='downloads')await refreshTasks();
  });
}

async function detail(gid){
  const node=state.config.selectedNodeId;const task=await api('/tasks/'+gid+'?node='+encodeURIComponent(node));
  modal('任务详情','<h3>'+esc(nameOf(task))+'</h3><div class="detail-metrics"><div>'+bytes(task.totalLength)+'<small>总大小</small></div><div>'+bytes(task.downloadSpeed)+'/s<small>下载速度</small></div><div>'+esc(task.connections||0)+'<small>连接数</small></div></div><div class="callout">状态：'+esc(statusName(task.status))+'<br>目录：'+esc(task.dir)+'<br>GID：'+esc(gid)+(task.errorMessage?'<br>错误：'+esc(task.errorMessage):'')+'</div><h3>任务文件</h3><div class="file-list">'+(task.files||[]).map((f,i)=>'<div class="file-row"><input data-file="'+esc(f.index)+'" type="checkbox" '+(f.selected==='true'?'checked':'')+' aria-label="选择文件"><span>'+esc(f.path)+'<br><small class="muted">'+bytes(f.length)+'</small></span>'+(node==='local'&&task.status==='complete'?'<a href="/api/files/'+gid+'/'+i+'">取回文件</a>':'')+'</div>').join('')+'</div><div class="form-actions"><button id="task-options">参数</button><button id="task-peers">Peers</button><button id="task-top">队列置顶</button><button id="select-files">保存文件选择</button>'+(node==='local'&&task.status==='complete'?'<button id="rename-preview">AI 整理</button>':'')+'</div><div id="detail-extra"></div>');
  $('#task-options').onclick=()=>run(()=>options(gid));$('#task-top').onclick=()=>run(async()=>{await api('/tasks/'+gid+'/position','POST',{nodeId:node,position:0});toast('已移至队列顶部');});
  $('#task-peers').onclick=()=>run(async()=>{const peers=await api('/tasks/'+gid+'/peers?node='+encodeURIComponent(node));$('#detail-extra').innerHTML='<pre class="pre">'+esc(JSON.stringify(peers,null,2))+'</pre>';});
  $('#select-files').onclick=()=>run(async()=>{const selection=[...document.querySelectorAll('[data-file]:checked')].map(x=>x.dataset.file).join(',');if(!selection)throw new Error('至少选择一个文件');await api('/options','POST',{nodeId:node,gid,options:{'select-file':selection}});toast('文件选择已保存');});
  if($('#rename-preview'))$('#rename-preview').onclick=()=>run(async()=>{const changes=await api('/files/'+gid+'/rename/preview','POST');modal('确认 AI 整理方案','<p class="hint">仅整理已完成的本地文件；先核对新名称。整理后 Aria2 原任务记录中的路径不会更新。</p><div class="stack">'+changes.map((x,i)=>'<label>'+esc(x.old)+'<input data-rename="'+i+'" value="'+esc(x.new)+'"></label>').join('')+'</div><div class="form-actions"><button id="apply-rename" class="primary">确认重命名</button></div>');$('#apply-rename').onclick=()=>run(async()=>{const edited=changes.map((x,i)=>({...x,new:$('[data-rename="'+i+'"]').value}));await api('/files/'+gid+'/rename','POST',edited);closeModal();toast('整理完成');});},$('#rename-preview'));
}

async function options(gid=null){
  const node=state.config.selectedNodeId;const config=await api('/options?node='+encodeURIComponent(node)+(gid?'&gid='+gid:''));
  const fields=gid?[['max-download-limit','下载限速'],['max-upload-limit','上传限速'],['split','分片数'],['seed-ratio','分享率']]:[['max-download-limit','下载限速'],['max-upload-limit','上传限速'],['max-concurrent-downloads','同时下载数'],['seed-ratio','分享率']];
  modal(gid?'任务参数':'全局下载参数','<form id="options-form"><p class="hint">限速支持 K / M，0 表示不限速。参数即时应用到当前节点。</p><div class="form-grid">'+fields.map(([key,label])=>field(label,key,config[key]||'0')).join('')+'<label class="full">BT Trackers（逗号分隔）<textarea name="bt-tracker" rows="4">'+esc(config['bt-tracker']||'')+'</textarea></label></div><div class="form-actions"><button type="submit" class="primary">保存参数</button></div></form>');
  bindForm('#options-form',async(data)=>{await api('/options','POST',{nodeId:node,gid,options:data});closeModal();toast('参数已生效');});
}
