import {$,esc,state,api,run} from './core.js';
import {episodeLabel,episodeNumber,episodeTitle,clock,resumable,externalMenu,bindExternal} from './media.js';

const PREFERENCE='aria2fast.subtitle';
const AUTO_NEXT=8;

export async function play(id){
  let d;
  try{d=await api('/library/episodes/'+encodeURIComponent(id));}
  catch(error){
    $('#content').innerHTML='<a class="back-link" href="#library">← 媒体库</a><div class="callout error">'+esc(error.message)+'<br>如果刚下载完成，媒体库可能还在扫描，请稍后刷新页面。</div>';
    return;
  }
  const e=d.episode,s=d.series;
  const tracks=d.subtitles;
  const previous=d.episodes.find(x=>x.id===d.previous),next=d.episodes.find(x=>x.id===d.next);
  const title=episodeTitle(e);
  const chosen=pickTrack(tracks);
  $('#content').innerHTML='<a class="back-link" href="#library/'+esc(s.id)+'">← '+esc(s.title)+'</a>'+
    '<div class="player-layout"><div class="player-main"><div class="player-frame"><video id="player" controls playsinline preload="metadata" src="'+esc(d.stream)+'">'+
    tracks.map(t=>'<track kind="subtitles" src="'+esc(t.url)+'" srclang="'+esc(t.language)+'" label="'+esc(t.label+' · '+t.format)+'">').join('')+
    '</video><div id="player-next" class="player-next" hidden></div></div>'+
    '<div class="player-bar"><div class="player-title"><div class="eyebrow">'+esc(s.title+(s.season>1?' · 第 '+s.season+' 季':''))+'</div><h1>'+esc(title)+'</h1><p>'+esc(e.name)+'</p></div>'+
    '<div class="toolbar player-actions">'+nav(previous,'‹ 上一集')+nav(next,'下一集 ›')+
    (tracks.length?'<select id="player-subtitle" aria-label="字幕"><option value="-1">关闭字幕</option>'+tracks.map((t,i)=>'<option value="'+i+'"'+(i===chosen?' selected':'')+'>'+esc(t.label+' · '+t.format)+'</option>').join('')+'</select>':'')+
    '<button type="button" id="player-watched" aria-pressed="'+e.watched+'">'+(e.watched?'✓ 已看':'标为已看')+'</button>'+externalMenu(e.id,e.name)+'</div></div>'+
    '<div id="player-notice"></div><p class="hint player-keys">快捷键：空格 播放 / 暂停 · ← / → 后退 / 前进 10 秒 · F 全屏 · M 静音 · N 下一集</p></div>'+
    '<aside class="panel player-list"><div class="panel-head"><h2>剧集</h2><span class="muted">'+d.episodes.length+' 个视频</span></div><div class="player-episodes">'+d.episodes.map(x=>'<a href="#play/'+esc(x.id)+'"'+(x.id===e.id?' class="active" aria-current="true"':'')+'><span class="episode-no">'+esc(episodeNumber(x))+'</span><span><b>'+esc(x.title||(x.number==null?x.name:episodeLabel(x)))+'</b><small>'+(x.watched?'✓ 已看':resumable(x)?'看到 '+clock(x.position):x.kind==='Extra'?esc(x.name):'未看')+'</small></span></a>').join('')+'</div></aside></div>';
  bindExternal($('#content'));
  const list=$('.player-episodes'),active=list.querySelector('.active');
  if(active)list.scrollTop=active.offsetTop-list.clientHeight/3;

  const video=$('#player');
  let started=false,left=false,saved=0,countdown=0;
  // Events of the old video can still arrive after leaving for the next episode.
  const notice=(html,error=false)=>{if(left)return;$('#player-notice').innerHTML='<div class="callout'+(error?' error':'')+'">'+html+'</div>';};
  showTrack(video,chosen);
  const select=$('#player-subtitle');
  if(select)select.onchange=()=>{const i=Number(select.value);showTrack(video,i);localStorage.setItem(PREFERENCE,i<0?'off':JSON.stringify({language:tracks[i].language,label:tracks[i].label}));};

  // Progress is saved every 10 seconds of playback, on pause, when the tab is hidden and when leaving.
  const save=(extra={},keepalive=false)=>{
    if(left||(!started&&!extra.watched))return;
    fetch('/api/library/episodes/'+encodeURIComponent(e.id)+'/progress',{method:'POST',keepalive,headers:{'X-Aria2Fast':'1','Content-Type':'application/json'},
      body:JSON.stringify({position:video.currentTime||0,duration:Number.isFinite(video.duration)?video.duration:0,...extra})}).catch(()=>{});
  };
  const watchedButton=$('#player-watched');
  const markButton=on=>{watchedButton.setAttribute('aria-pressed',String(on));watchedButton.textContent=on?'✓ 已看':'标为已看';};
  watchedButton.onclick=()=>run(async()=>{const on=watchedButton.getAttribute('aria-pressed')!=='true';await api('/library/episodes/'+e.id+'/watched','POST',{watched:on});markButton(on);},watchedButton);

  video.addEventListener('playing',()=>{started=true;});
  video.addEventListener('timeupdate',()=>{
    if(!started||video.paused||Math.abs(video.currentTime-saved)<10)return;
    saved=video.currentTime;save();
    if(video.duration&&video.currentTime>=video.duration*.9)markButton(true);
  });
  video.addEventListener('pause',()=>{if(!video.ended)save();});
  video.addEventListener('ended',()=>{save({watched:true});markButton(true);if(next)autoNext();});
  video.addEventListener('loadedmetadata',()=>{
    if(resumable(e)&&e.position<video.duration){
      video.currentTime=e.position;
      notice('已从 '+clock(e.position)+' 继续播放。<button type="button" class="text-button" id="player-restart">从头播放</button>');
      $('#player-restart').onclick=()=>{video.currentTime=0;$('#player-notice').innerHTML='';video.play().catch(()=>{});};
    }
    // Chrome plays the sound of videos whose picture it cannot decode (often HEVC) without raising an error.
    if(!video.videoWidth&&!video.videoHeight)notice('浏览器只能播放这个视频的声音，画面编码不受支持（常见于 HEVC / H.265）。请点上方「外部播放器」用本机播放器打开。',true);
    video.play().catch(()=>{});
  },{once:true});
  video.addEventListener('error',()=>{
    const code=video.error?.code;
    notice((code===4?'浏览器不支持这个视频的格式或编码（MKV 封装、HEVC / H.265、10bit、FLAC 音轨等较常见）。':code===2?'视频加载中断，请检查网络后刷新重试。':'视频解码失败。')+'请点上方「外部播放器」用本机播放器打开。',true);
  });

  function autoNext(){
    let remaining=AUTO_NEXT;
    const box=$('#player-next');box.hidden=false;
    box.innerHTML='<div><small>即将播放</small><strong>'+esc(episodeTitle(next))+'</strong><span id="next-count">'+remaining+' 秒后自动播放</span></div><div class="toolbar"><a class="button primary" href="#play/'+esc(next.id)+'">立即播放</a><button type="button" id="next-cancel">取消</button></div>';
    countdown=setInterval(()=>{
      remaining--;
      if(remaining<=0){clearInterval(countdown);location.hash='#play/'+next.id;return;}
      const count=$('#next-count');if(count)count.textContent=remaining+' 秒后自动播放';
    },1000);
    $('#next-cancel').onclick=()=>{clearInterval(countdown);box.hidden=true;};
  }

  const keys=ev=>{
    if(ev.defaultPrevented||ev.ctrlKey||ev.metaKey||ev.altKey||$('#modal').open)return;
    const target=ev.target;
    if(target.closest?.('input,textarea,select,button,summary,a,[contenteditable]'))return;
    const onVideo=target===video,key=ev.key.toLowerCase();
    // The video's own controls already handle these keys while it has focus.
    if((key===' '||key==='k')&&!(onVideo&&key===' ')){ev.preventDefault();if(video.paused)video.play().catch(()=>{});else video.pause();}
    else if((key==='arrowleft'||key==='arrowright')&&!onVideo){ev.preventDefault();video.currentTime=Math.max(0,video.currentTime+(key==='arrowleft'?-10:10));}
    else if(key==='f'){ev.preventDefault();if(document.fullscreenElement)document.exitFullscreen();else $('.player-frame').requestFullscreen?.().catch(()=>{});}
    else if(key==='m')video.muted=!video.muted;
    else if(key==='n'&&next)location.hash='#play/'+next.id;
  };
  const hidden=()=>{if(document.visibilityState==='hidden')save({},true);};
  document.addEventListener('keydown',keys);
  document.addEventListener('visibilitychange',hidden);
  window.addEventListener('pagehide',hidden);
  state.leave=()=>{
    save({},true);left=true;clearInterval(countdown);
    document.removeEventListener('keydown',keys);document.removeEventListener('visibilitychange',hidden);window.removeEventListener('pagehide',hidden);
    if(document.fullscreenElement)document.exitFullscreen().catch(()=>{});
    // Stop buffering the old video.
    video.pause();video.removeAttribute('src');video.load();
  };
}

const nav=(target,label)=>target?'<a class="button" href="#play/'+esc(target.id)+'" title="'+esc(episodeTitle(target))+'">'+label+'</a>':'<a class="button" aria-disabled="true">'+label+'</a>';

/** The remembered subtitle language, else the first track (Simplified Chinese is listed first). */
function pickTrack(tracks){
  if(!tracks.length)return -1;
  const saved=localStorage.getItem(PREFERENCE);
  if(saved==='off')return -1;
  try{
    const pref=JSON.parse(saved||'null');
    if(pref){const exact=tracks.findIndex(t=>t.language===pref.language&&t.label===pref.label);if(exact>=0)return exact;const same=tracks.findIndex(t=>t.language===pref.language);if(same>=0)return same;}
  }catch{}
  return 0;
}

function showTrack(video,index){
  [...video.textTracks].forEach((t,i)=>t.mode=i===index?'showing':'disabled');
}
