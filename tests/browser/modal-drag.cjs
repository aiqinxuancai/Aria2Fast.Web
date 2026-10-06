const assert = require('node:assert/strict');

module.exports = async function verifyModalDrag(page) {
  const viewport=page.viewportSize();
  for(const width of [1440,390]){
    await page.setViewportSize({width,height:844});
    await page.locator('#quick-add').click();
    const input=page.locator('#add-form textarea');
    const value='https://example.com/keep-my-unsaved-input.zip';
    await input.fill(value);
    const box=await input.boundingBox();
    await page.mouse.move(box.x+box.width-15,box.y+20);
    await page.mouse.down();
    await page.mouse.move(2,box.y+20,{steps:12});
    await page.mouse.up();
    assert(await page.locator('#modal').evaluate(x=>x.open),'Drag from input to backdrop closed modal');
    assert.equal(await input.inputValue(),value);
    // Starting on the backdrop and releasing inside must also keep it open.
    await page.mouse.move(2,box.y+20);
    await page.mouse.down();
    await page.mouse.move(box.x+20,box.y+20,{steps:12});
    await page.mouse.up();
    assert(await page.locator('#modal').evaluate(x=>x.open));
    // Ordinary backdrop click still dismisses it.
    await page.mouse.click(2,box.y+20);
    await page.waitForFunction(()=>!document.querySelector('#modal').open);
    await page.locator('#quick-add').click();
    await page.keyboard.press('Escape');
    await page.waitForFunction(()=>!document.querySelector('#modal').open);
    await page.locator('#quick-add').click();
    await page.locator('#modal-close').click();
    await page.waitForFunction(()=>!document.querySelector('#modal').open);
  }
  await page.setViewportSize(viewport);
};
