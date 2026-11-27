import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const url = process.env.CFL_TEST_URL ?? 'http://127.0.0.1:5192';
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ acceptDownloads: true });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    const mode = () => window.testFolderMode;
    window.showDirectoryPicker = async () => {
      if (mode() === 'cancel') throw new DOMException('Picker closed', 'AbortError');
      return (await navigator.storage.getDirectory()).getDirectoryHandle('Curve Export Check', { create: true });
    };
    const permission = FileSystemHandle.prototype.requestPermission;
    FileSystemHandle.prototype.requestPermission = function (options) {
      return this.name === 'Curve Export Check' && mode() === 'denied' ? Promise.resolve('denied') :
        permission?.call(this, options) ?? Promise.resolve('granted');
    };
    const openFolder = FileSystemDirectoryHandle.prototype.getDirectoryHandle;
    FileSystemDirectoryHandle.prototype.getDirectoryHandle = function (name, options) {
      if (this.name === 'Curve Export Check' && name === 'Curve' && mode() === 'missing')
        return Promise.reject(new DOMException('Folder no longer exists', 'NotFoundError'));
      return openFolder.call(this, name, options);
    };
  });
  await page.goto(url);
  await page.locator('#duty-form [name=family] option').filter({ hasText: /^X$/ }).waitFor({ state: 'attached' });
  await page.locator('#submission-details').evaluate(element => { element.open = true; });
  await page.locator('#submission-details-form [name=tag]').fill('1030-P-3012');
  await page.locator('#submission-details-form [name=plannedItems]').fill('2');
  const field = name => page.locator('#duty-form [name=' + name + ']');
  for (const [name, value] of Object.entries({ flow: '5', head: '50', density: '1110', viscosityCp: '1' }))
    await field(name).fill(value);
  await field('poles').selectOption('2');
  await field('family').selectOption('X');
  await field('xSubtype').selectOption('XA');
  await page.locator('#batch-add').click();
  await page.locator('#batch-items').getByRole('button', { name: '1030-P-3012' }).click();
  await page.locator('#confirm-pump:enabled').click();
  await page.locator('#metrics [name=efficiency]').fill('26');
  await page.locator('#metrics [name=npshr]').fill('0.336');
  await page.locator('#apply-rating').click();
  await page.locator('#reset-rating:enabled').click();
  await page.locator('#export-curve:enabled').waitFor();
  assert.match(await page.locator('#selection-title').innerText(), /XA 25-200/);
  assert.equal(await page.locator('#reset-rating').isDisabled(), true);
  assert.equal(await page.locator('#confirm-pump').isDisabled(), true);
  assert.equal(await page.locator('#metrics [name=efficiency]').getAttribute('placeholder'), '30.02');
  assert.equal(await page.locator('#metrics [name=efficiency]').inputValue(), '');

  const name = 'Curve_1030-P-3012_XA25-200_2950RPM_V0.pdf';
  const exportButton = page.locator('#export-curve');
  const payloads = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/pumps/export-pdf') payloads.push(request.postDataJSON());
  });
  const expectDownload = async () => {
    const [download] = await Promise.all([page.waitForEvent('download', { timeout: 8000 }), exportButton.click()]);
    assert.equal(download.suggestedFilename(), name);
    assert.equal((await readFile(await download.path())).subarray(0, 5).toString(), '%PDF-');
  };

  await page.evaluate(() => { window.testFolderMode = 'cancel'; });
  await exportButton.click();
  await page.locator('#export-curve:enabled').waitFor();
  await page.locator('#curve-export-status').getByText('已取消导出，未保存 PDF。').waitFor();
  assert.equal(payloads.length, 0, 'Canceling the directory picker must not generate a PDF.');
  await page.evaluate(() => { window.testFolderMode = ''; });
  await exportButton.click();
  await page.locator('#selection-folder-status').getByText(name, { exact: false }).waitFor();
  const signature = await page.evaluate(async name => {
    const root = await navigator.storage.getDirectory();
    const folder = await (await root.getDirectoryHandle('Curve Export Check')).getDirectoryHandle('Curve');
    const file = await (await folder.getFileHandle(name)).getFile();
    return file.slice(0, 5).text();
  }, name);
  assert.equal(signature, '%PDF-');
  assert.equal(payloads[0].ratingOverride ?? null, null);

  for (const mode of ['denied', 'missing']) {
    await page.evaluate(value => { window.testFolderMode = value; }, mode);
    await expectDownload();
    await page.locator('#curve-export-status').getByText(/无法写入项目目录.*浏览器下载/).waitFor();
  }
  assert.equal(payloads.length, 3);
  assert.ok(payloads.every(payload => payload.ratingOverride == null), 'Restored PDF used manual values.');

  const pdfRoute = '**/api/pumps/export-pdf';
  await page.route(pdfRoute, route => route.fulfill({ status: 400, json: { error: '选择已失效。' } }));
  await exportButton.click();
  await page.locator('#curve-export-status').getByText(/选择已失效/).waitFor();
  await page.unroute(pdfRoute);

  await page.locator('#batch-new').click();
  await page.locator('#submission-details-form [name=tag]').fill('1030-P-3013');
  await page.locator('#batch-add').click();
  await page.locator('#batch-search').click();
  await page.locator('#batch-export:enabled').waitFor();
  await page.evaluate(() => { window.testFolderMode = 'denied'; });
  const [zip] = await Promise.all([page.waitForEvent('download', { timeout: 8000 }), page.locator('#batch-export').click()]);
  assert.equal(zip.suggestedFilename(), 'Curves_V0.zip');
  assert.equal((await readFile(await zip.path())).subarray(0, 2).toString(), 'PK');
  await page.locator('#batch-status').getByText(/无法写入项目目录.*2.*浏览器下载/).waitFor();
  assert.deepEqual(errors, []);
  console.log('Restored XA 25-200 exports calculated values to its project folder and falls back to PDF downloads.');
} finally {
  await browser.close();
}
