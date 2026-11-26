import {$,esc,state,api,toast,run,modal,closeModal,heading,empty,bytes,time} from './core.js';
import {editSubscription} from './subscriptions.js';
import {addTask} from './downloads.js';
import {bindAnimeReview} from './anime-review.js';
let cards=[];let search='';let day='全部';let badgeObserver;
// "calendar" browses Mikan; "search" queries the enabled download sources. Plain #anime always opens the calendar.
let mode='calendar';
const finder={q:'',title:'',source:'all',category:'',filter:'',sources:[],results:null,request:0};
const MAX_ROWS=300;
const weekdays=['星期一','星期二','星期三','星期四','星期五','星期六','星期日'];
const dayOrder=value=>{const index=weekdays.indexOf(value);return index<0?7:index;};
const orderedDays=items=>[...new Set(items.map(c=>c.day||'其他'))].sort((a,b)=>dayOrder(a)-dayOrder(b));
export async function anime(){
  const params=new URLSearchParams(location.hash.split('?')[1]||'');
  mode=params.get('mode')==='search'?'search':'calendar';
  if(mode==='search'&&params.get('q')&&params.get('q')!==finder.q){finder.q=params.get('q');finder.title='';finder.results=null;}
  $('#content').innerHTML='<div class="anime-hero"><div class="eyebrow">DISCOVER YOUR NEXT FAVORITE</div><h1>新的故事，正在发生。</h1><p>从当季新番到心仪的续作，在这里发现、订阅，静待下一集。</p></div><div class="page-heading anime-heading"><div><div class="segmented" role="tablist" aria-label="浏览方式"><button type="button" role="tab" data-mode="calendar">番剧日历</button><button type="button" role="tab" data-mode="search">资源搜索</button></div><p id="anime-subtitle"></p></div><div class="toolbar" id="anime-tools"></div></div><div id="anime-body"></div>';
  $('.segmented').onclick=e=>{const b=e.target.closest('[data-mode]');if(b&&b.dataset.mode!==mode)run(()=>switchMode(b.dataset.mode));};
  await renderMode();
}
function switchMode(next){
  mode=next;
  history.replaceState(null,'',next==='search'?'#anime?mode=search'+(finder.q?'&q='+encodeURIComponent(finder.q):''):'#anime');
  return renderMode();
}
async function renderMode(){
  document.querySelectorAll('.segmented [data-mode]').forEach(b=>{const on=b.dataset.mode===mode;b.classList.toggle('active',on);b.setAttribute('aria-selected',String(on));});
  if(mode==='search'){
    $('#anime-tools').innerHTML='';
    $('#anime-subtitle').textContent='ACG.RIP · Nyaa · 动漫花园 · Prowlarr / Jackett 聚合搜索，一键下载或转为订阅';
    await renderFinder();
    return;
  }
  const now=new Date();
  $('#anime-subtitle').textContent='Mikan 番剧日历 · 字幕组订阅 · AI 评析';
  $('#anime-tools').innerHTML='<select id="anime-year" aria-label="年份">'+Array.from({length:12},(_,i)=>now.getFullYear()-i).map(y=>'<option>'+y+'</option>').join('')+'</select><select id="anime-season" aria-label="季度">'+['冬','春','夏','秋'].map((s,i)=>'<option '+(i===Math.floor(now.getMonth()/3)?'selected':'')+'>'+s+'</option>').join('')+'</select><button id="anime-load">↻ 加载季度</button>';
  $('#anime-body').innerHTML='<div class="panel-head" style="padding:0 0 20px;border:0"><div id="day-tabs" class="tabs"></div><input id="anime-search" type="search" class="search" placeholder="搜索作品名称…" aria-label="搜索番剧" value="'+esc(search)+'"></div><div id="anime-results"><div class="loading">正在连接 Mikan…</div></div>';
  $('#anime-search').oninput=e=>{search=e.target.value;render();};
  $('#anime-load').onclick=e=>run(()=>load(true),e.target);
  await load(false);
}
async function load(refresh){
  const year=$('#anime-year').value;const season=$('#anime-season').value;
  $('#anime-results').innerHTML='<div class="loading">正在获取季度番剧…</div>';
  try{cards=await api('/anime?year='+year+'&season='+encodeURIComponent(season)+'&refresh='+refresh);if(state.page!=='anime'||mode!=='calendar'||!$('#day-tabs'))return;const days=['全部',...orderedDays(cards)];if(!days.includes(day))day='全部';$('#day-tabs').innerHTML=days.map(d=>'<button data-day="'+esc(d)+'" class="'+(d===day?'active':'')+'">'+esc(d)+'</button>').join('');$('#day-tabs').onclick=e=>{const b=e.target.closest('[data-day]');if(b){day=b.dataset.day;$('#day-tabs').querySelectorAll('button').forEach(x=>x.classList.toggle('active',x===b));render();}};render();}catch(error){if(state.page==='anime'&&$('#anime-results'))$('#anime-results').innerHTML='<div class="callout error">'+esc(error.message)+'<br>可在设置中切换 Mikan 源站、配置代理后重试。</div>';}
}
function cardMarkup(c){
  const unpublished=c.hasReleases===false;
  return '<button class="anime-card'+(unpublished?' unpublished':'')+'" '+'data-anime="'+esc(c.id)+'"'+(unpublished?' aria-label="'+esc(c.name+'，暂无字幕组发布，查看详情')+'"':'')+'><div class="poster">'+(c.image?'<img loading="lazy" src="'+esc(c.image)+'" alt="'+esc(c.name)+'" referrerpolicy="no-referrer">':'')+(unpublished?'<span>暂无字幕组发布</span>':'')+'<div class="anime-badges"></div><div class="anime-episode"></div></div><h3 title="'+esc(c.name)+'">'+esc(c.name)+'</h3></button>';
}
function badgeMarkup(b){
  return (b.hot?'<b class="hot '+esc(b.hot)+'" title="'+b.groupCount+' 个字幕组">Hot</b>':'')+(b.updatedGroups?'<b class="updated" title="最近 24 小时更新的字幕组">'+b.updatedGroups+' 更新</b>':'');
}
function observeBadges(){
  badgeObserver?.disconnect();
  const current=cards;
  badgeObserver=new IntersectionObserver(entries=>{
    entries.filter(x=>x.isIntersecting).forEach(entry=>{
      badgeObserver.unobserve(entry.target);
      const card=current.find(c=>c.id===entry.target.dataset.anime);
      if(!card)return;
      const show=b=>{
        if(!entry.target.isConnected||cards!==current)return;
        entry.target.querySelector('.anime-badges').innerHTML=badgeMarkup(b);
        entry.target.querySelector('.anime-episode').textContent=b.latestEpisode?'第 '+b.latestEpisode+' 集':'';
      };
      if(card.badges){show(card.badges);return;}
      card.badgeRequest??=api('/anime/'+card.id+'/badges').then(b=>card.badges=b).finally(()=>card.badgeRequest=null);
      card.badgeRequest.then(show).catch(()=>{if(entry.target.isConnected)entry.target.querySelector('.anime-badges').title='更新信息暂不可用';});
    });
  },{rootMargin:'100px'});
  document.querySelectorAll('.anime-card[data-anime]:not(.unpublished)').forEach(card=>badgeObserver.observe(card));
}
function render(){
  const matches=cards.filter(c=>(day==='全部'||(c.day||'其他')===day)&&c.name.toLowerCase().includes(search.toLowerCase()));
  $('#anime-results').innerHTML=matches.length?orderedDays(matches).map(d=>{
    const items=matches.filter(c=>(c.day||'其他')===d);
    return '<section class="anime-day" aria-label="'+esc(d)+'"><div class="anime-day-heading"><h2>'+esc(d)+'</h2><span>'+items.length+' 部作品</span></div><div class="anime-grid">'+items.map(cardMarkup).join('')+'</div></section>';
  }).join(''):empty('没有找到相关作品','换个关键词，或选择其他季度试试看。','▦');
  $('#anime-results').querySelectorAll('[data-anime]').forEach(b=>b.onclick=()=>run(()=>detail(b.dataset.anime),b));
  observeBadges();
}
function feedKey(value){
  try{const url=new URL(value);url.hash='';url.searchParams.sort();return url.href;}catch{return value;}
}
function subscriptionBadge(group,subscriptions){
  const matches=subscriptions.filter(s=>feedKey(s.url)===feedKey(group.url));
  if(!matches.length)return '';
  const nodes=matches.map(s=>(state.config.nodes.find(n=>n.id===s.nodeId)?.name||s.nodeId)+(s.enabled?'':'（已暂停）')).join('、');
  return '<span class="badge anime-subscribed" title="'+esc('已添加至：'+nodes)+'">✓ 已订阅'+(matches.every(s=>!s.enabled)?' · 已暂停':'')+'</span>';
}
async function detail(id){
  modal(cards.find(c=>c.id===id)?.name||'番剧详情','<div class="loading">获取作品与字幕组…</div>');
  const loading=$('#modal-body .loading');
  let d,subscriptions;
  try{[d,subscriptions]=await Promise.all([api('/anime/'+id+'?generateAi=false'),api('/subscriptions')]);}
  catch(e){if(loading.isConnected)$('#modal-body').innerHTML='<div class="callout error">'+esc(e.message)+'</div>';return;}
  if(!loading.isConnected||!$('#modal').open)return;
  $('#modal-title').textContent=d.name;
  $('#modal-body').innerHTML='<p class="pre" id="anime-summary">'+esc(d.summary||d.tmdb?.overview||'源站暂未提供该番剧的简介。')+'</p>'+(d.tmdb?'<div class="detail-metrics"><div>'+d.tmdb.score.toFixed(1)+'<small>TMDB 评分 · '+d.tmdb.voteCount+' 人</small></div><div>'+d.tmdb.popularity.toFixed(0)+'<small>TMDB 热度</small></div><div>'+esc(d.tmdb.firstAirDate||'—')+'<small>首播日期</small></div></div><p class="hint">TMDB 匹配作品：'+esc(d.tmdb.name)+'。搜索匹配可能存在同名差异。</p>':'<p class="hint">配置 TMDB API Key 后可显示真实评分、投票数与热度。</p>')+'<div class="toolbar"><button id="ai-review">✧ AI 评析</button><button id="translate-summary">翻译简介</button><a href="'+esc(cards.find(c=>c.id===id)?.url||'#')+'" target="_blank" rel="noopener noreferrer">前往 Mikan ↗</a></div><div id="ai-review-content" style="margin-top:20px">'+(d.review?esc((d.review.score??'信息不足')+' · '+d.review.review):'')+'</div><h3 style="margin-top:25px">选择字幕组</h3>'+d.groups.map((g,i)=>'<div class="file-row anime-group-row"><span class="anime-group-name">'+esc(g.name)+subscriptionBadge(g,subscriptions)+'</span><button data-feed="'+i+'">查看更新</button><button class="primary" data-sub="'+i+'">订阅</button></div>').join('')+(d.groups.length?'':'<p class="hint" id="anime-groups-empty">暂无字幕组发布，发布后可查看更新和订阅。</p>')+'<div class="anime-cross-source"><button type="button" id="search-sources">⌕ 在其他下载源中搜索</button><span class="hint">用作品名在 ACG.RIP、Nyaa 等已启用的下载源中查找补充资源</span></div><div id="anime-feed"></div>';
  $('#search-sources').onclick=()=>{closeModal();run(()=>openFinder(d.name));};
  bindAnimeReview(id,d.review,$('#ai-review'),$('#ai-review-content'),state.config.settings.autoReview);
  const summaryElement=$('#anime-summary');
  const translateButton=$('#translate-summary');
  const translate=()=>run(async()=>{const r=await api('/anime/translate','POST',{text:d.originalSummary||d.summary||d.tmdb?.overview||''});if(summaryElement.isConnected)summaryElement.textContent=r.text;},translateButton);
  translateButton.onclick=translate;
  translateButton.disabled=!(d.originalSummary||d.summary||d.tmdb?.overview);
  if(state.config.settings.translateSummary&&d.originalSummary&&d.summary===d.originalSummary)translate();
  $('#modal-body').querySelectorAll('[data-sub]').forEach(b=>b.onclick=()=>editSubscription({name:d.name+' · '+d.groups[b.dataset.sub].name,url:d.groups[b.dataset.sub].url,namePath:d.name,mikanId:d.id}));
  $('#modal-body').querySelectorAll('[data-feed]').forEach(b=>b.onclick=()=>run(async()=>{const result=await api('/subscriptions/preview','POST',{url:d.groups[b.dataset.feed].url});$('#anime-feed').innerHTML='<h3 style="margin-top:24px">最新发布</h3><div class="file-list">'+result.items.map((x,i)=>'<div class="file-row"><span>'+esc(x.item.title)+'<br><small class="muted">'+bytes(x.item.size)+'</small></span><button data-download="'+i+'">下载</button></div>').join('')+'</div>';$('#anime-feed').querySelectorAll('[data-download]').forEach(btn=>btn.onclick=()=>addTask(result.items[btn.dataset.download].item.url));},b));
}

// Resource search: built-in sites and Torznab indexers, merged and sorted by publish time.
async function openFinder(title){
  Object.assign(finder,{q:title,title,source:'all',category:'',filter:'',results:null});
  mode='search';
  history.replaceState(null,'','#anime?mode=search&q='+encodeURIComponent(title));
  await renderMode();
}
async function renderFinder(){
  $('#anime-body').innerHTML='<div class="loading">正在读取下载源…</div>';
  try{finder.sources=await api('/anime/sources');}
  catch(error){if(mode==='search'&&$('#anime-body'))$('#anime-body').innerHTML='<div class="callout error">'+esc(error.message)+'</div>';return;}
  if(state.page!=='anime'||mode!=='search'||!$('#anime-body'))return;
  if(!finder.sources.length){$('#anime-body').innerHTML='<div class="panel">'+empty('尚未启用下载源','前往「设置中心 → 资源搜索源」启用 ACG.RIP、Nyaa、动漫花园，或添加 Prowlarr / Jackett。','⌕')+'<p style="text-align:center;margin-top:-30px;padding-bottom:30px"><a href="#settings">前往设置 →</a></p></div>';return;}
  if(finder.source!=='all'&&!finder.sources.some(s=>s.id===finder.source))finder.source='all';
  $('#anime-body').innerHTML='<form id="finder-form" class="finder-form" role="search"><input id="finder-q" name="q" type="search" required maxlength="100" aria-label="搜索关键词" placeholder="作品名、字幕组或关键词，例如：葬送的芙莉莲 1080p" value="'+esc(finder.q)+'"><select id="finder-source" aria-label="下载源"><option value="all">全部已启用源（'+finder.sources.length+'）</option>'+finder.sources.map(s=>'<option value="'+esc(s.id)+'" '+(s.id===finder.source?'selected':'')+'>'+esc(s.name)+'</option>').join('')+'</select><select id="finder-category" aria-label="分类" hidden></select><select id="finder-filter" aria-label="筛选" hidden></select><button type="submit" class="primary">搜索</button></form><p class="hint finder-hint">关键词越短结果越多。部分站点在大陆需要配置网络代理或镜像地址；遇到人机验证时可改用镜像，或通过 Prowlarr / Jackett 接入。</p><div id="finder-summary" class="finder-summary"></div><div id="finder-results"></div>';
  const fill=()=>{
    const source=finder.sources.find(s=>s.id===$('#finder-source').value);
    for(const [id,key] of [['#finder-category','categories'],['#finder-filter','filters']]){
      const options=source?.[key]||[];const select=$(id);const current=key==='categories'?finder.category:finder.filter;
      select.hidden=!options.length;
      select.innerHTML=options.map(o=>'<option value="'+esc(o.value)+'" '+(o.value===current?'selected':'')+'>'+esc(o.label)+'</option>').join('');
    }
  };
  fill();
  $('#finder-source').onchange=()=>{finder.category='';finder.filter='';fill();};
  $('#finder-form').onsubmit=e=>{e.preventDefault();searchSources();};
  if(finder.results&&finder.results.q===finder.q)renderResults();
  else if(finder.q)await searchSources();
  else $('#finder-results').innerHTML='<div class="panel">'+empty('搜索全网资源','输入作品名称，在已启用的下载源中查找资源，可直接下载，或把搜索结果转为订阅自动追更。','⌕')+'</div>';
}
async function searchSources(refresh=false){
  const q=$('#finder-q').value.trim();
  if(!q){$('#finder-q').reportValidity();return;}
  const source=$('#finder-source').value;
  Object.assign(finder,{q,source,category:$('#finder-category').hidden?'':$('#finder-category').value,filter:$('#finder-filter').hidden?'':$('#finder-filter').value});
  history.replaceState(null,'','#anime?mode=search&q='+encodeURIComponent(q));
  const params=new URLSearchParams({source,q});
  if(source!=='all'){if(finder.category)params.set('category',finder.category);if(finder.filter)params.set('filter',finder.filter);}
  if(refresh)params.set('refresh','true');
  const token=++finder.request;const button=$('#finder-form [type=submit]');
  button.disabled=true;
  $('#finder-summary').innerHTML='';
  $('#finder-results').innerHTML='<div class="loading">正在搜索 '+(source==='all'?finder.sources.length:1)+' 个下载源…</div>';
  try{
    const results=await api('/anime/search?'+params);
    if(token!==finder.request||!$('#finder-results'))return;
    finder.results={q,results};
    renderResults();
  }catch(error){if(token===finder.request&&$('#finder-results'))$('#finder-results').innerHTML='<div class="callout error">'+esc(error.message)+'</div>';}
  finally{if(button.isConnected)button.disabled=false;}
}
function renderResults(){
  const results=finder.results.results;
  $('#finder-summary').innerHTML=results.map((r,i)=>'<div class="finder-source'+(r.error?' failed':'')+'" data-source="'+esc(r.sourceId)+'"><div><strong>'+esc(r.sourceName)+'</strong><span>'+(r.error?esc(r.error):r.items.length+' 条结果')+'</span></div>'+(!r.error&&r.subscribeRssUrl?'<button type="button" class="compact" data-subscribe="'+i+'" title="以此搜索结果创建订阅，自动下载之后发布的新资源">＋ 订阅</button>':'')+'</div>').join('')+'<button type="button" class="compact quiet" id="finder-refresh">↻ 重新搜索</button>';
  const rows=results.flatMap(r=>r.items.map(item=>({item,source:r}))).sort((a,b)=>(Date.parse(b.item.published)||0)-(Date.parse(a.item.published)||0));
  $('#finder-results').innerHTML=rows.length?'<div class="panel finder-list">'+rows.slice(0,MAX_ROWS).map((row,i)=>{
    const x=row.item;const meta=[bytes(x.size)];
    if(x.seeders!=null)meta.push('做种 '+x.seeders+(x.leechers!=null?' · 下载 '+x.leechers:''));
    if(x.published)meta.push(time(x.published));
    if(x.category)meta.push(x.category);
    return '<div class="finder-row"><div class="finder-main"><div class="finder-title"><span class="badge finder-badge">'+esc(row.source.sourceName)+'</span><span title="'+esc(x.title)+'">'+esc(x.title||'（无标题）')+'</span></div><div class="finder-meta">'+meta.map(m=>'<span>'+esc(m)+'</span>').join('')+'</div></div><div class="finder-actions">'+(/^https?:\/\//i.test(x.pageUrl||'')?'<a href="'+esc(x.pageUrl)+'" target="_blank" rel="noopener noreferrer">原帖 ↗</a>':'')+'<button type="button" class="primary compact" data-download="'+i+'">下载</button></div></div>';
  }).join('')+'</div>'+(rows.length>MAX_ROWS?'<p class="hint">共 '+rows.length+' 条，仅显示最新的 '+MAX_ROWS+' 条，请缩小关键词范围。</p>':''):'<div class="panel">'+empty('没有找到资源','换个关键词试试，例如去掉集数或画质，或切换到其他下载源。','⌕')+'</div>';
  $('#finder-results').querySelectorAll('[data-download]').forEach(b=>b.onclick=()=>addTask(rows[b.dataset.download].item.url));
  $('#finder-summary').querySelectorAll('[data-subscribe]').forEach(b=>b.onclick=()=>{const r=results[b.dataset.subscribe];editSubscription({name:finder.q+' · '+r.sourceName,url:r.subscribeRssUrl,namePath:finder.title&&finder.title===finder.q?finder.title:''});});
  $('#finder-refresh').onclick=()=>searchSources(true);
}
