const assert=require('node:assert/strict');
module.exports=async(page,fixture)=>{
  const status=page.locator('#library-status');
  await status.filter({hasText:/尚未扫描|共 \d+ 部作品/}).waitFor();
  assert.equal(await page.locator('[name=libraryEnabled]').isChecked(),true);
  assert.equal(await page.locator('[name=libraryFetchMetadata]').isChecked(),true);
  await page.locator('[name=libraryScanIntervalMinutes]').fill('30');
  await page.locator('[name=libraryBangumiApiUrl]').fill(fixture+'/bgm');
  // Scanning saves the form first, like the other test buttons.
  const scan=page.waitForResponse(r=>r.url().endsWith('/api/library/scan')&&r.request().method()==='POST');
  await page.locator('#library-scan').click();
  assert.equal((await scan).status(),200);
  await status.filter({hasText:'共 0 部作品、0 个视频'}).waitFor();
  await page.reload();
  await page.locator('[name=libraryScanIntervalMinutes]').waitFor();
  assert.equal(await page.locator('[name=libraryScanIntervalMinutes]').inputValue(),'30');
  assert.equal(await page.locator('[name=libraryBangumiApiUrl]').inputValue(),fixture+'/bgm');
  await status.filter({hasText:'上次扫描'}).waitFor();

  // Library folders: the three defaults, adding one, and the web search choice.
  const rows=page.locator('#library-folders .folder-row');
  assert.equal(await rows.count(),3);
  assert.deepEqual(await rows.locator('.folder-category').evaluateAll(list=>list.map(s=>s.value)),['Anime','Series','Movie']);
  assert.equal(await rows.nth(1).locator('.folder-path').inputValue(),'tvshows');
  assert.equal(await page.locator('[name=searchProvider]').inputValue(),'keenable');
  await page.locator('#add-folder').click();
  await rows.nth(3).locator('.folder-path').fill('documentaries');
  await rows.nth(3).locator('.folder-category').selectOption('Series');
  await page.locator('#settings-form [type=submit]').click();
  await page.locator('.toast',{hasText:'设置已保存'}).waitFor();
  await page.reload();
  await rows.nth(3).waitFor();
  assert.equal(await rows.nth(3).locator('.folder-path').inputValue(),'documentaries');
  assert.match(await rows.nth(3).getAttribute('data-folder'),/^f[0-9a-f]{8}$/);
  await rows.nth(3).locator('[data-remove-folder]').click();
  assert.equal(await rows.count(),3);
  await page.locator('#settings-form [type=submit]').click();
  await page.locator('.toast',{hasText:'设置已保存'}).last().waitFor();
};
