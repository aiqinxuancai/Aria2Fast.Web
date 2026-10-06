import {$,api,toast,run} from './core.js';

export function desktopSettings(desktop){
  if(!desktop?.available)return '';
  return '<section class="settings-section" id="desktop"><div class="section-header"><h2>桌面与启动</h2></div><div class="settings-group"><label class="check"><input type="checkbox" id="desktop-autostart" '+(desktop.autoStart?'checked':'')+'>开机自动启动（登录后隐藏控制台运行）</label><p class="hint">此开关立即生效。启动完成后自动打开默认浏览器。移动程序目录后，请重新设置自启和快捷方式。</p><button type="button" id="desktop-shortcut">创建桌面快捷方式</button></div></section>';
}
export function bindDesktopSettings(){
  const toggle=$('#desktop-autostart');
  if(!toggle)return;
  toggle.onchange=()=>run(async()=>{
    const enabled=toggle.checked;
    try{const status=await api('/desktop/autostart','PUT',{enabled});toggle.checked=status.autoStart;toast(enabled?'已启用登录后自动启动':'已关闭自动启动');}
    catch(error){toggle.checked=!enabled;throw error;}
  },toggle);
  const shortcut=$('#desktop-shortcut');
  shortcut.onclick=()=>run(async()=>{await api('/desktop/shortcut','POST');toast('桌面快捷方式已创建');},shortcut);
}
