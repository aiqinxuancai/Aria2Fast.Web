import {bytes} from './core.js';

export function uploadField(){
  return '<section class="task-upload" aria-label="上传种子或 Metalink 文件"><input name="file" type="file" accept=".torrent,.metalink,.meta4" hidden><div class="upload-dropzone"><span class="upload-icon" aria-hidden="true"><svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 16V3m-5 5 5-5 5 5M4 15v5a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-5"/></svg></span><div class="upload-copy"><strong>拖拽文件到这里</strong><span class="hint">支持 .torrent、.metalink、.meta4，单个文件不超过 16 MB</span></div><button type="button" data-choose-file>选择文件</button></div><div class="upload-selection" hidden><div role="status"><strong data-file-name></strong><span class="hint" data-file-size></span></div><button type="button" class="quiet" data-remove-file>移除</button></div><p class="error-text" data-file-error role="alert" hidden></p><p class="hint upload-note">选择文件后，点击「开始下载」提交；本次将使用文件创建任务。</p></section>';
}

export function bindUpload(form){
  const area=form.querySelector('.task-upload');
  const input=form.elements.file;
  const selection=area.querySelector('.upload-selection');
  const error=area.querySelector('[data-file-error]');
  const choose=area.querySelector('[data-choose-file]');
  let selected=null,depth=0;
  function render(){
    selection.hidden=!selected;
    area.querySelector('[data-file-name]').textContent=selected?.name||'';
    area.querySelector('[data-file-size]').textContent=selected?bytes(selected.size):'';
    choose.textContent=selected?'更换文件':'选择文件';
  }
  function select(files){
    let message='';
    if(files.length!==1)message='请一次选择一个种子或 Metalink 文件';
    else if(!/\.(torrent|metalink|meta4)$/i.test(files[0].name))message='仅支持 .torrent、.metalink 或 .meta4 文件';
    else if(!files[0].size||files[0].size>16*1024*1024)message='请选择非空且不超过 16 MB 的文件';
    error.textContent=message;error.hidden=!message;
    if(!message)selected=files[0];
    input.value='';render();
  }
  choose.onclick=()=>input.click();
  input.onchange=()=>{if(input.files.length)select([...input.files]);};
  area.querySelector('[data-remove-file]').onclick=()=>{selected=null;input.value='';error.hidden=true;render();choose.focus();};
  // Handle drops throughout the form so the browser never opens a dropped file.
  const isFile=event=>Array.from(event.dataTransfer?.types||[]).includes('Files');
  form.addEventListener('dragenter',event=>{if(isFile(event)){event.preventDefault();depth++;area.classList.add('dragging');}});
  form.addEventListener('dragover',event=>{if(isFile(event)){event.preventDefault();event.dataTransfer.dropEffect='copy';}});
  form.addEventListener('dragleave',()=>{if(depth>0&&!--depth)area.classList.remove('dragging');});
  form.addEventListener('drop',event=>{
    if(!isFile(event))return;
    event.preventDefault();depth=0;area.classList.remove('dragging');select([...event.dataTransfer.files]);
  });
  return ()=>selected;
}
