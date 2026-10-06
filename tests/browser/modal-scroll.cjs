const assert = require('node:assert/strict');

module.exports = async function verifyModalScroll(page) {
  const viewport = page.viewportSize();
  for (const width of [1440, 390]) {
    await page.setViewportSize({width, height:844});
    await page.evaluate(() => window.scrollTo(0, 180));
    const before = await page.evaluate(() => window.scrollY);
    assert(before > 0, 'Background must be scrollable for this test');
    await page.evaluate(async () => {
      const {modal} = await import('/js/core.js');
      modal('滚动测试', '<div style="height:1600px">长内容</div>');
    });
    const dialog = page.locator('#modal');
    const box = await dialog.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, 400);
    await page.waitForTimeout(250);
    assert(await dialog.evaluate(x => x.scrollTop > 0), 'Modal content must still scroll');
    await dialog.evaluate(x => x.scrollTop = x.scrollHeight);
    await page.mouse.wheel(0, 1800);
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => window.scrollY), before, 'Bottom boundary leaked');
    await dialog.evaluate(x => x.scrollTop = 0);
    await page.mouse.wheel(0, -1800);
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => window.scrollY), before, 'Top boundary leaked');
    await page.evaluate(async () => {
      const {previewDialog} = await import('/js/directory-picker.js');
      previewDialog('短预览', '<p>无需滚动的内容</p>');
    });
    const preview = await page.locator('#feed-preview').boundingBox();
    await page.mouse.move(preview.x + preview.width / 2, preview.y + preview.height / 2);
    await page.mouse.wheel(0, 1800);
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => window.scrollY), before, 'Short nested modal leaked');
    await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).overflowY), 'hidden');
    await page.locator('#modal-close').click();
    assert.equal(await page.evaluate(() => window.scrollY), before, 'Closing moved background');
    await page.mouse.move(width / 2, 400);
    await page.mouse.wheel(0, 450);
    await page.waitForTimeout(250);
    assert(await page.evaluate(() => window.scrollY) > before, 'Background did not unlock');
  }
  await page.setViewportSize(viewport);
  await page.evaluate(() => window.scrollTo(0, 0));
};
