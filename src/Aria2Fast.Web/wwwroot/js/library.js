import {$,esc,state,api,run,toast,modal,closeModal,heading,empty,bytes,field} from './core.js';
import {episodeLabel,episodeNumber,episodeTitle,clock,percent,resumable,externalMenu,bindExternal,seriesPlaylist} from './media.js';

// The filter and sort survive visits to a work and back.
const view={q:'',sort:'updated',filter:'all'};
let token=0;let timer=0;let overview=null;
const STATUS={Pending:'获取中',Unmatched:'未匹配',Failed:'获取失败'};
const SOURCE={Manual:'手动指定',Subscription:'订阅记录',Mikan:'Mikan 番剧页',Search:'名称搜索'};
const KINDS=[['Episode','正片'],['Special','特别篇'],['Extra','花絮']];
const SORTS=[['updated','最近更新'],['played','最近观看'],['added','最近入库'],['title','名称'],['score','评分']];
const FILTERS=[['all','全部作品'],['unwatched','有未看的集'],['unmatched','未匹配资料']];
const options=(list,value)=>list.map(([v,l])=>'<option value="'+v+'"'+(v===value?' selected':'')+'>'+l+'</option>').join('');
const scrollPositions=new Map();

export async function library(id){
  const current=++token;
  clearTimeout(timer);
  state.leave=()=>{clearTimeout(timer);token++;if(!id)scrollPositions.set('wall',scrollY);};
  if(id)return detail(id,current);
  await wall(current);
  // Coming back from a work returns to the same place on the wall.
  if(current===token)scrollTo(0,scrollPositions.get('wall')||0);
}

async function wall(current){
  const data=await api('/library');
  if(current!==token)return;
  const s=data.status;
  $('#content').innerHTML=heading('媒体库','下载完成的视频自动整理成海报墙，点开就能播放。',s.enabled?'<button id="library-scan">↻ 扫描下载目录</button>':'')+'<div id="library-body"></div>';
  if(!s.enabled){$('#library-body').innerHTML='<div class="callout">媒体库已关闭，可以在 <a href="#settings">设置中心 → 媒体库</a> 中启用。</div>';return;}
  $('#library-body').innerHTML='<div id="library-notice"></div><section id="library-continue" aria-label="继续观看"></section><div class="library-tools"><input id="library-search" type="search" class="search" placeholder="搜索作品…" aria-label="搜索媒体库" value="'+esc(view.q)+'"><select id="library-sort" aria-label="排序">'+options(SORTS,view.sort)+'</select><select id="library-filter" aria-label="筛选">'+options(FILTERS,view.filter)+'</select></div><div id="library-grid"></div>';
  $('#library-search').oninput=e=>{view.q=e.target.value;grid();};
  $('#library-sort').onchange=e=>{view.sort=e.target.value;grid();};
  $('#library-filter').onchange=e=>{view.filter=e.target.value;grid();};
  $('#library-scan').onclick=e=>run(async()=>{const r=await api('/library/scan','POST');toast('扫描完成 · '+r.series+' 部作品，'+r.episodes+' 个视频');await refresh(current);},e.currentTarget);
  show(data,current);
}

async function refresh(current){
  try{const data=await api('/library');if(current===token&&$('#library-grid'))show(data,current);}catch{}
}

function show(data,current){
  overview=data;
  const s=data.status;
  const notices=[];
  if(s.warning)notices.push('<div class="callout error">'+esc(s.warning)+'</div>');
  if(s.fetchMetadata&&s.pending)notices.push('<div class="callout">正在后台获取 '+s.pending+' 部作品的海报和简介…</div>');
  $('#library-notice').innerHTML=notices.join('');
  $('#library-continue').innerHTML=continueRow(data.continueWatching);
  grid();
  // Posters arrive one work at a time; keep the wall current until they are all in.
  clearTimeout(timer);
  if((s.fetchMetadata&&s.pending)||s.scanning)timer=setTimeout(()=>refresh(current),4000);
}

function continueRow(items){
  if(!items.length)return '';
  return '<div class="anime-day-heading"><h2>继续观看</h2><span>'+items.length+' 部作品</span></div><div class="continue-row">'+items.map(c=>{
    const e=c.episode;const pct=percent(e);const started=resumable(e)&&!e.watched;
    return '<a class="continue-item" href="#play/'+esc(e.id)+'"><div class="continue-thumb">'+(c.poster?'<img loading="lazy" src="'+esc(c.poster)+'" alt="">':'')+'<i aria-hidden="true">▶</i></div><div class="continue-info"><strong>'+esc(c.title)+'</strong><span>'+esc(episodeTitle(e))+'</span><small>'+(started?'看到 '+clock(e.position)+' / '+clock(e.duration):'下一集')+'</small>'+(started?'<div class="progress"><span style="width:'+pct+'%"></span></div>':'')+'</div></a>';
  }).join('')+'</div>';
}

function visible(list){
  const q=view.q.trim().toLowerCase();
  const items=list.filter(s=>!q||[s.title,s.originalTitle,s.folderTitle].some(t=>(t||'').toLowerCase().includes(q)));
  const shown=view.filter==='unwatched'?items.filter(s=>s.episodeCount>s.watchedCount):view.filter==='unmatched'?items.filter(s=>s.status!=='Matched'):items;
  const date=v=>v?Date.parse(v):0;
  const by={updated:(a,b)=>date(b.updatedAt)-date(a.updatedAt),played:(a,b)=>date(b.playedAt)-date(a.playedAt)||date(b.updatedAt)-date(a.updatedAt),added:(a,b)=>date(b.addedAt)-date(a.addedAt),title:(a,b)=>a.title.localeCompare(b.title,'zh-CN'),score:(a,b)=>(b.score??-1)-(a.score??-1)};
  return shown.sort(by[view.sort]||by.updated);
}

function grid(){
  const box=$('#library-grid');if(!box||!overview)return;
  if(!overview.series.length){box.innerHTML='<div class="panel">'+empty('媒体库还是空的','下载完成的视频会自动出现在这里。下载目录里已有的视频，点右上角「扫描下载目录」即可整理。','▷')+'</div>';return;}
  const items=visible(overview.series);
  box.innerHTML=items.length?'<div class="anime-grid library-grid">'+items.map(card).join('')+'</div>':empty('没有符合条件的作品','换个关键词或筛选条件试试。','▷');
}

function card(s){
  const total=s.totalEpisodes&&s.totalEpisodes>=s.episodeCount?s.totalEpisodes:null;
  const left=s.episodeCount-s.watchedCount;const done=s.episodeCount>0&&left===0;
  const pct=s.episodeCount?Math.round(s.watchedCount/s.episodeCount*100):0;
  const recent=Date.now()-Date.parse(s.updatedAt)<3*864e5;
  const meta=[s.season>1?'第 '+s.season+' 季':'',s.airDate?s.airDate.slice(0,4):'',s.episodeCount?s.episodeCount+(total?' / '+total:'')+' 集':s.extraCount?s.extraCount+' 个视频':''].filter(Boolean).join(' · ');
  const badge=STATUS[s.status]?'<b class="'+s.status.toLowerCase()+'">'+STATUS[s.status]+'</b>':'';
  const flag=s.watchedCount>0&&left>0?'<b class="unwatched">'+left+' 集未看</b>':!s.watchedCount&&recent?'<b class="unwatched">新</b>':'';
  return '<a class="library-card" href="#library/'+esc(s.id)+'" data-series="'+esc(s.id)+'"><div class="poster">'+(s.poster?'<img loading="lazy" src="'+esc(s.poster)+'" alt="">':'<div class="poster-fallback"><strong>'+esc(s.title)+'</strong></div>')+'<div class="library-badges">'+badge+flag+'</div>'+(done?'<span>✓ 已看完</span>':'')+(pct>0&&!done?'<div class="library-progress" aria-label="已看 '+s.watchedCount+' 集"><i style="width:'+pct+'%"></i></div>':'')+'</div><h3 title="'+esc(s.title)+'">'+esc(s.title)+'</h3><small>'+esc(meta)+'</small></a>';
}

async function detail(id,current){
  const d=await api('/library/'+encodeURIComponent(id));
  if(current!==token)return;
  scrollTo(0,0);
  renderDetail(d);
}

function renderDetail(d){
  const s=d.series,m=d.metadata,r=d.resume;
  const facts=[s.season?'第 '+s.season+' 季':'',m?.platform,m?.airDate?'首播 '+m.airDate:'',m?.score?'★ '+m.score.toFixed(1):'',s.episodeCount?s.episodeCount+(m?.totalEpisodes?' / '+m.totalEpisodes:'')+' 集':'',s.watchedCount?'已看 '+s.watchedCount+' 集':'',bytes(s.size)].filter(Boolean);
  const source=m?'资料：<a href="'+esc(m.url)+'" target="_blank" rel="noopener noreferrer">'+(m.source==='bangumi'?'Bangumi':'TMDB')+' #'+m.sourceId+'</a>'+(s.matchedBy?' · 通过'+SOURCE[s.matchedBy]+'匹配':''):'';
  const status=s.status==='Matched'?'':'<div class="callout'+(s.status==='Failed'?' error':'')+'">'+esc(s.status==='Pending'?'正在获取海报和简介…':s.error||'没有找到匹配的条目')+'，可以点「修正匹配」手动选择。</div>';
  const resumed=r&&resumable(r);
  const play=r?'<a class="button primary" href="#play/'+esc(r.id)+'">▶ '+(resumed?'继续播放':'播放')+(r.number!=null?' '+esc(episodeLabel(r)):'')+(resumed?' · '+clock(r.position):'')+'</a>':'';
  const fixed=d.episodes.some(e=>e.numberSource);
  const more='<details class="sub-more popup"><summary>更多</summary><div class="sub-menu"><button type="button" data-all="true">全部标为已看</button><button type="button" data-all="false">全部标为未看</button><button type="button" id="series-playlist">下载播放列表（M3U）</button>'+(fixed?'<button type="button" id="series-reset">恢复自动识别集数</button>':'')+'</div></details>';
  const unnumbered=s.unnumbered?'<div class="callout">有 '+s.unnumbered+' 个视频没能从文件名识别出集数。'+(d.aiAvailable?'<button type="button" class="compact" id="series-recognize">用 AI 识别集数</button>':'在设置中配置 AI 后可以自动识别，')+' 也可以点视频旁的「集数」手动修改。</div>':'';
  $('#content').innerHTML='<a class="back-link" href="#library">← 媒体库</a>'+
    '<section class="panel library-hero"><div class="poster">'+(s.poster?'<img src="'+esc(s.poster)+'" alt="">':'<div class="poster-fallback"><strong>'+esc(s.title)+'</strong></div>')+'</div>'+
    '<div class="library-info"><div class="eyebrow">'+esc(s.directory||'下载目录')+'</div><h1>'+esc(s.title)+'</h1>'+(s.originalTitle&&s.originalTitle!==s.title?'<p class="library-original">'+esc(s.originalTitle)+'</p>':'')+'<div class="library-facts">'+facts.map(f=>'<span>'+esc(f)+'</span>').join('')+'</div></div>'+
    '<div class="library-more">'+(m?.summary?'<p class="library-summary" id="library-summary">'+esc(m.summary)+'</p>':'')+(source?'<p class="library-source">'+source+'</p>':'')+status+'<div class="library-actions">'+play+'<button type="button" id="series-match">修正匹配</button><button type="button" id="series-refresh">重新获取资料</button>'+more+'</div></div></section>'+
    unnumbered+
    (d.episodes.length?KINDS.map(([kind,label])=>{const list=d.episodes.filter(e=>e.kind===kind);return list.length?'<section class="panel"><div class="panel-head"><h2>'+label+'</h2><span class="muted">'+list.length+' 个视频</span></div><div class="episode-list">'+list.map(row).join('')+'</div></section>':'';}).join(''):'<div class="panel">'+empty('没有视频','这部作品的视频可能已被移动或删除。','▷')+'</div>');
  const summary=$('#library-summary');
  if(summary&&summary.scrollHeight>summary.clientHeight+4){summary.insertAdjacentHTML('afterend','<button type="button" class="text-button" id="summary-toggle">展开简介</button>');$('#summary-toggle').onclick=e=>{const open=summary.classList.toggle('expanded');e.target.textContent=open?'收起简介':'展开简介';};}
  bindExternal($('#content'));
  const update=next=>{if(state.page==='library')renderDetail(next);};
  $('#series-match').onclick=()=>editMatch(d,update);
  $('#series-refresh').onclick=e=>run(async()=>{update(await api('/library/'+s.id+'/refresh','POST'));toast('资料已更新');},e.currentTarget);
  $('#series-playlist').onclick=e=>run(()=>seriesPlaylist(s.id),e.currentTarget);
  document.querySelectorAll('[data-all]').forEach(b=>b.onclick=()=>run(async()=>{update(await api('/library/'+s.id+'/watched','POST',{watched:b.dataset.all==='true'}));},b));
  if($('#series-reset'))$('#series-reset').onclick=e=>run(async()=>{if(!confirm('清除这部作品所有手动和 AI 修改的集数，重新按文件名识别？'))return;update(await api('/library/'+s.id+'/numbers','DELETE'));toast('已恢复按文件名识别');},e.currentTarget);
  if($('#series-recognize'))$('#series-recognize').onclick=e=>run(async()=>{const result=await api('/library/'+s.id+'/recognize','POST');update(result.detail);toast('AI 识别了 '+result.recognized+' / '+result.total+' 个视频的集数');},e.currentTarget);
  document.querySelectorAll('[data-watched]').forEach(b=>b.onclick=()=>run(async()=>{await api('/library/episodes/'+b.dataset.watched+'/watched','POST',{watched:b.getAttribute('aria-pressed')!=='true'});update(await api('/library/'+s.id));},b));
  document.querySelectorAll('[data-number]').forEach(b=>b.onclick=()=>editNumber(d.episodes.find(e=>e.id===b.dataset.number),update));
}

function row(e){
  const pct=percent(e);
  const title=e.title||(e.number!=null?episodeLabel(e):e.name);
  const note=e.numberSource?' · '+(e.numberSource==='ai'?'AI 识别的集数':'手动修改的集数'):'';
  return '<div class="episode-row'+(e.watched?' watched':'')+'"><a class="episode-main" href="#play/'+esc(e.id)+'"><span class="episode-no">'+esc(episodeNumber(e))+'</span><span class="episode-text"><strong>'+esc(title)+'</strong><small>'+(title===e.name?'':esc(e.name)+' · ')+bytes(e.size)+(e.airDate?' · '+esc(e.airDate):'')+esc(note)+'</small>'+(resumable(e)&&!e.watched?'<span class="progress"><span style="width:'+pct+'%"></span></span>':'')+'</span></a>'+
    '<div class="episode-actions"><button type="button" class="compact" data-watched="'+esc(e.id)+'" aria-pressed="'+e.watched+'">'+(e.watched?'✓ 已看':'标为已看')+'</button>'+externalMenu(e.id,e.name)+'<button type="button" class="compact" data-number="'+esc(e.id)+'">集数</button></div></div>';
}

function editNumber(e,update){
  modal('修改集数','<form id="number-form" class="stack"><p class="hint file-name">'+esc(e.path)+'</p><div class="form-grid"><label>集数<input name="number" type="number" step="any" min="0" max="9999" value="'+(e.number??'')+'" placeholder="留空表示没有集数"></label><label>类型<select name="kind">'+options(KINDS,e.kind)+'</select></label></div><p class="hint">修改会一直保留，重新扫描也不会覆盖。</p><div class="toolbar"><button class="primary" type="submit">保存</button>'+(e.numberSource?'<button type="button" id="number-reset">恢复按文件名识别</button>':'')+'</div></form>');
  const form=$('#number-form');
  form.onsubmit=ev=>{ev.preventDefault();run(async()=>{const value=form.elements.number.value;update(await api('/library/episodes/'+e.id+'/number','POST',{number:value===''?null:Number(value),kind:form.elements.kind.value}));closeModal();},form.querySelector('[type=submit]'));};
  if($('#number-reset'))$('#number-reset').onclick=ev=>run(async()=>{update(await api('/library/episodes/'+e.id+'/number','DELETE'));closeModal();},ev.currentTarget);
}

function editMatch(d,update){
  const s=d.series;
  modal('修正匹配','<form id="match-search" class="finder-form"><input name="q" type="search" required maxlength="100" value="'+esc(s.folderTitle)+'" aria-label="作品名"><button class="primary" type="submit">搜索 Bangumi</button></form><div id="match-results" class="match-results" aria-live="polite"></div>'+
    '<details class="match-manual"><summary>直接填写条目 ID</summary><form id="match-id" class="stack"><div class="form-grid">'+field('Bangumi 条目 ID','bgmId',s.manualMatch&&s.bgmId?s.bgmId:'','number','min="1" placeholder="bgm.tv/subject/ 后的数字"')+field('TMDB 剧集 ID','tmdbId',s.manualMatch&&!s.bgmId&&s.tmdbId?s.tmdbId:'','number','min="1" placeholder="需要 TMDB API Key"')+'</div><div class="toolbar"><button class="primary" type="submit">保存</button></div></form></details>'+
    (s.manualMatch?'<button type="button" id="match-auto">恢复自动匹配</button>':''));
  const choose=(body,button)=>run(async()=>{const next=await api('/library/'+s.id+'/match','POST',body);closeModal();update(next);toast(next.series.status==='Matched'?'已匹配：'+next.series.title:next.series.error||'匹配失败',next.series.status!=='Matched');},button);
  const search=async()=>{
    const q=$('#match-search').elements.q.value.trim();if(!q)return;
    const box=$('#match-results');box.innerHTML='<div class="loading">正在搜索…</div>';
    try{
      const list=await api('/library/search?q='+encodeURIComponent(q));
      box.innerHTML=list.length?list.map(c=>'<div class="match-row'+(c.id===s.bgmId?' current':'')+'">'+(c.image?'<img src="'+esc(c.image)+'" alt="" loading="lazy" referrerpolicy="no-referrer">':'<span class="match-thumb"></span>')+'<div><strong>'+esc(c.nameCn||c.name)+'</strong><small>'+esc([c.nameCn?c.name:'',c.date,c.episodes?c.episodes+' 集':''].filter(Boolean).join(' · '))+'</small><a href="'+esc(c.url)+'" target="_blank" rel="noopener noreferrer">在 bgm.tv 查看</a></div><button type="button" class="compact'+(c.id===s.bgmId?'':' primary')+'" data-pick="'+c.id+'">'+(c.id===s.bgmId?'当前':'选择')+'</button></div>').join(''):'<p class="hint">没有找到，换个名称（中文、日文或罗马字）试试。</p>';
      box.querySelectorAll('[data-pick]').forEach(b=>b.onclick=()=>choose({bgmId:Number(b.dataset.pick)},b));
    }catch(error){box.innerHTML='<div class="callout error">'+esc(error.message)+'</div>';}
  };
  $('#match-search').onsubmit=e=>{e.preventDefault();search();};
  $('#match-id').onsubmit=e=>{e.preventDefault();const f=e.target.elements;const bgmId=Number(f.bgmId.value)||null,tmdbId=Number(f.tmdbId.value)||null;if(!bgmId&&!tmdbId){toast('请填写 Bangumi 或 TMDB 条目 ID',true);return;}choose({bgmId,tmdbId:bgmId?null:tmdbId},e.target.querySelector('[type=submit]'));};
  if($('#match-auto'))$('#match-auto').onclick=e=>choose({bgmId:null,tmdbId:null},e.currentTarget);
  search();
}
