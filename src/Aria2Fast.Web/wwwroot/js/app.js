import {monitorReviews} from './anime-review.js';
import {$,state,api,run,toast,refreshConfig,heading,esc,time,empty} from './core.js';
import {downloads,addTask,refreshTasks} from './downloads.js';
import {subscriptions} from './subscriptions.js';
import {anime} from './anime.js';
import {settings} from './settings.js';
import {bindBackdropClose} from './dialog-events.js';
const names={downloads:'下载任务',subscriptions:'我的订阅',anime:'发现番剧',activity:'活动记录',settings:'设置中心'};
let authenticated=false;
async function render(){if(!authenticated)return;const page=location.hash.slice(1).split('?')[0]||'downloads';state.page=names[page]?page:'downloads';$('#breadcrumb-title').textContent=names[state.page];document.querySelectorAll('nav a').forEach(a=>a.classList.toggle('active',a.dataset.page===state.page));$('#content').innerHTML='<div class="loading">正在载入…</div>';try{if(state.page==='activity'){await refreshConfig();$('#content').innerHTML=heading('活动记录','下载、订阅、番剧调查与评析进度')+'<div class="panel">'+(state.config.notices.length?state.config.notices.map(n=>'<div class="log-row"><time>'+time(n.time)+'</time><span class="badge '+esc(n.level)+'">'+esc(({success:'成功',info:'信息',warning:'提醒',error:'失败'})[n.level]||n.level)+'</span><span>'+esc(n.message)+'</span></div>').join(''):empty('一切就绪','新的下载与订阅动态会出现在这里。','≋'))+'</div>';}else await ({downloads,subscriptions,anime,settings}[state.page])();}catch(error){$('#content').innerHTML=heading(names[state.page],'')+'<div class="callout error">'+esc(error.message)+'</div>';toast(error.message,true);}}
async function enter(){await refreshConfig();authenticated=true;monitorReviews(true);$('#login').hidden=true;$('#shell').hidden=false;await render();}
function login(){authenticated=false;monitorReviews(false);$('#shell').hidden=true;$('#login').hidden=false;$('#modal').close();$('#login-form input').focus();}
$('#login-form').addEventListener('submit',e=>{e.preventDefault();run(async()=>{try{await api('/auth/login','POST',{password:new FormData(e.target).get('password')});e.target.reset();$('#login-error').textContent='';await enter();}catch(error){$('#login-error').textContent=error.message;}},e.target.querySelector('button'));});
$('#theme').value=window.getTheme();$('#theme').addEventListener('change',e=>window.setTheme(e.target.value));
$('#modal-close').addEventListener('click',()=>$('#modal').close());
bindBackdropClose($('#modal'));
$('#quick-add').addEventListener('click',()=>addTask());
$('#logout').addEventListener('click',()=>run(async()=>{await api('/auth/logout','POST');login();}));
$('#node-select').addEventListener('change',e=>run(async()=>{await api('/nodes/'+encodeURIComponent(e.target.value)+'/select','POST');state.selected.clear();await refreshConfig();await render();}));
window.addEventListener('hashchange',()=>run(render));window.addEventListener('auth-expired',login);
let polling=false;
setInterval(async()=>{if(!authenticated||document.hidden||state.page!=='downloads'||$('#modal').open||polling)return;polling=true;try{await refreshTasks();}finally{polling=false;}},4000);
run(async()=>{const auth=await api('/auth');if(auth.authenticated)await enter();else login();});

let activityRefreshing=false;
setInterval(async()=>{if(!authenticated||state.page!=='activity'||document.hidden||activityRefreshing)return;activityRefreshing=true;try{const config=await api('/state');if(state.page==='activity'){state.config.notices=config.notices;const panel=document.querySelector('#content .panel');if(panel)panel.innerHTML=config.notices.map(n=>'<div class="log-row"><time>'+time(n.time)+'</time><span class="badge '+esc(n.level)+'">'+esc(({success:'成功',info:'信息',warning:'提醒',error:'失败'})[n.level]||n.level)+'</span><span>'+esc(n.message)+'</span></div>').join('');}}catch{}finally{activityRefreshing=false;}},2000);
