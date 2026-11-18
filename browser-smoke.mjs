import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium, devices } from 'playwright';

const url = 'http://127.0.0.1:5178';
const out = new URL('./artifacts/', import.meta.url);
await mkdir(out, { recursive: true });
const browser = await chromium.launch({
  channel: 'msedge', headless: true,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader']
});
const request = { headers: { 'X-CFL-Request': '1' } };
try {
  const desktop = await browser.newPage({ viewport: { width: 1440, height: 1024 }, acceptDownloads: true });
  const errors = [];
  desktop.on('pageerror', error => errors.push(error.message));
  await desktop.goto(url);
  await desktop.locator('#open-submission').click();
  const docs = desktop.frameLocator('#submission-frame');
  await docs.locator('#new').waitFor();
  assert.equal(await docs.locator('h1').innerText(), '提交资料');
  assert.equal(await docs.locator('#duty-transfer').isVisible(), false);
  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#duty-form select[name=family] option').filter({hasText: /^TTMC$/}).waitFor({state:'attached'});
  await desktop.locator('#duty-form select[name=family]').selectOption('TTMC');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#plots img').last().waitFor({ timeout: 30000 });
  assert.equal(await desktop.locator('#plots img').count(), 3);
  assert.equal(await desktop.locator('#plots img').first().evaluate(img => img.naturalWidth > 0), true);
  assert.ok(await desktop.locator('#results tr').count() > 0);
  await desktop.screenshot({ path: new URL('pump-desktop.png', out).pathname.slice(1) });
  await desktop.pdf({ path: new URL('pump-print.pdf', out).pathname.slice(1), format: 'A4', printBackground: true, preferCSSPageSize: true });

  await desktop.locator('#open-submission').click();
  await docs.locator('#duty-transfer').waitFor();
  assert.match(await docs.locator('#duty-details').innerText(), /35 m³\/h.*55 m/);
  await docs.locator('#new').click();
  await docs.locator('#device option').first().waitFor({state:'attached'});
  assert.equal(await docs.locator('#apply-duty').isDisabled(), true);
  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#duty-form select[name=family]').selectOption('Z');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#results').getByRole('button', {name: 'Z 50-200', exact: true}).first().click();
  await desktop.locator('#plots img').last().waitFor({ timeout: 30000 });
  await desktop.locator('#open-submission').click();
  await docs.locator('#duty-details').getByText('Z 50-200', { exact: false }).waitFor();
  await docs.locator('#apply-duty').click();
  await docs.locator('#transfer-status').getByText('不匹配', {exact:false}).waitFor();
  await docs.locator('#device').selectOption('1');
  await docs.locator('#apply-duty').click();
  await docs.locator('#transfer-status').getByText('已写入当前设备。').waitFor();
  const fields = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'equipment', row: 1 } });
  assert.ok(fields.ok());
  const pump = (await fields.json()).fields;
  assert.equal(Number(pump.find(field => field.address === 'L22').value), 35);
  assert.equal(Number(pump.find(field => field.address === 'M22').value), 55);
  assert.ok(Number(pump.find(field => field.address === 'P22').value) > 0);
  const efficiency = Number(pump.find(field => field.address === 'Q22').value);
  assert.ok(efficiency > 0 && efficiency < 1);
  assert.equal(Number(pump.find(field => field.address === 'R22').value), 2950);
  const motor = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'motor', row: 1 } });
  const motorFields = (await motor.json()).fields;
  assert.equal(motorFields.find(field => field.address === 'S22').value, 'YBX3-160M2-2');
  assert.equal(Number(motorFields.find(field => field.address === 'W22').value), 15);

  await docs.locator('#calculate').click();
  await docs.locator('#generate:enabled').waitFor({ timeout: 120000 });
  await docs.locator('input[value=PID]').uncheck();
  await docs.locator('input[value=SEC]').uncheck();
  const rows = docs.locator('#rows input[type=checkbox]');
  for (let index = 1; index < await rows.count(); index++) await rows.nth(index).uncheck();
  await docs.locator('#generate').click();
  await docs.locator('#creationComplete[open]').waitFor({ timeout: 120000 });
  await docs.locator('#creationComplete button').click();
  await docs.locator('#files').getByRole('button', { name: '预览', exact: true }).first().click();
  await docs.locator('#viewport canvas').waitFor({ timeout: 30000 });
  const pixels = await docs.locator('#viewport canvas').screenshot().then(async bytes => {
    return desktop.evaluate(async base64 => {
      const img = new Image();
      img.src = 'data:image/png;base64,' + base64;
      await img.decode();
      const target = document.createElement('canvas');
      target.width = 320; target.height = 200;
      const context = target.getContext('2d');
      context.drawImage(img, 0, 0, target.width, target.height);
      const buffer = context.getImageData(0, 0, target.width, target.height).data;
      let lines = 0;
      for (let offset = 0; offset < buffer.length; offset += 4)
        if (buffer[offset] < 160) lines++;
      return lines;
    }, bytes.toString('base64'));
  });
  assert.ok(pixels > 30, 'The CAD canvas should contain drawn lines.');
  await desktop.screenshot({ path: new URL('sdoc-desktop.png', out).pathname.slice(1) });
  for (const [kind, signature] of [['pdf', '%PDF-'], ['dwg', 'AC10']]) {
    const asset = await desktop.request.get(url + '/api/submissions/file/' + kind + '?id=0');
    assert.ok(asset.ok());
    assert.ok((await asset.body()).toString('ascii', 0, signature.length) === signature);
  }
  assert.deepEqual(errors, []);

  for (const device of ['iPhone 13', 'Pixel 7']) {
    const phone = await browser.newPage(devices[device]);
    await phone.goto(url);
    await phone.locator('#duty-form button[type=submit]').click();
    await phone.locator('#plots img').last().waitFor({ timeout: 30000 });
    assert.equal(await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, device);
    await phone.screenshot({ path: new URL(device.replaceAll(' ', '-') + '.png', out).pathname.slice(1), fullPage: true });
    await phone.locator('#open-submission').click();
    const frame = phone.frameLocator('#submission-frame');
    await frame.locator('#duty-transfer').waitFor();
    const state = await phone.request.post(url + '/api/submissions/command/state', { ...request, data: {} });
    assert.equal((await state.json()).job, null, 'Independent browser sessions should start with no open project.');
    assert.equal(await frame.locator('body').evaluate(body => body.scrollWidth <= innerWidth + 1), true, device + ' SDOC');
    await phone.close();
  }
  console.log('Passed desktop and phone layouts, 提交资料 button before/after selection, project isolation, field transfer, calculation, vector print, CAD preview, DWG and PDF generation.');
} finally { await browser.close(); }
