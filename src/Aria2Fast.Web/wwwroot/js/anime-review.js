import {esc,api,run} from './core.js';

function reviewMarkup(review){
  if(!review)return '<p class="hint">调查番剧背景、原作与动画改编信息，结合来源生成评析。完成后自动保存，可随时重新调查。</p>';
  const sections=[['作品概况',review.overview],['原作信息',review.originalWork],['动画与改编',review.adaptation],['综合评析',review.review],['观看建议',review.recommendation],['待核实与局限',review.caveats]];
  const references=(review.references||[]).filter(source=>{try{const url=new URL(source.url);return ['http:','https:'].includes(url.protocol)&&!url.username&&!url.password;}catch{return false;}});
  return '<div class="hint">已保存 · '+esc(new Date(review.createdAt).toLocaleString('zh-CN'))+(review.model?' · '+esc(review.model):'')+'</div><p class="review-score">'+(review.score==null?'资料不足，暂不评分':'AI 推荐分 '+esc(review.score)+' / 10')+'</p>'+
    (review.warnings||[]).map(w=>'<p class="callout">'+esc(w)+'</p>').join('')+
    sections.filter(([,text])=>text).map(([title,text])=>'<section><h4>'+title+'</h4><p class="pre">'+esc(text)+'</p></section>').join('')+
    (references.length?'<details class="review-sources" open><summary>参考来源（'+references.length+'）</summary><ul>'+references.map(s=>'<li><a href="'+esc(s.url)+'" target="_blank" rel="noopener noreferrer">['+esc(s.id)+'] '+esc(s.title||s.url)+'</a></li>').join('')+'</ul></details>':review.sources?'<details><summary>参考来源</summary><p class="pre">'+esc(review.sources)+'</p></details>':'')+
    (review.queries?.length?'<details><summary>调查记录（'+review.queries.length+' 次搜索）</summary><ul>'+review.queries.map(q=>'<li>'+esc(q)+'</li>').join('')+'</ul></details>':'');
}

export function bindAnimeReview(id,review,button,container,autoReview){
  let current=review;
  container.classList.add('anime-review');
  const render=()=>{container.innerHTML=reviewMarkup(current);button.textContent=current?'↻ 重新调查':'✧ 调查并评析';};
  render();
  const investigate=async(refresh)=>{
    if(button.disabled)return;
    button.disabled=true;
    button.textContent='正在调查…';
    const status=document.createElement('p');
    status.className='callout';status.setAttribute('role','status');
    status.textContent='正在调查番剧与原作资料、核对网页并整理评析，可能需要几分钟。';
    container.prepend(status);container.setAttribute('aria-busy','true');
    try{
      current=await api('/anime/'+encodeURIComponent(id)+'/review'+(refresh?'?refresh=true':''),'POST');
      if(container.isConnected)render();
    }catch(error){
      status.classList.add('error');
      status.textContent=error.message+(current?' 已保留上次保存的评析。':' 可再次点击重试。');
    }finally{
      container.removeAttribute('aria-busy');button.disabled=false;
      button.textContent=current?'↻ 重新调查':'✧ 调查并评析';
    }
  };
  button.onclick=()=>investigate(!!current);
  if(autoReview&&!current)run(()=>investigate(false));
}
