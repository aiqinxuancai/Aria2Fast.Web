const assert = require('node:assert/strict');

module.exports = async function(page) {
  await page.locator('#quick-add').click();
  const form=page.locator('#add-form');
  const chooser=page.waitForEvent('filechooser');
  await form.locator('[data-choose-file]').click();
  await (await chooser).setFiles({name:'picked.torrent',mimeType:'application/x-bittorrent',buffer:Buffer.from('torrent fixture')});
  assert.equal(await form.locator('[data-file-name]').textContent(),'picked.torrent');
  await form.locator('[data-remove-file]').click();
  assert(await form.locator('.upload-selection').isHidden());
  async function drop(names) {
    const data=await page.evaluateHandle(names=>{
      const transfer=new DataTransfer();
      names.forEach(name=>transfer.items.add(new File(['upload fixture'],name)));
      return transfer;
    },names);
    await form.locator('.upload-dropzone').dispatchEvent('dragenter',{dataTransfer:data});
    assert(await form.locator('.task-upload').evaluate(el=>el.classList.contains('dragging')));
    await form.locator('.upload-dropzone').dispatchEvent('dragover',{dataTransfer:data});
    await form.locator('.upload-dropzone').dispatchEvent('drop',{dataTransfer:data});
    assert(!(await form.locator('.task-upload').evaluate(el=>el.classList.contains('dragging'))));
    await data.dispose();
  }
  await drop(['not-supported.txt']);
  assert(await form.locator('[data-file-error]').isVisible());
  assert(await form.locator('.upload-selection').isHidden());
  await drop(['first.torrent','second.torrent']);
  assert.match(await form.locator('[data-file-error]').textContent(),/一次选择一个/);
  await drop(['拖入文件.META4']);
  assert.equal(await form.locator('[data-file-name]').textContent(),'拖入文件.META4');
  assert(await form.locator('[data-file-error]').isHidden());
  await drop(['invalid.txt']);
  assert.equal(await form.locator('[data-file-name]').textContent(),'拖入文件.META4','Invalid replacement must retain the selected file');
  await form.locator('input[name=file]').setInputFiles({name:'empty.torrent',mimeType:'application/x-bittorrent',buffer:Buffer.alloc(0)});
  assert.match(await form.locator('[data-file-error]').textContent(),/非空/);
  await drop(['upload.metalink']);
  const originalViewport=page.viewportSize();
  await page.setViewportSize({width:390,height:844});
  assert(await form.evaluate(el=>el.scrollWidth<=el.clientWidth+1),'Upload card must fit mobile');
  await page.setViewportSize(originalViewport);
  let uploaded=false;
  await page.route('**/api/tasks/upload',async route=>{
    uploaded=true;
    const body=route.request().postDataBuffer().toString();
    assert(body.includes('filename="upload.metalink"'));
    assert(body.includes('upload fixture'));
    assert(body.includes('/downloads/历史'));
    await route.fulfill({json:'upload-fixture'});
  });
  assert.equal(uploaded,false,'Selecting a file must not upload immediately');
  await form.locator('[type=submit]').click();
  await form.waitFor({state:'hidden'});
  assert(uploaded);
  await page.unroute('**/api/tasks/upload');
};
