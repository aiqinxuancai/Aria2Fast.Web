import {directoryField,bindDirectory,rememberDirectory,previewDialog} from './directory-picker.js';
import {$,esc,state,api,toast,run,modal,closeModal,heading,empty,field,check,bindForm,nodeOptions,time} from './core.js';
let list=[];
let removeMenuListeners=()=>{};
export async function subscriptions(){
  removeMenuListeners();
  list=await api('/subscriptions');if(state.page!=='subscriptions')return;
  $('#content').innerHTML=heading('我的订阅','更新自动发现，喜欢的剧集不再错过。','<button id="check-subs">↻ 立即检查</button><button id="new-sub" class="primary">＋ 添加订阅</button>')+'<div class="callout">每 '+state.config.settings.subscriptionIntervalMinutes+' 分钟自动检查。新订阅默认下载源中已有的匹配资源；重新下载会忽略订阅历史，再次提交源中全部匹配资源。</div><div class="card-grid">'+list.map(s=>'<article class="sub-card"><div class="sub-top"><div class="sub-icon">◉</div><span class="badge '+(s.lastError?'error':s.enabled?'':'paused')+'">'+(s.lastError?'检查失败':s.enabled?'自动追更':'已暂停')+'</span></div><h3>'+esc(s.name)+'</h3><p>'+esc(state.config.nodes.find(n=>n.id===s.nodeId)?.name||s.nodeId)+' · '+s.history.filter(h=>!h.skipped).length+' 次提交<br>最近检查：'+time(s.lastChecked)+'<br>过滤：'+esc(s.filter||'全部内容')+'</p>'+(s.lastError?'<div class="error-text hint">'+esc(s.lastError)+'</div>':'')+'<div class="toolbar sub-actions"><button data-edit="'+esc(s.id)+'">编辑</button><details class="sub-more"><summary>更多</summary><div class="sub-menu"><button data-history="'+esc(s.id)+'">查看历史</button><button data-check="'+esc(s.id)+'">立即检查</button><button data-redownload="'+esc(s.id)+'">重新下载全部</button><button data-toggle="'+esc(s.id)+'">'+(s.enabled?'暂停订阅':'启用订阅')+'</button><button data-delete="'+esc(s.id)+'" class="danger">删除订阅</button></div></details></div></article>').join('')+'</div>'+(!list.length?'<div class="panel">'+empty('开始你的第一份订阅','添加 RSS 地址，或前往「发现番剧」选择喜欢的作品。','◉')+'</div>':'');
  $('#new-sub').onclick=()=>editSubscription();$('#check-subs').onclick=e=>run(async()=>{showCheck(await api('/subscriptions/check','POST',{}));await subscriptions();},e.target);
  $('#content').querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>editSubscription(list.find(x=>x.id===b.dataset.edit)));
  $('#content').querySelectorAll('[data-check]').forEach(b=>b.onclick=()=>run(async()=>{showCheck(await api('/subscriptions/check','POST',{id:b.dataset.check}));await subscriptions();},b));
  $('#content').querySelectorAll('[data-redownload]').forEach(b=>b.onclick=()=>run(async()=>{
    if(!confirm('重新提交此订阅源当前全部匹配资源？将忽略首次跳过和已提交历史，仍遵守包含/排除条件。可能产生重复任务；不会删除或强制覆盖现有文件。'))return;
    showCheck(await api('/subscriptions/'+encodeURIComponent(b.dataset.redownload)+'/redownload','POST'));
    await subscriptions();
  },b));
  $('#content').querySelectorAll('[data-toggle]').forEach(b=>b.onclick=()=>run(async()=>{const s=list.find(x=>x.id===b.dataset.toggle);await api('/subscriptions','POST',{...s,enabled:!s.enabled});await subscriptions();},b));
  $('#content').querySelectorAll('[data-delete]').forEach(b=>b.onclick=()=>run(async()=>{if(!confirm('删除此订阅？已下载任务和文件不会被删除。'))return;await api('/subscriptions/'+b.dataset.delete,'DELETE');await subscriptions();},b));
  $('#content').querySelectorAll('[data-history]').forEach(b=>b.onclick=()=>{const s=list.find(x=>x.id===b.dataset.history);modal(s.name+' · 订阅历史',s.history.length?s.history.slice().reverse().map(h=>'<div class="file-row"><span>'+esc(h.title)+'<br><small class="muted">'+time(h.time)+' · '+(h.skipped?'首次检查跳过（未下载）':'已提交下载')+'</small></span></div>').join(''):empty('暂无记录','等待下一次订阅检查。','◉'));});
  const closeMenus=event=>{
    if(event.type==='keydown'&&event.key!=='Escape')return;
    const current=event.target.closest('.sub-more');
    document.querySelectorAll('.sub-more[open]').forEach(menu=>{
      if(event.type==='click'&&menu===current&&!event.target.closest('.sub-menu button'))return;
      menu.open=false;
      if(event.type==='keydown')menu.querySelector('summary').focus();
    });
  };
  document.addEventListener('click',closeMenus);document.addEventListener('keydown',closeMenus);
  removeMenuListeners=()=>{document.removeEventListener('click',closeMenus);document.removeEventListener('keydown',closeMenus);};
}

export function editSubscription(source={}){
  const s={name:'',url:'',nodeId:state.config.selectedNodeId,directory:'',namePath:'',season:0,filter:'',excludeFilter:'',isFilterRegex:false,autoDir:false,enabled:true,skipExisting:false,...source};
  modal(source.id?'编辑订阅':'添加订阅','<form id="sub-form"><div class="form-grid">'+field('订阅名称','name',s.name,'text','required')+'<label>下载节点<select name="nodeId">'+nodeOptions(s.nodeId)+'</select></label><div class="full">'+field('RSS / Atom 地址','url',s.url,'url','required')+'</div>'+directoryField('下载基础目录',s.directory)+field('作品目录名','namePath',s.namePath)+field('季度（0 = 不分季）','season',s.season,'number','min="0" max="999"')+field('包含关键词（多个用 | 分隔）','filter',s.filter)+field('排除关键词','excludeFilter',s.excludeFilter)+'<div class="stack">'+check('使用正则表达式', 'isFilterRegex',s.isFilterRegex)+check('AI 按作品分目录', 'autoDir',s.autoDir)+check('启用自动检查','enabled',s.enabled)+(!source.id?check('仅追更：首次检查跳过已有资源，不下载','skipExisting',s.skipExisting):'')+'</div><div class="full callout directory-summary"><strong>最终完整下载目录</strong><div data-final-directory></div><small class="hint" data-ai-path-note hidden>AI 作品名将在每条资源下载时确定。</small></div></div><div class="form-actions"><button type="button" id="preview-sub">预览匹配</button><button type="submit" class="primary">保存订阅</button></div></form>');
  bindDirectory($('#sub-form'),true);
  $('#preview-sub').onclick=e=>run(async()=>{
    const form=$('#sub-form');
    const result=await api('/subscriptions/preview','POST',{url:form.elements.url.value,filter:form.elements.filter.value,excludeFilter:form.elements.excludeFilter.value,isFilterRegex:form.elements.isFilterRegex.checked});
    if(!form.isConnected)return;
    previewDialog('订阅匹配预览','<p class="hint">'+esc(result.title)+' · '+result.items.filter(x=>x.matches).length+' / '+result.items.length+' 条匹配</p><div class="file-list">'+(result.items.length?result.items.map(x=>'<div class="file-row"><span class="badge '+(x.matches?'':'paused')+'">'+(x.matches?'匹配':'忽略')+'</span><span>'+esc(x.item.title)+'</span></div>').join(''):empty('暂无资源','此订阅源还没有发布内容。'))+'</div>');
  },e.target);
  bindForm('#sub-form',async data=>{const saved=await api('/subscriptions','POST',{...s,...data});rememberDirectory(data.nodeId,data.directory);closeModal();toast('订阅已保存');if(!source.id&&data.enabled)showCheck(await api('/subscriptions/check','POST',{id:saved.id}));if(state.page==='subscriptions')await subscriptions();});
}

function showCheck(result){
  toast('检查完成：提交 '+result.submitted+' 个任务，首次跳过 '+result.skipped+' 条');
  if(result.errors?.length)toast(result.errors.join('\n'),true);
}
