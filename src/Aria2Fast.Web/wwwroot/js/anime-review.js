import {esc,api,run,toast} from './core.js';

function reviewSummary(review){
  const text=[review.recommendation,review.review||review.overview].filter(Boolean).join(' ').replace(/\s+/g,' ').trim();
  const chars=Array.from(text||'暂无简要结论，请展开查看调查信息。');
  if(chars.length<=180)return chars.join('');
  const excerpt=chars.slice(0,179).join('');
  const sentence=Math.max(excerpt.lastIndexOf('。'),excerpt.lastIndexOf('！'),excerpt.lastIndexOf('？'));
  return sentence>=80?excerpt.slice(0,sentence+1):excerpt+'…';
}

function reviewMarkup(review){
  if(!review)return '<p class="hint">调查番剧背景、原作与动画改编信息，结合来源生成评析。完成后自动保存，可随时重新调查。</p>';
  const sections=[['作品概况',review.overview],['原作信息',review.originalWork],['动画与改编',review.adaptation],['综合评析',review.review],['观看建议',review.recommendation],['待核实与局限',review.caveats]];
  const references=(review.references||[]).filter(source=>{try{const url=new URL(source.url);return ['http:','https:'].includes(url.protocol)&&!url.username&&!url.password;}catch{return false;}});
  return '<p class="review-summary">'+esc(reviewSummary(review))+'</p><details class="review-full"><summary><span class="review-expand">展开完整评析</span><span class="review-collapse">收起完整评析</span></summary><div class="review-body"><div class="hint">已保存 · '+esc(new Date(review.createdAt).toLocaleString('zh-CN'))+(review.model?' · '+esc(review.model):'')+'</div><p class="review-score">'+(review.score==null?'资料不足，暂不评分':'AI 推荐分 '+esc(review.score)+' / 10')+'</p>'+
    (review.warnings||[]).map(w=>'<p class="callout">'+esc(w)+'</p>').join('')+
    sections.filter(([,text])=>text).map(([title,text])=>'<section><h4>'+title+'</h4><p class="pre">'+esc(text)+'</p></section>').join('')+
    (references.length?'<details class="review-sources" open><summary>参考来源（'+references.length+'）</summary><ul>'+references.map(s=>'<li><a href="'+esc(s.url)+'" target="_blank" rel="noopener noreferrer">['+esc(s.id)+'] '+esc(s.title||s.url)+'</a></li>').join('')+'</ul></details>':review.sources?'<details><summary>参考来源</summary><p class="pre">'+esc(review.sources)+'</p></details>':'')+
    (review.queries?.length?'<details><summary>调查记录（'+review.queries.length+' 次搜索）</summary><ul>'+review.queries.map(q=>'<li>'+esc(q)+'</li>').join('')+'</ul></details>':'')+'</div></details>';
}

const localTasks=new Map();
let remoteTasks=new Map();
const watched=new Map();
let monitoring=false,inFlight=null,initialized=false;
const emit=()=>window.dispatchEvent(new Event('review-progress'));
export function pollReviews(){
  if(inFlight)return inFlight;
  inFlight=(async()=>{
    const tasks=await api('/anime/review-tasks');
    remoteTasks=new Map(tasks.map(task=>[task.animeId,task]));
    for(const task of tasks){
      const previous=watched.get(task.id);
      const local=localTasks.get(task.animeId);
      if(local&&!local.running&&local.remoteId!==task.id)localTasks.delete(task.animeId);
      if(initialized&&task.status!=='running'&&previous!=='completed'&&previous!=='failed'&&!localTasks.has(task.animeId))
        toast('【'+task.name+'】'+task.progress,task.status==='failed');
      watched.set(task.id,task.status);
    }
    initialized=true;emit();
  })().finally(()=>{inFlight=null;});
  return inFlight;
}
export function monitorReviews(enabled){
  monitoring=enabled;
  if(enabled)pollReviews().catch(()=>{});
  else{remoteTasks.clear();watched.clear();initialized=false;}
}
setInterval(()=>{if(monitoring)pollReviews().catch(()=>{});},2000);

export function bindAnimeReview(id,review,button,container,autoReview){
  let current=localTasks.get(id)?.result||review;
  let checking=true;
  container.classList.add('anime-review');
  const render=()=>{
    if(!container.isConnected){window.removeEventListener('review-progress',render);return;}
    const local=localTasks.get(id),remote=remoteTasks.get(id);
    if(local?.result)current=local.result;
    const busy=checking||local?.running||remote?.status==='running';
    button.disabled=!!busy;
    button.textContent=busy?(checking?'检查调查状态…':'正在调查…'):current?'↻ 重新调查':'✧ 调查并评析';
    container.toggleAttribute('aria-busy',!!busy);
    if(busy)container.setAttribute('aria-busy','true');
    const expanded=[...container.querySelectorAll('details')].map(element=>element.open);
    const markup=reviewMarkup(current);
    if(container.dataset.markup!==markup){
      container.innerHTML=markup;
      container.dataset.markup=markup;
      container.querySelectorAll('details').forEach((element,index)=>{if(index<expanded.length)element.open=expanded[index];});
    }
    container.querySelector(':scope > [role="status"]')?.remove();
    const message=busy?(remote?.status==='running'?remote.progress:'正在调查番剧与原作资料…'):local?.error;
    if(message){const status=document.createElement('p');status.className='callout'+(!busy?' error':'');status.setAttribute('role','status');status.textContent=message+(!busy&&current?' 已保留上次保存的评析。':'');container.prepend(status);}
    if(!busy&&remote?.status==='completed'&&!local?.result&&remote.id!==container.dataset.loadedReview){
      container.dataset.loadedReview=remote.id;
      api('/anime/'+encodeURIComponent(id)+'/review','POST').then(result=>{current=result;render();}).catch(()=>{delete container.dataset.loadedReview;});
    }
  };
  window.addEventListener('review-progress',render);
  const investigate=async(refresh)=>{
    if(button.disabled)return;
    const title=document.querySelector('#modal-title')?.textContent||'番剧';
    const task={running:true,result:current,error:null};localTasks.set(id,task);emit();
    try{
      task.result=await api('/anime/'+encodeURIComponent(id)+'/review'+(refresh?'?refresh=true':''),'POST');
      toast('【'+title+'】调查与评析已完成并保存');
    }catch(error){task.error=error.message;toast('调查与评析失败：'+error.message,true);}
    finally{await pollReviews().catch(()=>{});task.remoteId=remoteTasks.get(id)?.id;task.running=false;emit();}
  };
  button.onclick=()=>investigate(!!current);
  render();
  pollReviews().then(()=>{checking=false;render();if(autoReview&&!current&&!button.disabled)run(()=>investigate(false));}).catch(()=>{checking=false;render();});
}
