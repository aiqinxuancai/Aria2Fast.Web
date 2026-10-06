// A drag starting inside a dialog must never count as a backdrop click.
export function bindBackdropClose(dialog){
  let start=null;
  let releasedOutside=false;
  const outside=e=>{
    const r=dialog.getBoundingClientRect();
    return e.target===dialog&&(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom);
  };
  const reset=()=>{start=null;releasedOutside=false;};
  dialog.addEventListener('pointerdown',e=>{
    reset();
    if(e.isPrimary&&e.button===0&&outside(e))start={id:e.pointerId,x:e.clientX,y:e.clientY};
  });
  dialog.addEventListener('pointerup',e=>{
    releasedOutside=!!start&&start.id===e.pointerId&&outside(e)&&Math.hypot(e.clientX-start.x,e.clientY-start.y)<=6;
  });
  dialog.addEventListener('click',e=>{
    const dismiss=releasedOutside&&outside(e);
    reset();
    if(dismiss)dialog.close();
  });
  dialog.addEventListener('pointercancel',reset);
  dialog.addEventListener('close',reset);
}
