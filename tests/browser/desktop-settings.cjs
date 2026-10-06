const assert=require('node:assert/strict');
module.exports=async page=>{
  assert.equal(await page.locator('#desktop').count(),0);
  let enabled=false,shortcuts=0;
  await page.route('**/api/desktop/autostart',async route=>{
    enabled=route.request().postDataJSON().enabled;
    await route.fulfill({json:{available:true,autoStart:enabled}});
  });
  await page.route('**/api/desktop/shortcut',async route=>{shortcuts++;await route.fulfill({json:{path:'fixture'}});});
  try{
    await page.evaluate(async()=>{
      const module=await import('/js/desktop-settings.js');
      document.querySelector('#settings-form').insertAdjacentHTML('afterbegin',module.desktopSettings({available:true,autoStart:false}));
      module.bindDesktopSettings();
    });
    await page.locator('#desktop-autostart').check();
    await page.waitForFunction(()=>!document.querySelector('#desktop-autostart').disabled);
    assert(enabled);
    await page.locator('#desktop-autostart').uncheck();
    await page.waitForFunction(()=>!document.querySelector('#desktop-autostart').disabled);
    assert(!enabled);
    await page.locator('#desktop-shortcut').click();
    await page.waitForFunction(()=>!document.querySelector('#desktop-shortcut').disabled);
    assert.equal(shortcuts,1);
  }finally{
    await page.evaluate(()=>document.querySelector('#desktop').remove());
    await page.unroute('**/api/desktop/autostart');
    await page.unroute('**/api/desktop/shortcut');
  }
};
