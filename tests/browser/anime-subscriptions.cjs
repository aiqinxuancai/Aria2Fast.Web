const assert=require('node:assert/strict');

module.exports=async function(page){
  const card=page.locator('[data-anime="123"]');
  const firstGroup=page.locator('.anime-group-row').first();
  await card.click();
  await firstGroup.waitFor();
  assert.equal(await page.locator('.anime-subscribed').count(),0);
  await firstGroup.locator('[data-sub]').click();
  const url=await page.locator('#sub-form [name=url]').inputValue();
  await page.locator('#sub-form [name=enabled]').uncheck();
  await page.locator('#sub-form [type=submit]').click();
  await page.locator('#feed-preview [data-confirm]').click();
  await page.locator('#sub-form').waitFor({state:'hidden'});
  await card.click();
  await firstGroup.locator('.anime-subscribed').waitFor();
  assert.equal(await firstGroup.locator('.anime-subscribed').textContent(),'✓ 已订阅 · 已暂停');
  assert.match(await firstGroup.locator('.anime-subscribed').getAttribute('title'),/已添加至：.+（已暂停）/);
  assert.equal(await page.locator('.anime-subscribed').count(),1,'Other groups must remain unmarked');
  await page.locator('#modal-close').click();
  const id=await page.evaluate(async url=>{
    const {api}=await import('/js/core.js');
    const saved=(await api('/subscriptions')).find(s=>s.url===url);
    await api('/subscriptions','POST',{...saved,enabled:true});
    return saved.id;
  },url);
  await card.click();
  await firstGroup.locator('.anime-subscribed').waitFor();
  assert.equal(await firstGroup.locator('.anime-subscribed').textContent(),'✓ 已订阅');
  await page.locator('#modal-close').click();
  await page.evaluate(async id=>{
    const {api}=await import('/js/core.js');
    await api('/subscriptions/'+encodeURIComponent(id),'DELETE');
  },id);
  await card.click();
  await firstGroup.waitFor();
  assert.equal(await page.locator('.anime-subscribed').count(),0,'Deleted subscriptions must no longer be marked');
  await page.locator('#modal-close').click();
};
