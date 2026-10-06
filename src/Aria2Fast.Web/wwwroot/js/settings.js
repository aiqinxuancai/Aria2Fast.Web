import {$,esc,state,api,toast,run,modal,closeModal,heading,field,check,bindForm,refreshConfig,formData} from './core.js';

export async function settings(){
  await refreshConfig();const s=state.config.settings;
  $('#content').innerHTML=heading('设置中心','连接你的设备，打造适合自己的下载工作空间。')+'<div class="settings-layout">'+
    '<section class="panel"><div class="panel-head"><h3>外观与访问</h3><span class="badge">个人偏好</span></div><div class="panel-body"><div class="form-grid"><label>主题<select id="settings-theme"><option value="auto">跟随系统</option><option value="light">浅色</option><option value="dark">深色</option></select></label><div><p class="hint">主题保存在当前浏览器。自动模式会实时跟随系统深浅色设置。</p><button id="change-password">修改访问密码</button><button id="settings-logout" class="quiet">退出登录</button></div></div></div></section>'+
    '<section class="panel"><div class="panel-head"><h3>下载节点</h3><button id="new-node">＋ 远程节点</button></div><div class="panel-body" id="nodes">'+state.config.nodes.map(n=>'<div class="setting-row"><div><strong>'+esc(n.name)+'</strong><small>'+esc(n.url)+(n.id==='local'?' · '+(state.config.local.running?'运行中':'未启动'):'')+'</small></div><div class="toolbar"><button data-test-node="'+esc(n.id)+'">测试</button>'+(n.id!=='local'?'<button data-edit-node="'+esc(n.id)+'">编辑</button><button data-delete-node="'+esc(n.id)+'" class="danger">删除</button>':'<button id="restart-local">重启 Aria2</button>')+'</div></div>').join('')+(state.config.local.error?'<p class="hint error-text">'+esc(state.config.local.error)+'</p>':'')+'</div></section>'+
    '<form id="settings-form"><section class="panel"><div class="panel-head"><h3>本地下载与自动订阅</h3></div><div class="panel-body"><div class="form-grid">'+check('启用服务器本地 Aria2','localEnabled',s.localEnabled)+field('RPC 端口','localRpcPort',s.localRpcPort,'number','min="1024" max="65535"')+field('Aria2 执行文件（留空自动查找）','aria2Executable',s.aria2Executable)+field('本地下载目录','downloadDirectory',s.downloadDirectory,'text','required')+field('订阅检查间隔（分钟）','subscriptionIntervalMinutes',s.subscriptionIntervalMinutes,'number','min="1" max="1440"')+field('Mikan 源站','mikanBaseUrl',s.mikanBaseUrl,'url','required')+field('订阅 / AI 网络代理','proxyUrl',s.proxyUrl,'url','placeholder="http://host:port"')+'</div><p class="hint" style="margin-top:18px">修改本地执行文件、端口或目录后，先保存，再点击「重启 Aria2」。远程任务的保存路径以远程节点为准。</p></div></section>'+
    '<section class="panel"><div class="panel-head"><h3>AI 接口管理</h3><button type="button" id="new-ai">＋ 添加接口</button></div><div class="panel-body"><p class="hint">添加或编辑接口时可直接测试连接。测试使用弹窗当前填写的参数，无需先保存。</p>'+ (s.aiProfiles.length?s.aiProfiles.map(p=>'<div class="setting-row ai-profile-row"><div><strong>'+esc(p.name)+'</strong>'+(s.selectedAiId===p.id||(!s.selectedAiId&&s.aiProfiles[0]===p)?' <span class="badge">当前使用</span>':'')+'<small>'+esc(p.protocol)+' · '+esc(p.modelName)+'</small><small>'+esc(p.baseUrl)+'</small></div><div class="toolbar"><button type="button" data-edit-ai="'+esc(p.id)+'">编辑 / 测试</button><button type="button" data-delete-ai="'+esc(p.id)+'" class="danger">删除</button></div></div>').join(''):'<p class="hint">尚未配置 AI 接口，点击「添加接口」开始。</p>')+'</div></section>'+
    '<section class="panel"><div class="panel-head"><h3>AI 功能与番剧信息</h3></div><div class="panel-body"><div class="form-grid"><label class="full">功能使用的 AI 接口<select name="selectedAiId"><option value="">使用第一项</option>'+s.aiProfiles.map(p=>'<option value="'+esc(p.id)+'" '+(s.selectedAiId===p.id?'selected':'')+'>'+esc(p.name)+' · '+esc(p.modelName)+'</option>').join('')+'</select></label>'+check('查看详情时自动翻译简介','translateSummary',s.translateSummary)+check('查看详情时自动生成 AI 评析','autoReview',s.autoReview)+field('TMDB API Key','tmdbApiKey',s.tmdbApiKey,'password','autocomplete="off"')+field('Tavily API Key（评析联网参考）','tavilyApiKey',s.tavilyApiKey,'password','autocomplete="off"')+'</div><p class="hint" style="margin-top:18px">所选接口用于翻译、评析、AI 分目录与文件整理。功能设置需点击下方「保存所有设置」。TMDB 信息来自独立接口。</p></div></section>'+
    '<section class="panel"><div class="panel-head"><h3>完成通知 · PushDeer</h3></div><div class="panel-body"><div class="form-grid">'+check('启用下载完成推送','pushEnabled',s.pushEnabled)+field('PushDeer Key','pushKey',s.pushKey,'password','autocomplete="off"')+'<div class="full">'+field('推送接口','pushEndpoint',s.pushEndpoint,'url','required')+'</div></div><button type="button" id="test-push" style="margin-top:18px">发送测试通知</button></div></section>'+
    '<section class="panel"><div class="panel-head"><h3>订阅备份与 OSS 同步</h3></div><div class="panel-body"><div class="form-grid">'+field('OSS Endpoint','ossEndpoint',s.oss.endpoint,'url')+field('Bucket','ossBucket',s.oss.bucket)+field('Access Key ID','ossAccessKeyId',s.oss.accessKeyId)+field('Access Key Secret','ossAccessKeySecret',s.oss.accessKeySecret,'password','autocomplete="off"')+'<div class="full">'+field('对象 Key','ossObjectKey',s.oss.objectKey)+'</div></div><div class="toolbar" style="margin-top:20px"><a href="/api/backup" download>导出订阅 JSON ↗</a><button type="button" id="import-backup">导入订阅</button><button type="button" id="oss-upload">上传 OSS</button><button type="button" id="oss-download">从 OSS 合并</button><input type="file" id="backup-file" accept=".json" hidden></div><p class="hint" style="margin-top:18px">备份包含订阅规则与历史记录，不包含节点密钥、AI 密钥或登录密码。导入采用合并方式，支持桌面端订阅数组；跨设备导入需先匹配节点 ID。</p></div></section><div class="form-actions"><button class="primary" type="submit">保存所有设置</button></div></form></div>';
  $('#settings-theme').value=window.getTheme();$('#settings-theme').onchange=e=>{window.setTheme(e.target.value);$('#theme').value=e.target.value;};
  $('#settings-logout').onclick=()=>$('#logout').click();
  $('[name=ossEndpoint]').closest('.form-grid').insertAdjacentHTML('afterbegin',check('启用定时合并同步','ossAutoSync',s.oss.autoSync)+field('同步间隔（分钟）','ossIntervalMinutes',s.oss.intervalMinutes||30,'number','min="1" max="1440"'));
  const save=async()=>{const data=formData($('#settings-form'));const settings={...state.config.settings,...data,oss:{autoSync:data.ossAutoSync,intervalMinutes:data.ossIntervalMinutes,endpoint:data.ossEndpoint,bucket:data.ossBucket,accessKeyId:data.ossAccessKeyId,accessKeySecret:data.ossAccessKeySecret,objectKey:data.ossObjectKey}};for(const key of Object.keys(settings))if(key.startsWith('oss')&&key!=='oss')delete settings[key];await api('/settings','PUT',settings);await refreshConfig();};
  bindForm('#settings-form',async()=>{await save();toast('设置已保存');});
  $('#new-node').onclick=()=>editNode();$('#new-ai').onclick=()=>editAi();
  document.querySelectorAll('[data-edit-node]').forEach(b=>b.onclick=()=>editNode(state.config.nodes.find(n=>n.id===b.dataset.editNode)));
  document.querySelectorAll('[data-test-node]').forEach(b=>b.onclick=()=>run(async()=>{const r=await api('/nodes/'+b.dataset.testNode+'/test','POST');toast('连接成功 · Aria2 '+r.version);},b));
  document.querySelectorAll('[data-delete-node]').forEach(b=>b.onclick=()=>run(async()=>{if(!confirm('删除此远程节点？'))return;await api('/nodes/'+b.dataset.deleteNode,'DELETE');await settings();},b));
  document.querySelectorAll('[data-edit-ai]').forEach(b=>b.onclick=()=>editAi(state.config.settings.aiProfiles.find(p=>p.id===b.dataset.editAi)));
  document.querySelectorAll('[data-delete-ai]').forEach(b=>b.onclick=()=>run(async()=>{if(!confirm('删除此 AI 接口？'))return;await api('/ai/profiles/'+encodeURIComponent(b.dataset.deleteAi),'DELETE');await reloadAiSettings();},b));
  $('#restart-local').onclick=e=>run(async()=>{await save();const r=await api('/local/restart','POST');if(r.error)throw new Error(r.error);toast('本地 Aria2 已重启');await settings();},e.target);
  $('#test-push').onclick=e=>run(async()=>{await save();await api('/push/test','POST');toast('测试通知已发送');},e.target);
  $('#oss-upload').onclick=e=>run(async()=>{if(!confirm('用当前订阅覆盖 OSS 中的备份对象？'))return;await save();await api('/backup/oss/upload','POST');toast('已上传 OSS');},e.target);
  $('#oss-download').onclick=e=>run(async()=>{await save();const r=await api('/backup/oss/download','POST');toast('已合并 '+r.imported+' 条订阅');},e.target);
  $('#import-backup').onclick=()=>$('#backup-file').click();$('#backup-file').onchange=e=>run(async()=>{const file=e.target.files[0];if(!file)return;const parsed=JSON.parse(await file.text());const r=await api('/backup/import','POST',parsed);toast('已导入 '+r.imported+' 条订阅');e.target.value='';});
  $('#change-password').onclick=()=>{modal('修改访问密码','<form id="password-form" class="stack">'+field('当前密码','currentPassword','','password','required autocomplete="current-password"')+field('新密码（至少 12 字符）','newPassword','','password','required minlength="12" autocomplete="new-password"')+'<button class="primary" type="submit">更新密码并重新登录</button></form>');bindForm('#password-form',async data=>{await api('/auth/password','POST',data);closeModal();window.dispatchEvent(new Event('auth-expired'));toast('密码已更新，请重新登录');});};
}

function editNode(source={}){
  modal(source.id?'编辑节点':'添加远程节点','<form id="node-form" class="stack">'+field('节点名称','name',source.name||'远程节点','text','required')+field('JSON-RPC 地址','url',source.url||'http://127.0.0.1:6800/jsonrpc','url','required')+field('RPC Secret','secret',source.secret||'','password','autocomplete="off"')+field('远程默认下载目录','downloadDirectory',source.downloadDirectory||'')+'<p class="hint">地址从 Web 服务端连接。远程 Aria2 需启用 HTTP RPC，并允许 Web 服务器访问。</p><button class="primary" type="submit">保存节点</button></form>');
  bindForm('#node-form',async data=>{await api('/nodes','POST',{...source,...data});closeModal();toast('节点已保存');await settings();});
}
function editAi(source={}){
  const protocols=[['OpenAIChatCompletions','OpenAI 兼容 · Chat Completions'],['OpenAIResponses','OpenAI · Responses'],['Claude','Anthropic · Messages'],['Gemini','Google · Gemini']];
  modal(source.id?'编辑 AI 配置':'添加 AI 配置','<form id="ai-form" class="stack">'+field('配置名称','name',source.name||'AI 服务','text','required')+'<label>接口协议<select name="protocol">'+protocols.map(([v,t])=>'<option value="'+v+'" '+(source.protocol===v?'selected':'')+'>'+t+'</option>').join('')+'</select></label>'+field('API 基础地址','baseUrl',source.baseUrl||'https://api.openai.com','url','required')+field('模型名称','modelName',source.modelName||'','text','required placeholder="填写服务商提供的模型 ID"')+field('API Key','apiKey',source.apiKey||'','password','required autocomplete="off"')+'<p class="hint">填写基础地址，不含 chat/completions 等末尾路径。兼容地址可包含 /v1。</p><div class="ai-editor-actions"><button type="button" id="test-ai">测试连接</button><button type="submit" class="primary">保存 AI 配置</button></div><div id="ai-test-result" class="callout" role="status" aria-live="polite" hidden></div></form>');
  const form=$('#ai-form');const result=$('#ai-test-result');const test=$('#test-ai');
  let revision=0;
  form.addEventListener('input',()=>{revision++;if(!result.hidden){result.textContent='参数已修改，请重新测试。';result.className='callout';}});
  test.onclick=async()=>{
    if(!form.reportValidity())return;
    const current=revision;test.disabled=true;test.textContent='连接中…';
    result.hidden=false;result.className='callout';result.textContent='正在请求 AI 服务，请稍候…';
    try{
      const reply=await api('/ai/test-profile','POST',formData(form));
      if(!form.isConnected||revision!==current)return;
      result.className='callout ai-test-success';result.textContent='连接成功 · '+reply.elapsedMs+' ms\\n返回内容：\\n'+reply.text;
    }catch(error){
      if(form.isConnected&&revision===current){result.className='callout error';result.textContent='连接失败\\n'+error.message;}
    }finally{test.disabled=false;test.textContent='测试连接';}
  };
  bindForm('#ai-form',async data=>{await api('/ai/profiles','POST',{...source,...data,...(source.id?{id:source.id}:{})});closeModal();toast('AI 配置已保存');await reloadAiSettings();});
}

async function reloadAiSettings(){
  const draft=formData($('#settings-form'));
  await settings();
  for(const [name,value] of Object.entries(draft)){
    const input=$('#settings-form').elements.namedItem(name);
    if(!input)continue;
    if(input.type==='checkbox')input.checked=value;
    else if(input.tagName!=='SELECT'||[...input.options].some(o=>o.value===String(value)))input.value=value;
  }
}
