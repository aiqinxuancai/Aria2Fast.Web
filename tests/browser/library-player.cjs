const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'../..');

// A few seconds of WebM (VP8) recorded with Playwright's bundled ffmpeg: a real, seekable video without extra downloads.
async function recordVideo(browser,target){
  const context=await browser.newContext({viewport:{width:320,height:180},recordVideo:{dir:path.dirname(target),size:{width:320,height:180}}});
  const page=await context.newPage();
  await page.setContent('<body style="margin:0;background:#123"><div id="b" style="width:80px;height:80px;background:#7de0c0"></div><script>let x=0;setInterval(()=>{x=(x+7)%240;b.style.transform="translateX("+x+"px)"},40)</script></body>');
  await page.waitForTimeout(3500);
  const video=page.video();
  await context.close();
  fs.renameSync(await video.path(),target);
}

module.exports=async(page,info,browser)=>{
  const downloads=path.join(info.data,'downloads');
  const folder=path.join(downloads,'测试番剧','Season 1');
  fs.mkdirSync(folder,{recursive:true});
  const first=path.join(folder,'[Fixture] 测试番剧 - 01 [1080p].webm');
  await recordVideo(browser,first);
  fs.copyFileSync(first,path.join(folder,'[Fixture] 测试番剧 - 02 [1080p].webm'));
  fs.writeFileSync(path.join(folder,'[Fixture] 测试番剧 - 01 [1080p].sc.ass'),'\ufeff[Script Info]\nTitle: test\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:30.00,Default,,0,0,0,,{\\an8}简体字幕\\N第二行\n');
  fs.writeFileSync(path.join(folder,'[Fixture] 测试番剧 - 01 [1080p].tc.srt'),'1\n00:00:00,000 --> 00:00:30,000\n<i>繁體字幕</i>\n');
  fs.writeFileSync(path.join(downloads,'[ANi] Broken Show - 01 [1080P].mp4'),'not a video');
  const api=async(p,method='GET',data)=>{const r=await page.request.fetch(info.base+'/api'+p,{method,headers:{'X-Aria2Fast':'1'},data});assert(r.ok(),p+' '+r.status());return r.json().catch(()=>null);};
  // The subscription's Mikan page links Bangumi subject 9001, which has episode titles.
  const sub=await api('/subscriptions','POST',{name:'测试番剧',url:info.fixture+'/RSS/123?group=player',nodeId:'local',namePath:'测试番剧',season:1,mikanId:'123',enabled:false});
  await api('/library/scan','POST');
  for(let i=0;i<80;i++){const o=await api('/library');if(o.series.length===2&&o.series.every(s=>s.status!=='Pending'))break;await page.waitForTimeout(250);}

  await page.locator('nav a[data-page=library]').click();
  const card=page.locator('.library-card',{hasText:'测试番剧中文名'});
  await card.waitFor();
  assert.equal(await page.locator('nav a[data-page=library]').getAttribute('class'),'active');
  assert.equal(await page.locator('#library-continue').innerHTML(),'');
  await page.locator('#library-search').fill('不存在的作品');
  await page.locator('#library-grid .empty').waitFor();
  await page.locator('#library-search').fill('');
  await page.locator('#library-filter').selectOption('unmatched');
  await page.locator('.library-card',{hasText:'Broken Show'}).waitFor();
  assert.equal(await card.count(),0);
  await page.locator('#library-filter').selectOption('all');
  await card.click();
  await page.locator('.library-hero h1',{hasText:'测试番剧中文名'}).waitFor();
  assert.deepEqual(await page.locator('.episode-row strong').allInnerTexts(),['第一集','第二集']);
  assert.match(await page.locator('.library-source').innerText(),/Bangumi #9001.*Mikan/);

  // Manual match dialog: Bangumi search results with a choose button.
  await page.locator('#series-match').click();
  await page.locator('#match-results .hint, #match-results .match-row').first().waitFor();
  await page.locator('#match-search input').fill('Search Show');
  await page.locator('#match-search button').click();
  await page.locator('.match-row',{hasText:'Search Show'}).locator('[data-pick="9002"]').waitFor();
  await page.locator('#modal-close').click();

  // Play: autoplay after the click, Simplified Chinese subtitles first, top-aligned ASS line with its line break.
  await page.locator('.library-actions a.primary',{hasText:'播放 第 1 集'}).click();
  await page.waitForFunction(()=>{const v=document.querySelector('#player');return v&&v.readyState>=2&&!v.paused&&v.videoWidth===320;},null,{timeout:15000});
  assert.equal(await page.locator('nav a[data-page=library]').getAttribute('class'),'active');
  assert.deepEqual(await page.evaluate(()=>[...document.querySelector('#player').textTracks].map(t=>t.label+'|'+t.mode)),['简体中文 · ASS|showing','繁體中文 · SRT|disabled']);
  await page.waitForFunction(()=>[...document.querySelector('#player').textTracks].find(t=>t.mode==='showing')?.cues?.length>0);
  assert.deepEqual(await page.evaluate(()=>{const c=[...document.querySelector('#player').textTracks].find(t=>t.mode==='showing').cues[0];return [c.text,c.line];}),['简体字幕\n第二行',0]);
  await page.locator('#player-subtitle').selectOption('t1');
  assert.equal(await page.evaluate(()=>localStorage.getItem('aria2fast.subtitle')),JSON.stringify({language:'zh-Hant',label:'繁體中文'}));
  fs.mkdirSync(path.join(root,'artifacts/screenshots'),{recursive:true});
  await page.screenshot({path:path.join(root,'artifacts/screenshots/library-player.png')});

  // Keyboard shortcuts and progress saved on pause.
  await page.evaluate(()=>document.activeElement?.blur());
  const saved=page.waitForRequest(r=>r.url().endsWith('/progress')&&r.method()==='POST');
  await page.keyboard.press('k');
  const progress=(await saved).postDataJSON();
  assert(progress.position>0&&progress.duration>3,JSON.stringify(progress));
  await page.keyboard.press('m');
  assert.equal(await page.evaluate(()=>document.querySelector('#player').muted),true);

  // External players: the link opens without logging in; the M3U names the signed link.
  await page.locator('.player-actions .ext-menu summary').click();
  await page.locator('.ext-list [data-m3u]').waitFor();
  const download=page.waitForEvent('download');
  await page.locator('.ext-list [data-m3u]').click();
  const playlist=fs.readFileSync(await (await download).path(),'utf8');
  const url=playlist.split('\n').find(line=>line.startsWith(info.base+'/media/'));
  assert(playlist.startsWith('#EXTM3U\n#EXTINF:-1,[Fixture] 测试番剧 - 01 [1080p].webm\n')&&url,playlist);
  const anonymous=await fetch(url,{headers:{Range:'bytes=0-3'}});
  assert.equal(anonymous.status,206);
  assert.deepEqual([...new Uint8Array(await anonymous.arrayBuffer())],[0x1a,0x45,0xdf,0xa3]); // EBML header
  for(const link of await page.locator('.ext-list a').evaluateAll(list=>list.map(a=>a.getAttribute('href'))))
    assert.match(link,/^(potplayer|iina|infuse|vlc-x-callback|intent|nplayer-)[:/a-z-]*.*media/);

  // The end of an episode marks it watched and offers the next one.
  await page.evaluate(()=>{const v=document.querySelector('#player');v.currentTime=v.duration-0.2;v.play();});
  await page.locator('#player-next:not([hidden])',{hasText:'第 2 集 · 第二集'}).waitFor({timeout:10000});
  assert.equal(await page.locator('#player-watched').getAttribute('aria-pressed'),'true');
  await page.locator('#player-next a.primary').click();
  await page.locator('.player-title h1',{hasText:'第 2 集 · 第二集'}).waitFor();
  await page.waitForFunction(()=>{const v=document.querySelector('#player');return v&&v.readyState>=2&&!v.paused;},null,{timeout:15000});
  assert.equal(await page.locator('.player-episodes a.active b').innerText(),'第二集');
  assert.match(await page.locator('.player-episodes a').first().innerText(),/✓ 已看/);
  assert.equal(await page.locator('#player-subtitle').count(),0); // no subtitles for episode 2

  // Back on the wall: continue with episode 2; the first is watched.
  await page.locator('nav a[data-page=library]').click();
  await page.locator('.continue-item',{hasText:'第 2 集 · 第二集'}).waitFor();
  await page.screenshot({path:path.join(root,'artifacts/screenshots/library-wall.png'),fullPage:true});
  await card.click();
  await page.locator('.episode-row.watched',{hasText:'第一集'}).waitFor();
  await page.locator('.episode-row',{hasText:'第二集'}).locator('[data-watched]').click();
  await page.locator('.episode-row.watched',{hasText:'第二集'}).waitFor();
  await page.screenshot({path:path.join(root,'artifacts/screenshots/library-detail.png'),fullPage:true});

  // A file the browser cannot decode explains why and points to external players; unknown IDs explain too.
  const broken=(await api('/library')).series.find(s=>s.folderTitle==='Broken Show');
  const episode=(await api('/library/'+broken.id)).episodes[0];
  await page.goto(info.base+'/#play/'+episode.id);
  await page.locator('#player-notice .callout.error',{hasText:'外部播放器'}).waitFor({timeout:10000});
  await page.goto(info.base+'/#play/0123456789abcdef');
  await page.locator('.callout.error',{hasText:'视频不存在'}).waitFor();
  await api('/subscriptions/'+sub.id,'DELETE');

  // A movie in the default movies folder: category tabs on the wall, a file list instead of episodes, the movie title in the player.
  const movieFolder=path.join(downloads,'movies','测试电影 (2021)');
  fs.mkdirSync(movieFolder,{recursive:true});
  fs.copyFileSync(first,path.join(movieFolder,'Test.Movie.2021.1080p.webm'));
  await api('/library/scan','POST');
  await page.goto(info.base+'/#library');
  await page.locator('#library-categories [data-category=Movie]').click();
  const movieCard=page.locator('.library-card',{hasText:'测试电影'});
  await movieCard.waitFor();
  assert.equal(await page.locator('.library-card').count(),1);
  assert.match(await movieCard.locator('small').innerText(),/^电影 · 2021/);
  await page.screenshot({path:path.join(root,'artifacts/screenshots/library-movies.png'),fullPage:true});
  await movieCard.click();
  await page.locator('.library-hero h1',{hasText:'测试电影'}).waitFor();
  assert.equal(await page.locator('.panel-head h2',{hasText:'视频文件'}).count(),1);
  assert.equal(await page.locator('[data-number]').count(),0);
  await page.locator('.library-actions a.primary').click();
  await page.locator('.player-title h1',{hasText:'测试电影'}).waitFor();
  assert.equal(await page.locator('.player-title .eyebrow').innerText(),'电影');
  await page.locator('#library-categories').count();
  await page.goto(info.base+'/#library');
  await page.locator('#library-categories [data-category=all]').click();
};
