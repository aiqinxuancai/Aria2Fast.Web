import {esc,field,check,api,modal,toast,run,bytes} from './core.js';

const fields=[
  ['max-concurrent-downloads','同时下载数','3','number','min="1" max="20"'],
  ['max-connection-per-server','HTTP 单服务器连接数','8','number','min="1" max="16"'],
  ['split','HTTP 分片数','8','number','min="1" max="16"'],
  ['min-split-size','HTTP 最小分片大小','10M','text','placeholder="10M"'],
  ['bt-max-peers','每个 BT 任务最大连接数','128','number','min="1" max="512"'],
  ['max-overall-download-limit','总下载限速（0 = 不限制）','0','text','placeholder="0 / 10M"'],
  ['max-overall-upload-limit','总上传限速（0 = 不限制）','0','text','placeholder="0 / 1M"']
];

export function localTuning(s,trackers){
  const options=s.localOptions||{};
  return '<div class="settings-group"><h4>本地下载调优</h4><div class="form-grid">'+
    field('BT TCP 端口','localBtPort',s.localBtPort,'number','min="1024" max="65535"')+
    field('DHT UDP 端口','localDhtPort',s.localDhtPort,'number','min="1024" max="65535"')+
    fields.map(([key,label,value,type,attrs])=>field(label,'tuning-'+key,options[key]??value,type,attrs)).join('')+
    check('启用 DHT 节点发现','tuning-enable-dht',options['enable-dht']!=='false')+
    check('启用 PEX 节点交换','tuning-enable-peer-exchange',options['enable-peer-exchange']!=='false')+
    '</div><p class="hint">保存后重启本地服务生效。HTTP 连接数与分片数不会增加 BT 节点。上传跑满时，可尝试限制为实测上行的 70%～80%。</p><p class="hint">Docker 和路由器需映射相同的 BT TCP / DHT UDP 端口，RPC 端口仅用于管理。</p><button type="button" id="diagnose-local">连接诊断</button></div>'+
    '<div class="settings-group"><h4>公共 Tracker</h4><div class="form-grid">'+
    check('自动更新并添加公共 Tracker','trackerAutoUpdate',s.trackerAutoUpdate)+
    field('更新间隔（小时）','trackerUpdateHours',s.trackerUpdateHours,'number','min="1" max="168"')+
    '<label class="full">Tracker 列表来源（每行一个，最多 5 个）<textarea name="trackerSources" rows="3">'+esc(s.trackerSources)+'</textarea></label></div>'+
    '<p class="hint">适用于公共 BT；下载 PT 私有种子请关闭此项。更新作用于后续新任务，已有任务保留原配置。关闭后需重启本地服务。更新失败会保留上次有效列表。</p>'+
    '<p class="hint" id="tracker-status">'+trackerStatus(trackers)+'</p><button type="button" id="update-trackers">保存并更新 Tracker</button></div>';
}

function trackerStatus(t){
  return esc('缓存 '+(t?.urls?.length||0)+' 个 · 最近成功：'+(t?.lastSuccess?new Date(t.lastSuccess).toLocaleString():'尚未更新')+(t?.error?' · '+t.error:''));
}

export function tuningOptions(data,previous){
  const result={...previous};
  for(const key of Object.keys(data))if(key.startsWith('tuning-')){result[key.slice(7)]=String(data[key]);delete data[key];}
  return result;
}

export function bindLocalTuning(save){
  document.querySelector('#update-trackers').onclick=e=>run(async()=>{
    await save();
    const result=await api('/local/trackers/update','POST');
    const state=await api('/state');
    document.querySelector('#tracker-status').innerHTML=trackerStatus(state.trackers);
    toast('已更新 '+result.count+' 个 Tracker，'+(result.applied?'用于后续新任务':'启动本地服务后生效')+(result.warning?'；部分来源失败':''));
  },e.currentTarget);
  document.querySelector('#diagnose-local').onclick=e=>run(async()=>{
    const result=await api('/local/diagnostics');
    modal('本地下载连接诊断',
      (result.restartRequired?'<p class="callout">设置在服务启动后有变更；如需应用端口、DHT 等启动参数，请重启本地服务。</p>':'')+
      '<div class="stack">'+result.checks.map(x=>'<div class="callout"><strong>'+esc(x.name)+'</strong><p>'+esc(x.detail)+'</p></div>').join('')+'</div>'+
      '<h3>当前生效参数</h3><div class="pre">'+esc(Object.entries(result.options).filter(([key])=>key!=='bt-tracker').map(([key,value])=>key+' = '+value).join('\n'))+'</div>'+
      '<h3>活跃 BT 任务</h3>'+(result.torrents.length?result.torrents.map(x=>'<div class="file-row"><span>'+esc(x.name)+'<br>连接 '+esc(x.connections)+' · 做种连接 '+esc(x.seeders)+' · '+bytes(Number(x.downloadSpeed))+'/s</span></div>').join(''):'<p class="hint">暂无活跃 BT 任务。</p>')+
      '<p class="hint">节点少或长期没有做种连接时，资源热度可能是瓶颈。监听状态只检查本机端口，不能确认端口由哪个进程占用或公网是否可达。</p>');
  },e.currentTarget);
}
