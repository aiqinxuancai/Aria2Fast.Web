const assert=require('node:assert/strict');
const {spawnSync}=require('node:child_process');
const path=require('node:path');
const root=path.resolve(__dirname,'../..');

// With ffmpeg (TEST_FFMPEG or PATH): automatic choice, switching to remux over HLS (hls.js), seeking, embedded subtitles and burn-in options.
module.exports=async(page,info)=>{
  const python=process.env.PYTHON||'python';
  const check=spawnSync(python,['tests/media_fixtures.py'],{cwd:root,encoding:'utf8',env:{...process.env,PYTHONUTF8:'1'}});
  if(check.status!==0){console.log('SKIP browser transcoding: ffmpeg / ffprobe not found');return;}
  const tools=JSON.parse(check.stdout);
  const made=spawnSync(python,['tests/media_fixtures.py',path.join(info.data,'downloads','transcode')],{cwd:root,encoding:'utf8',env:{...process.env,PYTHONUTF8:'1'},timeout:300000});
  assert.equal(made.status,0,made.stderr);
  const files=JSON.parse(made.stdout).files;
  const api=async(p,method='GET',data)=>{const r=await page.request.fetch(info.base+'/api'+p,{method,headers:{'X-Aria2Fast':'1'},data});assert(r.ok(),p+' '+r.status()+' '+await r.text());return r.json().catch(()=>null);};
  const settings=(await api('/state')).settings;
  await api('/settings','PUT',{...settings,library:{...settings.library,transcoding:true,ffmpegPath:path.dirname(tools.ffmpeg)}});
  await api('/library/scan','POST');
  const ids={};
  for(const series of (await api('/library')).series)for(const episode of (await api('/library/'+series.id)).episodes)ids[episode.name]=episode.id;
  const vp9=ids[path.basename(files.vp9)],h264=ids[path.basename(files.h264)];
  assert(vp9&&h264,JSON.stringify(ids));

  // Chromium reads VP9 / FLAC in MKV itself, so the automatic choice is direct play.
  await page.goto(info.base+'/#play/'+vp9);
  await page.locator('#player-media',{hasText:'直接播放'}).waitFor({timeout:20000});
  assert.match(await page.locator('#player-media').innerText(),/^MKV · VP9 1280×720 · FLAC /);
  await page.waitForFunction(()=>{const v=document.querySelector('#player');return v&&v.readyState>=2;},null,{timeout:15000});

  // Remux over HLS: hls.js attaches a MediaSource, playback continues, and a seek far ahead restarts ffmpeg there.
  const created=page.waitForResponse(r=>r.url().endsWith('/hls')&&r.request().method()==='POST');
  await page.locator('#player-mode').selectOption('copy');
  assert.equal((await created).status(),200);
  await page.locator('#player-media',{hasText:'转封装（画面不转码）'}).waitFor();
  await page.waitForFunction(()=>{const v=document.querySelector('#player');return v&&v.src.startsWith('blob:')&&v.readyState>=2&&v.currentTime>0.3;},null,{timeout:30000});
  assert(Math.abs(await page.evaluate(()=>document.querySelector('#player').duration)-12)<0.5);
  await page.evaluate(()=>{const v=document.querySelector('#player');v.currentTime=8.5;v.play();});
  await page.waitForFunction(()=>{const v=document.querySelector('#player');return v.currentTime>8.6&&v.readyState>=3;},null,{timeout:30000});

  // Leaving ends the server session.
  const ended=page.waitForRequest(r=>/\/api\/library\/hls\/[0-9a-f]+$/.test(r.url())&&r.method()==='DELETE');
  await page.goto(info.base+'/#play/'+h264);
  await ended;

  // H.264 is missing from Playwright's Chromium, so only the choices are checked here: the embedded ASS is offered as a
  // text track and for burning in, and remux is not offered for a codec the browser cannot decode.
  await page.locator('#player-subtitle option[value=t0]').waitFor({state:'attached',timeout:20000});
  assert.equal(await page.locator('#player-subtitle option[value=t0]').innerText(),'简体中文 · 内封 ASS');
  assert.equal(await page.locator('#player-subtitle optgroup option',{hasText:'内封 ASS 完整特效'}).count(),1);
  const canDecode=await page.evaluate(()=>MediaSource.isTypeSupported('video/mp4; codecs="avc1.64001F"'));
  assert.equal(await page.locator('#player-mode option[value=copy]').count(),canDecode?1:0);
  if(!canDecode)await page.locator('#player-media',{hasText:'转码'}).waitFor();
  await page.goto(info.base+'/#library');
  await api('/settings','PUT',settings);
};
