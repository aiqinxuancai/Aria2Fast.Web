const assert=require('node:assert/strict');
module.exports=async(page,fixture)=>{
  await page.locator('[name=pushKey]').fill('deer-key');
  await page.locator('[name=pushEndpoint]').fill(fixture+'/pushdeer');
  await page.locator('[name=barkKey]').fill('bark-key');
  await page.locator('[name=barkEndpoint]').fill(fixture+'/bark/');
  const test=async(selector,path)=>{
    const response=page.waitForResponse(r=>r.url().endsWith(path)&&r.request().method()==='POST');
    await page.locator(selector).click();
    assert.equal((await response).status(),200);
    await page.waitForFunction(selector=>!document.querySelector(selector).disabled,selector);
  };
  await page.locator('[name=pushEnabled]').uncheck();
  await page.locator('[name=barkEnabled]').check();
  await test('#test-push','/api/push/test');
  await page.reload();
  await page.locator('#test-bark').waitFor();
  assert.equal(await page.locator('[name=pushEnabled]').isChecked(),false);
  assert.equal(await page.locator('[name=barkEnabled]').isChecked(),true);
  assert.equal(await page.locator('[name=barkKey]').inputValue(),'bark-key');
  await page.locator('[name=pushEnabled]').check();
  await page.locator('[name=barkEnabled]').uncheck();
  await test('#test-bark','/api/bark/test');
  await page.reload();
  await page.locator('#test-push').waitFor();
  assert.equal(await page.locator('[name=pushEnabled]').isChecked(),true);
  assert.equal(await page.locator('[name=barkEnabled]').isChecked(),false);
};
