import {$,esc,state,api,toast,run,modal,heading,empty,bytes} from './core.js';
import {editSubscription} from './subscriptions.js';
import {addTask} from './downloads.js';
let cards=[];let search='';let day='全部';let badgeObserver;
const weekdays=['星期一','星期二','星期三','星期四','星期五','星期六','星期日'];
const dayOrder=value=>{const index=weekdays.indexOf(value);return index<0?7:index;};
const orderedDays=items=>[...new Set(items.map(c=>c.day||'其他'))].sort((a,b)=>dayOrder(a)-dayOrder(b));
export async function anime(){
  const now=new Date();
  $('#content').innerHTML='<div class="anime-hero"><div class="eyebrow">DISCOVER YOUR NEXT FAVORITE</div><h1>新的故事，正在发生。</h1><p>从当季新番到心仪的续作，在这里发现、订阅，静待下一集。</p></div><div class="page-heading"><div><h2>探索番剧</h2><p>Mikan 番剧日历 · 字幕组订阅 · AI 评析</p></div><div class="toolbar"><select id="anime-year" aria-label="年份">'+Array.from({length:12},(_,i)=>now.getFullYear()-i).map(y=>'<option>'+y+'</option>').join('')+'</select><select id="anime-season" aria-label="季度">'+['冬','春','夏','秋'].map((s,i)=>'<option '+(i===Math.floor(now.getMonth()/3)?'selected':'')+'>'+s+'</option>').join('')+'</select><button id="anime-load">↻ 加载季度</button></div></div><div class="panel-head" style="padding:0 0 20px;border:0"><div id="day-tabs" class="tabs"></div><input id="anime-search" type="search" class="search" placeholder="搜索作品名称…" aria-label="搜索番剧"></div><div id="anime-results"><div class="loading">正在连接 Mikan…</div></div>';
  $('#anime-search').oninput=e=>{search=e.target.value;render();};
  $('#anime-load').onclick=e=>run(()=>load(true),e.target);
  await load(false);
}
async function load(refresh){
  const year=$('#anime-year').value;const season=$('#anime-season').value;
  $('#anime-results').innerHTML='<div class="loading">正在获取季度番剧…</div>';
  try{cards=await api('/anime?year='+year+'&season='+encodeURIComponent(season)+'&refresh='+refresh);if(state.page!=='anime')return;const days=['全部',...orderedDays(cards)];if(!days.includes(day))day='全部';$('#day-tabs').innerHTML=days.map(d=>'<button data-day="'+esc(d)+'" class="'+(d===day?'active':'')+'">'+esc(d)+'</button>').join('');$('#day-tabs').onclick=e=>{const b=e.target.closest('[data-day]');if(b){day=b.dataset.day;$('#day-tabs').querySelectorAll('button').forEach(x=>x.classList.toggle('active',x===b));render();}};render();}catch(error){if(state.page==='anime')$('#anime-results').innerHTML='<div class="callout error">'+esc(error.message)+'<br>可在设置中切换 Mikan 源站、配置代理后重试。</div>';}
}
function cardMarkup(c){
  const unpublished=c.hasReleases===false;
  return '<button class="anime-card'+(unpublished?' unpublished':'')+'" '+(unpublished?'disabled aria-label="'+esc(c.name+'，暂无字幕组发布')+'"':'data-anime="'+esc(c.id)+'"')+'><div class="poster">'+(c.image?'<img loading="lazy" src="'+esc(c.image)+'" alt="'+esc(c.name)+'" referrerpolicy="no-referrer">':'')+(unpublished?'<span>暂无字幕组发布</span>':'')+'<div class="anime-badges"></div><div class="anime-episode"></div></div><h3 title="'+esc(c.name)+'">'+esc(c.name)+'</h3></button>';
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
  document.querySelectorAll('.anime-card[data-anime]').forEach(card=>badgeObserver.observe(card));
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
async function detail(id){
  modal('番剧详情','<div class="loading">获取作品与字幕组…</div>');
  let d;try{d=await api('/anime/'+id);}catch(e){$('#modal-body').innerHTML='<div class="callout error">'+esc(e.message)+'</div>';return;}
  $('#modal-title').textContent=d.name;
  $('#modal-body').innerHTML='<p class="pre" id="anime-summary">'+esc(d.summary||d.tmdb?.overview||'暂无简介')+'</p>'+(d.tmdb?'<div class="detail-metrics"><div>'+d.tmdb.score.toFixed(1)+'<small>TMDB 评分 · '+d.tmdb.voteCount+' 人</small></div><div>'+d.tmdb.popularity.toFixed(0)+'<small>TMDB 热度</small></div><div>'+esc(d.tmdb.firstAirDate||'—')+'<small>首播日期</small></div></div><p class="hint">TMDB 匹配作品：'+esc(d.tmdb.name)+'。搜索匹配可能存在同名差异。</p>':'<p class="hint">配置 TMDB API Key 后可显示真实评分、投票数与热度。</p>')+'<div class="toolbar"><button id="ai-review">✧ AI 评析</button><button id="translate-summary">翻译简介</button><a href="'+esc(cards.find(c=>c.id===id)?.url||'#')+'" target="_blank" rel="noopener noreferrer">前往 Mikan ↗</a></div><div id="ai-review-content" class="pre" style="margin-top:20px">'+(d.review?esc((d.review.score??'信息不足')+' · '+d.review.review):'')+'</div><h3 style="margin-top:25px">选择字幕组</h3>'+d.groups.map((g,i)=>'<div class="file-row"><span>'+esc(g.name)+'</span><button data-feed="'+i+'">查看更新</button><button class="primary" data-sub="'+i+'">订阅</button></div>').join('')+'<div id="anime-feed"></div>';
  const showReview=r=>{ $('#ai-review-content').textContent=(r.score===null?'资料不足':r.score+' / 10')+' · '+r.review+(r.sources?'\n\n参考资料：\n'+r.sources:''); };
  if(d.review)showReview(d.review);
  $('#ai-review').onclick=e=>run(async()=>{showReview(await api('/anime/'+id+'/review','POST'));},e.target);
  $('#translate-summary').onclick=e=>run(async()=>{const r=await api('/anime/translate','POST',{text:d.originalSummary||d.summary||d.tmdb?.overview||''});$('#anime-summary').textContent=r.text;},e.target);
  $('#modal-body').querySelectorAll('[data-sub]').forEach(b=>b.onclick=()=>editSubscription({name:d.name+' · '+d.groups[b.dataset.sub].name,url:d.groups[b.dataset.sub].url,namePath:d.name}));
  $('#modal-body').querySelectorAll('[data-feed]').forEach(b=>b.onclick=()=>run(async()=>{const result=await api('/subscriptions/preview','POST',{url:d.groups[b.dataset.feed].url});$('#anime-feed').innerHTML='<h3 style="margin-top:24px">最新发布</h3><div class="file-list">'+result.items.map((x,i)=>'<div class="file-row"><span>'+esc(x.item.title)+'<br><small class="muted">'+bytes(x.item.size)+'</small></span><button data-download="'+i+'">下载</button></div>').join('')+'</div>';$('#anime-feed').querySelectorAll('[data-download]').forEach(btn=>btn.onclick=()=>addTask(result.items[btn.dataset.download].item.url));},b));
}
