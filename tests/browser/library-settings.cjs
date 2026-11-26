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
};
