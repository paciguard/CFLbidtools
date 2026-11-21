import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { chromium, devices } from 'playwright';

const root = new URL('./', import.meta.url);
const origin = 'https://appassets.androidplatform.net';
const output = new URL('./artifacts/', root);
await mkdir(output, { recursive: true });
assert.ok(process.env.CFL_OFFLINE_TEST_DLL, 'Set CFL_OFFLINE_TEST_DLL to the OfflineCheck DLL.');
const engine = spawn('dotnet', [process.env.CFL_OFFLINE_TEST_DLL, 'artifacts/offline-ui-tests', '--ui-bridge'],
  { stdio: ['pipe', 'pipe', 'inherit'] });
const pending = new Map();
const saves = [];
const aborted = [];
createInterface({ input: engine.stdout }).on('line', line => {
  const result = JSON.parse(line);
  if (result.data?.saved) saves.push(result.data);
  pending.get(result.id)?.(result);
  pending.delete(result.id);
});
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const context = await browser.newContext({ ...devices['Pixel 7'], offline: true, serviceWorkers: 'block' });
  await context.exposeBinding('cflTestNative', (_, text) => new Promise(resolve => {
    pending.set(JSON.parse(text).id, resolve);
    engine.stdin.write(text + '\n');
  }));
  await context.addInitScript(() => {
    window.CflDevice = { request: text => window.cflTestNative(text).then(reply => {
      // Reproduce the native WebView's large message delivery.
      const json = JSON.stringify(reply);
      const pieces = json.match(/[\s\S]{1,24000}/g);
      if (pieces.length === 1) window.cflReply(reply);
      else pieces.forEach((part, index) => window.cflReplyPart(reply.id, index, pieces.length, part));
    }) };
  });
  const mimes = { html: 'text/html', js: 'text/javascript', css: 'text/css', png: 'image/png', jpg: 'image/jpeg', webmanifest: 'application/json' };
  const vendor = 'vendor/SDOCtools/ZTools.Standalone/wwwroot/';
  await context.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== origin || url.pathname.startsWith('/api/')) {
      aborted.push(url.href);
      return route.abort();
    }
    const name = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    const asset = name === 'sdoc-assets/mobile.js' ? 'vendor/SDOCtools/ZTools.Mobile.Shared/web/mobile.js' :
      name.startsWith('sdoc-assets/') ? vendor + name.slice('sdoc-assets/'.length) : 'wwwroot/' + name;
    try {
      await route.fulfill({ contentType: mimes[name.split('.').at(-1)], body: await readFile(new URL(asset, root)),
        headers: { 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; frame-src 'self'; worker-src 'none'; form-action 'none'" } });
    } catch { await route.fulfill({ status: 404, body: '' }); }
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin);
  const subtype = page.getByLabel('X 子型号');
  assert.equal(await subtype.isVisible(), false);
  await page.locator('#duty-form select[name=family]').selectOption('X');
  await subtype.selectOption('XZX');
  await page.locator('#duty-form button[type=submit]').click();
  await page.locator('#results').getByRole('button', { name: 'XZX 50-200', exact: true }).first().click();
  await page.locator('#export-curve:enabled').waitFor();
  await page.locator('#export-curve').click();
  await page.locator('#export-curve:enabled').waitFor();
  assert.ok(saves.some(file => file.name === 'Curve_XZX50-200_2950RPM_V0.pdf' && file.size > 1000));
  await subtype.selectOption('XF');
  assert.equal(await page.locator('#export-curve').isDisabled(), true);
  await page.locator('#duty-form select[name=family]').selectOption('XC');
  assert.equal(await subtype.isVisible(), false);
  await page.getByRole('spinbutton', { name: '介质黏度 cP' }).fill('40');
  await page.locator('#duty-form button[type=submit]').click();
  await page.locator('#results').getByRole('button', { name: 'XC 50-200', exact: true }).first().click();
  const plots = page.locator('#plots .screen-plot');
  await plots.last().waitFor();
  assert.equal(await plots.count(), 3);
  assert.ok(await plots.first().evaluate(image => image.naturalWidth > 0));
  assert.equal(await page.locator('#metrics').getByText('NPSHr（估算）').count(), 1);
  assert.match(await page.locator('#selection-subtitle').innerText(), /OH_750/);
  assert.match(await page.locator('#selection-original').innerText(), /CZ_50-200_B_CZ50-200\/2/);
  await page.emulateMedia({ media: 'print' });
  assert.equal(await page.locator('#selection-original').isVisible(), false);
  await page.emulateMedia({ media: 'screen' });
  await page.screenshot({ path: fileURLToPath(new URL('offline-android-pump.png', output)) });
  await page.locator('#export-curve').click();
  await page.locator('#export-curve:enabled').waitFor();
  assert.ok(saves.some(file => file.name === 'Curve_XC50-200_2950RPM_V0.pdf' && file.size > 1000));
  await page.locator('#open-submission').click();
  const docs = page.frameLocator('#submission-frame');
  await docs.locator('#duty-details').getByText('40 cP', { exact: false }).waitFor();
  await docs.locator('#transfer-model option[value=CZ50-200]').waitFor({ state: 'attached' });
  await docs.locator('#new-from-pump').click();
  await docs.locator('#device option').filter({ hasText: 'CZ50-200' }).waitFor({ state: 'attached' });
  await page.locator('nav [data-view=catalog]').click();
  await page.locator('#catalog-form select[name=family]').selectOption('BKC');
  await page.locator('#catalog-form button[type=submit]').click();
  const firstCurve = page.locator('#catalog-results tr').first();
  await firstCurve.getByRole('cell', { name: 'BKC', exact: true }).waitFor();
  await firstCurve.getByRole('cell', { name: '未开放选型', exact: false }).waitFor();
  await page.locator('nav [data-view=pumps]').click();
  await page.locator('#duty-form select[name=family]').selectOption('A');
  await page.locator('#duty-form [name=flow]').fill('59.54');
  await page.locator('#duty-form [name=head]').fill('145.49');
  await page.locator('#duty-form [name=viscosityCp]').fill('');
  await page.locator('#duty-form button[type=submit]').click();
  await page.locator('#results').getByRole('button', { name: 'A 23-40', exact: true }).waitFor();
  await page.locator('#export-curve:enabled').waitFor();
  await page.locator('#export-curve').click();
  await page.locator('#export-curve:enabled').waitFor();
  assert.ok(saves.some(file => file.name === 'Curve_A23-40_2980RPM_V0.pdf' && file.size > 1000));
  await page.locator('#duty-form [name=flow]').fill('30');
  await page.locator('#duty-form [name=head]').fill('150');
  await page.locator('#duty-form [name=rpm]').fill('2980');
  await page.locator('#duty-form button[type=submit]').click();
  const trim = page.locator('#trim-preview');
  await trim.getByRole('cell', { name: '156.67', exact: true }).waitFor();
  assert.equal(await trim.locator('tbody tr').count(), 5);
  assert.equal(await trim.getByRole('cell', { name: '参考插值', exact: true }).count(), 1);
  for (const head of ['119.11', '86.98', '58.85'])
    assert.equal(await trim.getByRole('cell', { name: head, exact: true }).count(), 1);
  assert.equal(await page.locator('#trim-graphs img').count(), 3);
  await page.locator('#export-trim-data').click();
  await page.locator('#export-trim-data:enabled').waitFor();
  assert.ok(saves.some(file => file.name === 'Impeller_Trim_Curves.json' && file.size > 1000));
  await page.locator('#results').getByRole('button', { name: 'A 23-40', exact: true }).waitFor();
  await page.locator('#selection-original').filter({ hasText: '叶轮直径为插值' }).waitFor();
  await page.locator('#metrics').filter({ hasText: '308.79' }).waitFor();
  await page.locator('#export-curve:enabled').waitFor();
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await trim.screenshot({ path: fileURLToPath(new URL('trim-preview-phone.png', output)) });
  await page.locator('#trim-graphs').screenshot({ path: fileURLToPath(new URL('trim-graphs-phone.png', output)) });
  assert.deepEqual(aborted, []);
  assert.deepEqual(errors, []);
  console.log('Offline mobile UI loaded with networking disabled, plotted CZ curves, saved a PDF and transferred to ZTools.');
} finally {
  await browser.close();
  engine.stdin.end();
  if (engine.exitCode === null && engine.signalCode === null)
    await new Promise(resolve => engine.once('exit', resolve));
}
