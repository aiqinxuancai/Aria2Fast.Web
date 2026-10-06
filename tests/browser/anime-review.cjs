const assert=require('node:assert/strict');

module.exports=async function verifyAnimeReview(page){
  const review={score:8,review:'综合评析 [1]',createdAt:'2026-10-06T08:00:00Z',overview:'作品概况 [1]',originalWork:'原作作者与出版社 [1]',adaptation:'动画改编 [1]',recommendation:'适合喜欢冒险的观众',caveats:'最新连载状态待核实',model:'fixture',version:2,queries:['作品 原作 作者'],warnings:[],references:[{id:1,title:'官方资料',url:'https://example.com/anime'},{id:2,title:'非法链接',url:'javascript:alert(1)'}]};
  let calls=0,release;
  const pending=new Promise(resolve=>release=resolve);
  await page.route('**/api/anime/123/review*',async route=>{
    calls++;
    if(calls===1){await pending;return route.fulfill({json:review});}
    assert(route.request().url().includes('refresh=true'));
    await route.fulfill({status:502,json:{error:'模拟搜索失败'}});
  });
  try{
    await page.locator('#ai-review').click();
    await page.locator('#ai-review-content[aria-busy=true]').waitFor();
    assert(await page.locator('#ai-review').isDisabled());
    release();
    await page.locator('#ai-review').filter({hasText:'重新调查'}).waitFor();
    assert((await page.locator('#ai-review-content').textContent()).includes('原作作者与出版社'));
    assert.equal(await page.locator('#ai-review-content a').count(),1);
    assert.equal(await page.locator('#ai-review-content a').getAttribute('rel'),'noopener noreferrer');
    await page.locator('#ai-review').click();
    await page.locator('#ai-review-content .error').waitFor();
    assert((await page.locator('#ai-review-content').textContent()).includes('原作作者与出版社'));
    assert((await page.locator('#ai-review-content .error').textContent()).includes('已保留'));
    assert.equal(calls,2);
  }finally{release();await page.unroute('**/api/anime/123/review*');}
};
