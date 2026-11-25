const assert=require('node:assert/strict');

// Resource search mode, detail-page cross-source search and source settings, against the integration fixtures.
module.exports=async function verifyAnimeSearch(page,fixture){
  await page.evaluate(async fixture=>{
    const call=(path,method,body)=>fetch('/api'+path,{method,headers:{'Content-Type':'application/json','X-Aria2Fast':'1'},body:JSON.stringify(body)}).then(async r=>{if(!r.ok)throw new Error(await r.text());});
    const settings=(await fetch('/api/state').then(r=>r.json())).settings;
    for(const [key,path] of [['acgRip','/acgrip'],['nyaa','/nyaa/'],['dmhy','/dmhy'],['acgnx','/acgnx']])settings.animeSources[key]={enabled:true,baseUrl:fixture+path};
    await call('/settings','PUT',settings);
    await call('/anime/torznab','POST',{id:'browser',name:'Browser Prowlarr',url:fixture+'/torznab/api',apiKey:'fixture-key',categories:'5070'});
  },fixture);

  await page.locator('nav a[data-page=anime]').click();
  await page.locator('.anime-card').first().waitFor();
  await page.locator('.segmented [data-mode=search]').click();
  await page.locator('#finder-form').waitFor();
  assert.equal(await page.locator('#anime-year').count(),0,'Calendar controls belong to calendar mode');
  assert.match(await page.locator('#finder-results').textContent(),/搜索全网资源/);
  assert.equal(await page.locator('#finder-source option').count(),6);

  await page.locator('#finder-q').fill('测试作品');
  await page.locator('#finder-form [type=submit]').click();
  await page.locator('.finder-row').first().waitFor();
  assert.match(await page.evaluate(()=>location.hash),/mode=search&q=%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81/);
  assert.equal(await page.locator('.finder-source').count(),5);
  assert.equal(await page.locator('.finder-source.failed').count(),1,'Only the challenged source fails');
  assert.match(await page.locator('.finder-source.failed').textContent(),/末日动漫.*人机验证/);
  assert.equal(await page.locator('.finder-row').count(),6);
  const nyaa=page.locator('.finder-row',{has:page.locator('.finder-badge',{hasText:'Nyaa'})});
  assert.match(await nyaa.locator('.finder-meta').textContent(),/1\.4 GB.*做种 120 · 下载 8/);
  assert.equal(await nyaa.locator('a[target=_blank]').getAttribute('rel'),'noopener noreferrer');
  const dmhy=page.locator('.finder-row',{has:page.locator('.finder-badge',{hasText:'动漫花园'})});
  assert.equal(await dmhy.locator('.finder-meta span').first().textContent(),'—','Unknown sizes are not shown as 0 B');

  await page.locator('#finder-source').selectOption('nyaa');
  assert.equal(await page.locator('#finder-category').isVisible(),true);
  assert.equal(await page.locator('#finder-filter').isVisible(),true);
  await page.locator('#finder-source').selectOption('acgnx');
  assert.equal(await page.locator('#finder-category').isHidden(),true);
  assert.equal(await page.locator('#finder-filter').isHidden(),true);
  await page.locator('#finder-source').selectOption('all');

  await page.locator('.finder-source[data-source=acgrip] [data-subscribe]').click();
  await page.locator('#sub-form').waitFor();
  assert.equal(await page.locator('#sub-form input[name=url]').inputValue(),fixture+'/acgrip/.xml?term=%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81');
  assert.equal(await page.locator('#sub-form input[name=name]').inputValue(),'测试作品 · ACG.RIP');
  await page.locator('#modal-close').click();
  await page.locator('.finder-source[data-source="torznab:browser"] [data-subscribe]').click();
  assert.doesNotMatch(await page.locator('#sub-form input[name=url]').inputValue(),/apikey/);
  await page.locator('#modal-close').click();
  await nyaa.locator('[data-download]').click();
  assert.equal(await page.locator('#add-form textarea').inputValue(),fixture+'/nyaa/download/1.torrent');
  await page.locator('#modal-close').click();

  await page.setViewportSize({width:390,height:844});
  await page.waitForTimeout(150);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true,'Search results overflow on mobile');
  await page.setViewportSize({width:1440,height:1000});

  await page.locator('.segmented [data-mode=calendar]').click();
  await page.locator('.anime-card[data-anime="123"]').click();
  await page.locator('#search-sources').click();
  await page.locator('#finder-q').waitFor();
  assert.equal(await page.locator('#finder-q').inputValue(),'Fixture Anime');
  assert.equal(await page.locator('#modal').evaluate(x=>x.open),false);
  await page.locator('.finder-row').first().waitFor();

  await page.locator('nav a[data-page=settings]').click();
  await page.locator('#sources .torznab-card').filter({hasText:'Browser Prowlarr'}).waitFor();
  assert.equal(await page.locator('[name=proxyTorrentDownloads]').isChecked(),true);
  await page.locator('[name=src-acgnx-enabled]').uncheck();
  await page.locator('#settings-form [type=submit]').click();
  await page.locator('.toast').filter({hasText:'设置已保存'}).waitFor();
  const saved=await page.evaluate(()=>fetch('/api/state').then(r=>r.json()).then(s=>s.settings.animeSources));
  assert.equal(saved.acgnx.enabled,false);
  assert.equal(saved.torznab.length,1,'Saving settings keeps Torznab endpoints');
  await page.locator('[data-edit-torznab=browser]').click();
  await page.locator('#test-torznab').click();
  await page.locator('#torznab-test-result.ai-test-success').filter({hasText:'Fixture Indexer'}).waitFor();
  await page.locator('#torznab-form input[name=apiKey]').fill('wrong');
  await page.locator('#test-torznab').click();
  await page.locator('#torznab-test-result.error').filter({hasText:'Invalid API Key'}).waitFor();
  await page.locator('#modal-close').click();
};
