import {$,esc,api,toast,modal} from './core.js';

// Helpers shared by the media library and the player: labels, external players and M3U playlists.
const pad=n=>Number.isInteger(n)?String(n).padStart(2,'0'):String(n);
export const episodeNumber=e=>e.number==null?'—':(e.kind==='Special'?'SP'+pad(e.number):pad(e.number));
export function episodeLabel(e){
  if(e.kind==='Extra')return '花絮';
  if(e.number==null)return e.kind==='Special'?'特别篇':'未识别集数';
  return e.kind==='Special'?'SP '+pad(e.number):'第 '+e.number+' 集';
}
export function clock(seconds){
  const s=Math.max(0,Math.floor(seconds||0));const h=Math.floor(s/3600),m=Math.floor(s%3600/60),r=s%60;
  return (h?h+':'+String(m).padStart(2,'0'):m)+':'+String(r).padStart(2,'0');
}
export const percent=e=>e.duration>0?Math.min(100,Math.round(e.position/e.duration*100)):0;
/** Same rule as the server: past 10 seconds and before the credits, playback resumes where it stopped. */
export const resumable=e=>e.position>10&&(!e.duration||e.position<Math.min(e.duration*.9,e.duration-15));
export const episodeTitle=e=>e.number==null?e.name:episodeLabel(e)+(e.title?' · '+e.title:'');
// Missing or broken artwork falls back to the title card; inline onerror handlers are blocked by the CSP.
document.addEventListener('error',e=>{
  const img=e.target;
  if(!(img instanceof HTMLImageElement))return;
  // Collages and thumbnails keep their plain background.
  if(img.closest('.tile-art,.thumb-art')){img.remove();return;}
  if(!img.closest('.library-card .poster,.library-hero .poster'))return;
  const title=img.closest('.library-card,.library-hero')?.querySelector('h3,h1')?.textContent||'';
  const card=document.createElement('div');card.className='poster-fallback';
  card.innerHTML='<strong>'+esc(title)+'</strong>';
  img.replaceWith(card);
},true);

// Players that register a URL handler. Desktop VLC and mpv have none; they open the M3U playlist instead.
const agent=navigator.userAgent;
export const platform=/android/i.test(agent)?'android':/iphone|ipad|ipod/i.test(agent)||(/macintosh/i.test(agent)&&navigator.maxTouchPoints>1)?'ios':/macintosh|mac os x/i.test(agent)?'mac':/windows/i.test(agent)?'windows':'other';
const intent=pkg=>(url,name)=>{const u=new URL(url);return 'intent://'+u.host+u.pathname+u.search+'#Intent;scheme='+u.protocol.slice(0,-1)+';type=video/*;package='+pkg+';S.title='+encodeURIComponent(name)+';end';};
const PLAYERS=[
  {name:'PotPlayer',on:['windows'],href:u=>'potplayer://'+u},
  {name:'IINA',on:['mac'],href:u=>'iina://weblink?url='+encodeURIComponent(u)},
  {name:'Infuse',on:['mac','ios'],href:u=>'infuse://x-callback-url/play?url='+encodeURIComponent(u)},
  {name:'VLC',on:['ios'],href:u=>'vlc-x-callback://x-callback-url/stream?url='+encodeURIComponent(u)},
  {name:'VLC',on:['android'],href:intent('org.videolan.vlc')},
  {name:'MX Player',on:['android'],href:intent('com.mxtech.videoplayer.ad')},
  {name:'nPlayer',on:['ios','android'],href:u=>'nplayer-'+u}
];

const links=new Map();
/** Signed link valid for 24 hours; reused while at least an hour is left. */
export async function mediaLink(id){
  const hit=links.get(id);
  if(hit&&hit.expires>Date.now()+3600e3)return hit.url;
  const r=await api('/library/episodes/'+encodeURIComponent(id)+'/link','POST');
  const url=location.origin+r.url;links.set(id,{url,expires:Date.parse(r.expiresAt)});return url;
}

export const externalMenu=(id,name)=>'<details class="sub-more popup ext-menu" data-external="'+esc(id)+'" data-name="'+esc(name)+'"><summary>外部播放器</summary><div class="sub-menu ext-list"><p class="hint">正在生成播放链接…</p></div></details>';

/** The link is fetched when the menu opens, so tapping a player opens it straight away (iOS blocks handlers opened after a delay). */
export function bindExternal(root){
  root.querySelectorAll('details[data-external]').forEach(menu=>menu.addEventListener('toggle',()=>{if(menu.open&&!menu.dataset.ready)fill(menu);}));
}
async function fill(menu){
  const list=menu.querySelector('.ext-list');const name=menu.dataset.name;
  try{
    const url=await mediaLink(menu.dataset.external);
    list.innerHTML=PLAYERS.filter(p=>p.on.includes(platform)).map(p=>'<a href="'+esc(p.href(url,name))+'">'+esc(p.name)+'</a>').join('')+'<button type="button" data-copy>复制播放链接</button><button type="button" data-m3u>下载 M3U（VLC、mpv 等）</button><p class="hint">链接 24 小时内有效，打开无需登录；修改访问密码后全部失效。</p>';
    list.querySelector('[data-copy]').onclick=()=>copyLink(url);
    list.querySelector('[data-m3u]').onclick=()=>downloadText(safeName(name.replace(/\.[^.]+$/,''))+'.m3u8',m3u([{title:name,url}]));
    menu.dataset.ready='1';
  }catch(error){list.innerHTML='<p class="hint error-text">'+esc(error.message)+'</p>';}
}

export async function copyLink(url){
  try{await navigator.clipboard.writeText(url);toast('播放链接已复制，24 小时内有效');}
  catch{
    // The clipboard API needs HTTPS (or localhost).
    modal('播放链接','<div class="stack"><p class="hint">浏览器不允许直接复制（未使用 HTTPS 访问时常见），请手动复制：</p><textarea readonly rows="4" class="link-box">'+esc(url)+'</textarea></div>');
    const box=$('#modal-body textarea');box.focus();box.select();
  }
}

export const m3u=items=>'#EXTM3U\n'+items.map(i=>'#EXTINF:-1,'+String(i.title).replace(/[\r\n]+/g,' ')+'\n'+i.url).join('\n')+'\n';
export const safeName=value=>String(value).replace(/[\\/:*?"<>|\r\n]+/g,' ').trim().slice(0,80)||'playlist';
export function downloadText(name,text){
  const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([text],{type:'audio/x-mpegurl;charset=utf-8'}));a.download=name;
  document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),60000);
}
export async function seriesPlaylist(id){
  const r=await api('/library/'+encodeURIComponent(id)+'/links','POST');
  if(!r.episodes.length)throw new Error('没有可以播放的视频');
  downloadText(safeName(r.title)+'.m3u8',m3u(r.episodes.map(e=>({title:episodeLabel(e)+(e.title?' '+e.title:'')+' · '+e.name,url:location.origin+e.url}))));
  toast('播放列表已下载，其中的链接 24 小时内有效');
}

// Pop-up menus close on an outside click, after choosing an item, and on Escape.
document.addEventListener('click',e=>document.querySelectorAll('details.popup[open]').forEach(menu=>{if(!menu.contains(e.target)||e.target.closest('.sub-menu a,.sub-menu button'))menu.open=false;}));
document.addEventListener('keydown',e=>{if(e.key!=='Escape')return;document.querySelectorAll('details.popup[open]').forEach(menu=>{menu.open=false;menu.querySelector('summary').focus();});});
