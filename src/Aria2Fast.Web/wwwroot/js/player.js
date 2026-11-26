import {$,esc,state,api,run} from './core.js';
import {episodeLabel,episodeNumber,episodeTitle,clock,resumable,externalMenu,bindExternal} from './media.js';
import {choose,videoSupported,audioSupported,defaultAudio,attachHls,startSession,endSession,describe,codecName,channels} from './stream.js';

const PREFERENCE='aria2fast.subtitle';
const AUTO_NEXT=8;
const ENCODERS={copy:'不转码',libx264:'x264 软件编码',h264_nvenc:'NVIDIA NVENC',h264_qsv:'Intel Quick Sync',h264_vaapi:'VAAPI',h264_videotoolbox:'VideoToolbox',h264_amf:'AMD AMF'};

export async function play(id){
  let d,media;
  try{
    // Media details need ffprobe (cached after the first time); without ffmpeg the answer comes back at once.
    [d,media]=await Promise.all([api('/library/episodes/'+encodeURIComponent(id)),api('/library/episodes/'+encodeURIComponent(id)+'/media').catch(error=>({available:false,reason:error.message}))]);
  }catch(error){
    $('#content').innerHTML='<a class="back-link" href="#library">← 媒体库</a><div class="callout error">'+esc(error.message)+'<br>如果刚下载完成，媒体库可能还在扫描，请稍后刷新页面。</div>';
    return;
  }
  const info=media?.available?media.info:null;
  const e=d.episode,s=d.series;
  // A movie's videos are its parts and versions, not episodes.
  const movie=s.category==='Movie';
  const label=x=>movie?(x.title||x.name):episodeTitle(x);
  // Subtitle tracks: files beside the video, then text streams inside it (extracted on first use).
  const tracks=[...d.subtitles,...(info?info.subtitles.filter(x=>x.text).map(x=>({url:'/api/library/episodes/'+e.id+'/subtitles/embedded/'+x.index,label:x.label,language:language(x),format:'内封 '+(x.styled?'ASS':x.codec==='webvtt'?'VTT':'SRT')})):[])];
  // Burned in by ffmpeg: ASS with full styling and the file's fonts, image subtitles (PGS / DVD), sidecar ASS files.
  const burns=info?[...info.subtitles.filter(x=>x.styled||x.image).map(x=>({value:'embedded:'+x.index,label:x.label+' · '+(x.image?'图形字幕':'内封 ASS 完整特效')})),
    ...d.subtitles.map((t,i)=>({t,i})).filter(({t})=>t.format==='ASS'||t.format==='SSA').map(({t,i})=>({value:'external:'+i,label:t.label+' · 外挂 ASS 完整特效'}))]:[];
  const previous=d.episodes.find(x=>x.id===d.previous),next=d.episodes.find(x=>x.id===d.next);
  const chosen=pickTrack(tracks);
  const heights=info?.video?[1080,720,480].filter(h=>h<info.video.height&&(!media.maxHeight||h<=media.maxHeight)):[];
  $('#content').innerHTML='<a class="back-link" href="#library/'+esc(s.id)+'">← '+esc(s.title)+'</a>'+
    '<div class="player-layout"><div class="player-main"><div class="player-frame"><video id="player" controls playsinline preload="metadata">'+
    tracks.map(t=>'<track kind="subtitles" src="'+esc(t.url)+'" srclang="'+esc(t.language)+'" label="'+esc(t.label+' · '+t.format)+'">').join('')+
    '</video><div id="player-next" class="player-next" hidden></div></div>'+
    '<div class="player-bar"><div class="player-title"><div class="eyebrow">'+esc(movie?'电影':s.title+(s.season>1?' · 第 '+s.season+' 季':''))+'</div><h1>'+esc(movie?s.title:episodeTitle(e))+'</h1><p>'+esc(e.name)+'</p><p id="player-media"></p></div>'+
    '<div class="toolbar player-actions">'+nav(previous,movie?'‹ 上一个':'‹ 上一集')+nav(next,movie?'下一个 ›':'下一集 ›')+
    (tracks.length||burns.length?'<select id="player-subtitle" aria-label="字幕"><option value="-1">关闭字幕</option>'+tracks.map((t,i)=>'<option value="t'+i+'"'+(i===chosen?' selected':'')+'>'+esc(t.label+' · '+t.format)+'</option>').join('')+
      (burns.length?'<optgroup label="烧录到画面（需要转码）">'+burns.map(b=>'<option value="b:'+esc(b.value)+'">'+esc(b.label)+'</option>').join('')+'</optgroup>':'')+'</select>':'')+
    (info&&info.audio.length>1?'<select id="player-audio" aria-label="音轨">'+info.audio.map(a=>'<option value="'+a.index+'"'+(a===defaultAudio(info)?' selected':'')+'>'+esc(a.label+' · '+codecName(a.codec)+' '+channels(a.channels))+'</option>').join('')+'</select>':'')+
    (info?'<select id="player-mode" aria-label="播放方式"><option value="auto">自动选择播放方式</option><option value="direct">直接播放原文件</option>'+(videoSupported(info.video)?'<option value="copy">转封装（画面不转码）</option>':'')+
      '<option value="transcode">转码 · '+(media.maxHeight&&info.video&&info.video.height>media.maxHeight?media.maxHeight+'p':'原始分辨率')+'</option>'+heights.filter(h=>h<(media.maxHeight||9999)).map(h=>'<option value="transcode-'+h+'">转码 · '+h+'p</option>').join('')+'</select>':'')+
    '<button type="button" id="player-watched" aria-pressed="'+e.watched+'">'+(e.watched?'✓ 已看':'标为已看')+'</button>'+externalMenu(e.id,e.name)+'</div></div>'+
    '<div id="player-notice"></div><p class="hint player-keys">快捷键：空格 播放 / 暂停 · ← / → 后退 / 前进 10 秒 · F 全屏 · M 静音 · N 下一集</p></div>'+
    '<aside class="panel player-list"><div class="panel-head"><h2>'+(movie?'视频文件':'剧集')+'</h2><span class="muted">'+d.episodes.length+' 个视频</span></div><div class="player-episodes">'+d.episodes.map(x=>'<a href="#play/'+esc(x.id)+'"'+(x.id===e.id?' class="active" aria-current="true"':'')+'><span class="episode-no">'+(movie?'▶':esc(episodeNumber(x)))+'</span><span><b>'+esc(movie?x.name:x.title||(x.number==null?x.name:episodeLabel(x)))+'</b><small>'+(x.watched?'✓ 已看':resumable(x)?'看到 '+clock(x.position):x.kind==='Extra'?esc(x.name):'未看')+'</small></span></a>').join('')+'</div></aside></div>';
  bindExternal($('#content'));
  const list=$('.player-episodes'),active=list.querySelector('.active');
  if(active)list.scrollTop=active.offsetTop-list.clientHeight/3;

  const video=$('#player');
  let started=false,left=false,saved=0,countdown=0;
  // Events of the old video can still arrive after leaving for the next episode.
  const notice=(html,error=false)=>{if(left)return;$('#player-notice').innerHTML=html?'<div class="callout'+(error?' error':'')+'">'+html+'</div>':'';};
  showTrack(video,chosen);

  // Progress is saved every 10 seconds of playback, on pause, when the tab is hidden and when leaving.
  const save=(extra={},keepalive=false)=>{
    // readyState 0: the source is being replaced and the position reads 0.
    if(left||(!started&&!extra.watched)||!video.readyState)return;
    fetch('/api/library/episodes/'+encodeURIComponent(e.id)+'/progress',{method:'POST',keepalive,headers:{'X-Aria2Fast':'1','Content-Type':'application/json'},
      body:JSON.stringify({position:video.currentTime||0,duration:Number.isFinite(video.duration)?video.duration:0,...extra})}).catch(()=>{});
  };
  const watchedButton=$('#player-watched');
  const markButton=on=>{watchedButton.setAttribute('aria-pressed',String(on));watchedButton.textContent=on?'✓ 已看':'标为已看';};
  watchedButton.onclick=()=>run(async()=>{const on=watchedButton.getAttribute('aria-pressed')!=='true';await api('/library/episodes/'+e.id+'/watched','POST',{watched:on});markButton(on);},watchedButton);

  // Source: the file itself, or an HLS session (remux / transcode). Switching keeps the position.
  const prefs={mode:'auto',audio:info?defaultAudio(info)?.index??null:null,burn:null};
  let controller=null,session=null,current=null,loads=0,pendingAt=0,fellBack=false;
  const resolve=()=>{
    if(!info)return {mode:'direct'};
    if(prefs.burn)return {mode:'transcode',height:0,burn:prefs.burn};
    if(prefs.mode==='direct')return {mode:'direct'};
    if(prefs.mode==='copy')return {mode:'copy',audioCopy:audioSupported(info.audio.find(a=>a.index===prefs.audio))};
    if(prefs.mode.startsWith('transcode'))return {mode:'transcode',height:Number(prefs.mode.split('-')[1]||0)};
    return choose(info,prefs.audio);
  };
  const showMode=(plan,created)=>{
    const how=plan.mode==='direct'?'直接播放':plan.mode==='copy'?'转封装（画面不转码'+(created&&!created.audioCopy?'，音频转为 AAC':'')+'）'
      :'转码'+(created?' '+created.height+'p · '+(ENCODERS[created.encoder]||created.encoder):'')+(plan.burn?' · 烧录字幕':'');
    $('#player-media').textContent=(info?describe(info)+' · ':'')+how;
  };
  const teardown=()=>{
    controller?.destroy();controller=null;
    endSession(session);session=null;
    video.pause();video.removeAttribute('src');video.load();
  };
  async function load(at){
    const plan=resolve(),token=++loads;
    teardown();
    current=plan;pendingAt=at;
    showMode(plan);
    if(plan.mode==='direct'){video.src=d.stream;return true;}
    notice('正在准备'+(plan.mode==='copy'?'转封装':'转码')+'，第一次打开或拖动进度后需要几秒钟…');
    try{
      const created=await startSession(e.id,{mode:plan.mode,audio:prefs.audio,audioCopy:!!plan.audioCopy,height:plan.height||0,burn:plan.burn||null,start:at||0});
      if(token!==loads||left){endSession(created.id);return false;}
      session=created.id;showMode(plan,created);
      controller=await attachHls(video,created.playlist,at,message=>{if(token===loads)notice('播放失败：'+esc(message)+'。可以换一种播放方式，或用外部播放器打开。',true);});
      return true;
    }catch(error){if(token===loads)notice(esc(error.message),true);return false;}
  }
  // The browser could not play the file after all: transcode instead, once, if ffmpeg is there.
  const failDirect=message=>{
    if(info&&!fellBack&&prefs.mode==='auto'){fellBack=true;prefs.mode='transcode';const at=video.currentTime||pendingAt;notice(message+'已自动改用转码播放。');load(at);if($('#player-mode'))$('#player-mode').value='transcode';}
    else notice(message+'请'+(info?'换一种播放方式，或':'')+'点上方「外部播放器」用本机播放器打开。',true);
  };

  video.addEventListener('playing',()=>{started=true;if(current?.mode!=='direct'&&/正在准备/.test($('#player-notice').textContent))notice('');});
  video.addEventListener('timeupdate',()=>{
    if(!started||video.paused||Math.abs(video.currentTime-saved)<10)return;
    saved=video.currentTime;save();
    if(video.duration&&video.currentTime>=video.duration*.9)markButton(true);
  });
  video.addEventListener('pause',()=>{if(!video.ended)save();});
  video.addEventListener('ended',()=>{save({watched:true});markButton(true);if(next)autoNext();});
  video.addEventListener('loadedmetadata',()=>{
    if(left||!current)return;
    // HLS starts at the requested position by itself.
    if(current.mode==='direct'&&pendingAt>0&&pendingAt<video.duration)video.currentTime=pendingAt;
    pendingAt=0;
    // Chrome plays the sound of videos whose picture it cannot decode (often HEVC) without raising an error.
    if(current.mode==='direct'&&!video.videoWidth&&!video.videoHeight)return failDirect('浏览器只能播放这个视频的声音，画面编码不受支持（常见于 HEVC / H.265）。');
    video.play().catch(()=>{});
  });
  video.addEventListener('error',()=>{
    if(left||!video.error||!current)return;
    const code=video.error.code;
    if(current.mode==='direct')failDirect(code===4?'浏览器不支持这个视频的格式或编码（MKV 封装、HEVC / H.265、10bit、FLAC 音轨等较常见）。':code===2?'视频加载中断。':'视频解码失败。');
  });

  const subtitle=$('#player-subtitle');
  if(subtitle)subtitle.onchange=()=>{
    const value=subtitle.value;
    if(value.startsWith('b:')){prefs.burn=value.slice(2);showTrack(video,-1);load(video.currentTime);return;}
    const i=value==='-1'?-1:Number(value.slice(1));
    if(prefs.burn){prefs.burn=null;load(video.currentTime);}
    showTrack(video,i);
    localStorage.setItem(PREFERENCE,i<0?'off':JSON.stringify({language:tracks[i].language,label:tracks[i].label}));
  };
  const audio=$('#player-audio');
  if(audio)audio.onchange=()=>{prefs.audio=Number(audio.value);if(prefs.mode==='direct'){prefs.mode='auto';$('#player-mode').value='auto';}load(video.currentTime);};
  const mode=$('#player-mode');
  if(mode)mode.onchange=()=>{prefs.mode=mode.value;fellBack=true;load(video.currentTime);};

  const resumeAt=resumable(e)?e.position:0;
  load(resumeAt).then(ok=>{
    if(!ok||!resumeAt)return;
    notice('已从 '+clock(resumeAt)+' 继续播放。<button type="button" class="text-button" id="player-restart">从头播放</button>');
    const restart=$('#player-restart');
    if(restart)restart.onclick=()=>{video.currentTime=0;notice('');video.play().catch(()=>{});};
  });

  function autoNext(){
    let remaining=AUTO_NEXT;
    const box=$('#player-next');box.hidden=false;
    box.innerHTML='<div><small>即将播放</small><strong>'+esc(label(next))+'</strong><span id="next-count">'+remaining+' 秒后自动播放</span></div><div class="toolbar"><a class="button primary" href="#play/'+esc(next.id)+'">立即播放</a><button type="button" id="next-cancel">取消</button></div>';
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
    save({},true);left=true;loads++;clearInterval(countdown);
    document.removeEventListener('keydown',keys);document.removeEventListener('visibilitychange',hidden);window.removeEventListener('pagehide',hidden);
    if(document.fullscreenElement)document.exitFullscreen().catch(()=>{});
    // Stop buffering, and let the server stop converting.
    teardown();
  };
}

const nav=(target,label)=>target?'<a class="button" href="#play/'+esc(target.id)+'" title="'+esc(episodeTitle(target))+'">'+label+'</a>':'<a class="button" aria-disabled="true">'+label+'</a>';

/** srclang for an embedded stream, from its title (which fansubs fill) or its language tag. */
function language(stream){
  if(/简|chs|sc\b/i.test(stream.label))return 'zh-Hans';
  if(/繁|cht|tc\b/i.test(stream.label))return 'zh-Hant';
  return ({chi:'zh',zho:'zh',jpn:'ja',eng:'en',kor:'ko'})[stream.language]||'und';
}

/** The remembered subtitle language, else the first track (Simplified Chinese sidecar files are listed first). */
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
