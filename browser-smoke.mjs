import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium, devices } from 'playwright';

const url = process.env.CFL_TEST_URL ?? 'http://127.0.0.1:5178';
const out = new URL('./artifacts/', import.meta.url);
await mkdir(out, { recursive: true });
const browser = await chromium.launch({
  channel: 'msedge', headless: true,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader']
});
const request = { headers: { 'X-CFL-Request': '1' } };
try {
  const desktop = await browser.newPage({ viewport: { width: 1440, height: 1024 }, acceptDownloads: true });
  const connection = await desktop.request.get(new URL('/api/connection', url).href);
  assert.equal(connection.status(), 204);
  assert.equal(connection.headers()['x-cfl-workbench'], '1');
  assert.equal(connection.headers()['cache-control'], 'no-store');
  await desktop.addInitScript(() => {
    // Use a real writable directory handle without interacting with the OS picker in headless mode.
    window.showDirectoryPicker = async () => (await navigator.storage.getDirectory())
      .getDirectoryHandle('CFL Test PDFs', { create: true });
  });
  const errors = [];
  desktop.on('pageerror', error => errors.push(error.message));
  await desktop.goto(url);
  await desktop.locator('#open-submission').click();
  const docs = desktop.frameLocator('#submission-frame');
  await docs.locator('#new').waitFor();
  assert.equal(await docs.locator('h1').innerText(), '提交资料');
  assert.equal(await docs.locator('#duty-transfer').isVisible(), false);
  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#duty-form select[name=family] option').filter({hasText: /^XTT$/}).waitFor({state:'attached'});
  await desktop.locator('#duty-form select[name=family]').selectOption('XTT');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#plots .screen-plot').last().waitFor({ timeout: 30000 });
  assert.equal(await desktop.locator('#plots .screen-plot').count(), 3);
  assert.equal(await desktop.locator('#plots .screen-plot').first().evaluate(img => img.naturalWidth > 0), true);
  const verifyMarkers = async page => {
    const triangles = await page.locator('#plots img').evaluateAll(images => images.map(img => {
      const xml = new DOMParser().parseFromString(decodeURIComponent(img.src.split(',')[1]), 'image/svg+xml');
      return [...xml.querySelectorAll('polygon')].map(shape => {
        const points = shape.getAttribute('points').trim().split(/[ ,\s]+/).map(Number);
        const x = points.filter((_, index) => index % 2 === 0);
        const y = points.filter((_, index) => index % 2 === 1);
        return { count: points.length / 2, stroke: shape.style.stroke.replaceAll(' ', ''),
          points: x.map((value, index) => [value, y[index]]),
          fill: shape.style.fill, width: Math.max(...x) - Math.min(...x), height: Math.max(...y) - Math.min(...y) };
      }).find(shape => shape.count === 3 && shape.stroke === 'rgb(0,0,255)');
    }));
    assert.equal(triangles.length, 6);
    for (const triangle of triangles) {
      assert.ok(triangle, 'Missing duty-point triangle');
      assert.equal(triangle.fill, 'none');
      assert.ok(Math.abs(triangle.width - 14) < 0.01 && Math.abs(triangle.height - 14) < 0.01);
      const [upperRight, lowerRight, upperLeft] = triangle.points;
      assert.ok(upperRight[0] === lowerRight[0] && upperRight[1] === upperLeft[1] &&
        upperRight[0] > upperLeft[0] && upperRight[1] < lowerRight[1], 'Duty-point triangle was mirrored');
    }
  };
  await verifyMarkers(desktop);
  assert.ok(await desktop.locator('#results tr').count() > 0);
  await desktop.screenshot({ path: new URL('pump-desktop.png', out).pathname.slice(1) });
  const printCurve = async filename => {
    await desktop.locator('#plots .screen-plot').last().waitFor();
    await desktop.locator('#plots img').evaluateAll(images => Promise.all(images.map(img => img.decode())));
    return desktop.pdf({ path: new URL(filename, out).pathname.slice(1), printBackground: true, preferCSSPageSize: true });
  };
  await printCurve('pump-print.pdf');

  await desktop.locator('#open-submission').click();
  await docs.locator('#duty-transfer').waitFor();
  assert.match(await docs.locator('#duty-details').innerText(), /35 m³\/h.*55 m/);
  await docs.locator('#new').click();
  await docs.locator('#add:enabled').waitFor();
  assert.equal(await docs.locator('#device option').count(), 0);
  assert.equal(await docs.locator('#calculate').isDisabled(), true);
  await docs.locator('[data-form=project]').click();
  await docs.getByText('CFL订单号', { exact: true }).waitFor();
  await docs.locator('#cancel').click();
  assert.equal(await docs.locator('#apply-duty').isDisabled(), true);
  assert.equal(await docs.locator('#new-from-pump').isVisible(), false);
  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#duty-form select[name=family]').selectOption('X');
  const subtype = desktop.getByLabel('X 子型号');
  assert.deepEqual(await subtype.locator('option').evaluateAll(opts => opts.map(option => option.value)),
    ['', 'XA', 'XE', 'XF', 'XZX', 'XGP']);
  await subtype.selectOption('XE');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#results').getByRole('button', {name: 'XE 50-200', exact: true}).first().click();
  await desktop.locator('#plots .screen-plot').last().waitFor({ timeout: 30000 });
  for (const [name, value] of Object.entries({
    company: 'CFL Test Company', drawingModel: 'CFL100-300', project: 'Web test',
    tag: 'Test-01', service: 'Test service', quantity: '1', material: 'S-5'
  })) await desktop.locator('#submission-details-form [name=' + name + ']').fill(value);
  const singleCurve = 'Curve_Test-01_Test service_XE50-200_2950RPM_V0.pdf';
  await desktop.locator('#export-curve').click();
  await desktop.locator('#selection-folder-status').getByText(singleCurve, { exact: false }).waitFor();
  const xPdf = await desktop.evaluate(async name => {
    const project = await (await navigator.storage.getDirectory()).getDirectoryHandle('CFL Test PDFs');
    const file = await (await project.getFileHandle(name)).getFile();
    return Array.from(new Uint8Array(await file.arrayBuffer()));
  }, singleCurve);
  await writeFile(new URL('XE-Curve-Export.pdf', out), Buffer.from(xPdf));
  await printCurve('Z-Curve-Report.pdf');
  await desktop.locator('#open-submission').click();
  await docs.locator('#duty-details').getByText('XE 50-200', { exact: false }).waitFor();
  await docs.locator('#transfer-model').selectOption('ZE50-2200');
  await docs.locator('#new-from-pump').click();
  await docs.locator('#device option').filter({ hasText: 'ZE50-2200' }).waitFor({state:'attached'});
  assert.equal(await docs.locator('#device option').count(), 1);
  await docs.locator('#add').click();
  await docs.locator('#device option').filter({ hasText: '设备-2' }).waitFor({ state: 'attached' });
  const invalid = desktop.waitForResponse(response => response.url().endsWith('/command/calculate'));
  await docs.locator('#calculate').click();
  const validation = await invalid;
  assert.equal(validation.status(), 422);
  const issues = (await validation.json()).issues;
  for (const address of ['input!E21', 'input!Y21', 'input!AA21', 'input!U21', 'input!V21', 'input!N22', 'input!F22']) {
    assert.ok(issues.some(issue => issue.address === address), address);
    await docs.locator('#issue-list').getByText(address, { exact: false }).waitFor();
  }
  assert.equal(await docs.locator('#generate').isDisabled(), true);
  await docs.locator('#issue-list button').filter({ hasText: /^修改泵型号$/ }).click();
  assert.equal(await docs.locator('#device').inputValue(), '1');
  await docs.locator('#pump-fields input[data-address=N22]:focus').waitFor();
  await docs.locator('#remove').click();
  await docs.getByRole('button', {name:'确认删除', exact:true}).click();
  await docs.locator('#device option').filter({ hasText: '设备-2' }).waitFor({ state: 'detached' });
  const fields = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'equipment', row: 0 } });
  assert.ok(fields.ok());
  const pump = (await fields.json()).fields;
  assert.equal(Number(pump.find(field => field.address === 'L21').value), 35);
  assert.equal(Number(pump.find(field => field.address === 'M21').value), 55);
  assert.ok(Number(pump.find(field => field.address === 'P21').value) > 0);
  const efficiency = Number(pump.find(field => field.address === 'Q21').value);
  assert.ok(efficiency > 0 && efficiency < 1);
  assert.equal(Number(pump.find(field => field.address === 'R21').value), 2950);
  assert.equal(pump.find(field => field.address === 'AB21').value, 'CFL100-300');
  assert.equal(pump.find(field => field.address === 'N21').value, 'ZE50-2200');
  assert.equal(pump.find(field => field.address === 'F21').value, 'Test service');
  const hydraulicForm = docs.locator('#pump-input');
  await hydraulicForm.getByLabel('流量 (m³/h)').waitFor();
  assert.equal(await hydraulicForm.getByLabel('泵型号').inputValue(), 'ZE50-2200');
  assert.equal(Number(await hydraulicForm.getByLabel('流量 (m³/h)').inputValue()), 35);
  await hydraulicForm.getByLabel('流量 (m³/h)').fill('36');
  await hydraulicForm.getByLabel('效率 (%)').fill('61.25');
  const saveHydraulics = desktop.waitForResponse(response => response.url().endsWith('/command/save'));
  await hydraulicForm.getByRole('button', { name: '保存运行参数' }).click();
  const savedHydraulics = await saveHydraulics;
  assert.ok(savedHydraulics.ok(), await savedHydraulics.text());
  assert.equal(Number(await hydraulicForm.getByLabel('流量 (m³/h)').inputValue()), 36);
  assert.equal(Number(await hydraulicForm.getByLabel('效率 (%)').inputValue()), 61.25);
  const workbook = await desktop.request.get(url + '/api/submissions/file/xlsx');
  assert.ok(workbook.ok());
  assert.equal((await workbook.body()).toString('ascii', 0, 2), 'PK');
  const motor = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'motor', row: 0 } });
  const motorFields = (await motor.json()).fields;
  const field = address => motorFields.find(entry => entry.address === address);
  assert.ok(field('R21').readOnly && Number(field('R21').value) === 2950);
  assert.ok(field('W21').readOnly && Number(field('W21').value) > 0);
  assert.equal(field('F21').value, '50 Hz');
  assert.equal(field('T21').value, '');
  const overwrite = await desktop.request.post(url + '/api/submissions/command/save', {
    ...request, data: { group: 'motor', row: 0, changes: [{ sheet: 'input', address: 'W21', value: '999' }] }
  });
  assert.equal(overwrite.status(), 400);

  async function fill(group, values) {
    const changes = Object.entries(values).map(([address, value]) => ({ sheet: 'input', address, value }));
    const saved = await desktop.request.post(url + '/api/submissions/command/save',
      { ...request, data: { group, row: 0, changes } });
    assert.ok(saved.ok(), await saved.text());
  }
  await fill('equipment', { D21: '0011' });
  await docs.locator('[data-form=project]').click();
  const projectSettings = docs.locator('#dialog');
  const flangeStandard = projectSettings.getByLabel('法兰标准');
  await flangeStandard.selectOption('HG/T 20615');
  await projectSettings.getByRole('button', { name: '保存参数' }).click();
  await projectSettings.waitFor({ state: 'hidden' });
  const savedTag = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'equipment', row: 0 } });
  assert.equal((await savedTag.json()).fields.find(field => field.address === 'D21').value, '0011');
  await docs.locator('[data-form=configuration]').click();
  const options = docs.locator('#dialog');
  const edition = options.getByLabel('API 610 图纸版本');
  await edition.waitFor();
  assert.deepEqual(await edition.locator('option').allTextContents(), ['请选择', '7版', '8版', '10版']);
  assert.equal(await edition.inputValue(), '');
  for (const label of ['轴承形式', 'API 冲洗方案', '冷却方案'])
    assert.equal(await options.getByLabel(label).inputValue(), '');
  await edition.selectOption('8版');
  await options.getByLabel('轴承形式').fill('QWL');
  await options.getByLabel('API 冲洗方案').fill('11');
  await options.getByLabel('冷却方案').fill('M');
  const saveConfig = desktop.waitForResponse(response => response.url().endsWith('/command/save'));
  await options.getByRole('button', { name: '保存参数' }).click();
  const savedConfig = await saveConfig;
  assert.ok(savedConfig.ok(), await savedConfig.text());
  await options.waitFor({ state: 'hidden' });
  await docs.locator('[data-form=motor]').click();
  const motorForm = docs.locator('#dialog');
  await motorForm.getByLabel('电机系列 / 能效系列').fill('YBX5');
  const frameList = await motorForm.getByLabel('机座 / GA 底座号').getAttribute('list');
  assert.ok((await motorForm.locator('#' + frameList + ' option').allTextContents()).includes('160M'));
  await motorForm.getByLabel('防爆标志').fill('Ex db IIB T4 Gb');
  await motorForm.getByLabel('外壳防护等级 (IP)').fill('ip55');
  await motorForm.getByLabel('电机品牌 / 制造商').fill('Test Motor');
  await motorForm.getByLabel('电机铭牌频率').selectOption('60 Hz');
  await motorForm.getByLabel('驱动方式').selectOption('VFD');
  await motorForm.getByLabel('机座 / GA 底座号').fill('160M');
  await motorForm.getByLabel('电机铭牌电压').selectOption('380V');
  const saveMotor = desktop.waitForResponse(response => response.url().endsWith('/command/save'));
  await motorForm.getByRole('button', { name: '保存参数' }).click();
  const savedMotor = await saveMotor;
  assert.ok(savedMotor.ok(), await savedMotor.text());
  await motorForm.waitFor({ state: 'hidden' });
  const savedFields = await desktop.request.post(url + '/api/submissions/command/parameters',
    { ...request, data: { group: 'motor', row: 0 } });
  const written = Object.fromEntries((await savedFields.json()).fields.map(field => [field.address, field.value]));
  assert.equal(written.W21, field('W21').value);
  assert.equal(written.R21, '2950');
  assert.equal(written.E21, 'IP55');
  assert.equal(written.F21, '60 Hz');
  assert.equal(written.G21, 'VFD');

  const calculation = desktop.waitForResponse(response => response.url().endsWith('/command/calculate'));
  await docs.locator('#calculate').click();
  const calculated = await calculation;
  assert.ok(calculated.ok(), await calculated.text());
  assert.match((await calculated.json()).rows[0].template, /^ZE8_/);
  await docs.locator('#generate:enabled').waitFor({ timeout: 120000 });
  const rows = docs.locator('#rows input[type=checkbox]');
  for (let index = 1; index < await rows.count(); index++) await rows.nth(index).uncheck();
  await docs.locator('#project-folder-status').getByText('PDF 工作目录：CFL Test PDFs').waitFor();
  const output = desktop.waitForResponse(response => response.url().endsWith('/command/generate'), { timeout: 120000 });
  await docs.locator('#generate').click();
  const generated = await output;
  assert.ok(generated.ok(), await generated.text());
  const { files } = await generated.json();
  assert.deepEqual(files.map(file => file.kind).sort(), ['GA', 'PID', 'SEC']);
  for (const file of files) {
    assert.equal(file.name, file.kind + '__0011_Test service_CFL100-300_Plan11+M_YBX5-160M-15kW_V0');
    const scene = await desktop.request.post(url + '/api/submissions/command/scene', { ...request, data: { id: file.id } });
    assert.ok(scene.ok(), await scene.text());
    const { items } = await scene.json();
    assert.ok(items.some(item => item.text?.trim() === 'CFL100-300'), file.kind + ' drawing model');
    assert.ok(items.some(item => item.text?.trim() === 'CFL Test Company'), file.kind + ' company name');
    if (file.kind === 'GA') {
      for (const expected of ['YBX5', 'Test Motor'])
        assert.ok(items.some(item => item.text?.trim() === expected), 'GA ' + expected);
    }
  }
  await docs.locator('#creationComplete[open]').waitFor({ timeout: 120000 });
  assert.match(await docs.locator('#creationSummary').innerText(), /3 个 PDF 保存到工作目录：CFL Test PDFs/);
  const savedPdfs = (kind = '') => docs.locator('body').evaluate(async (_, kind) => {
    const root = await (await navigator.storage.getDirectory()).getDirectoryHandle('CFL Test PDFs');
    const directory = kind ? await root.getDirectoryHandle(kind) : root;
    const results = [];
    for await (const [name, handle] of directory.entries()) {
      if (handle.kind !== 'file') continue;
      const file = await handle.getFile();
      results.push([name, await file.slice(0, 5).text()]);
    }
    return results.sort(([left], [right]) => left.localeCompare(right));
  }, kind);
  const autoSaved = await savedPdfs();
  assert.equal(autoSaved.length, 4);
  assert.ok(autoSaved.some(([name]) => name === singleCurve));
  assert.ok(autoSaved.every(([name, prefix]) => name.endsWith('.pdf') && prefix === '%PDF-'));
  await docs.locator('#creationComplete button').click();
  await docs.locator('#files').getByRole('button', { name: '导出 PDF', exact: true }).first().click();
  await docs.locator('#project-folder-status').getByText(' (2).pdf', { exact: false }).waitFor();
  const withDuplicate = await savedPdfs();
  assert.equal(withDuplicate.length, 5);
  assert.deepEqual(withDuplicate.filter(([name]) => !name.endsWith(' (2).pdf')), autoSaved);
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
  await desktop.reload();
  await docs.locator('#project-folder-status').getByText('PDF 工作目录：CFL Test PDFs').waitFor();

  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#submission-details-form [name=plannedItems]').fill('2');
  await desktop.locator('#submission-details-form [name=tag]').fill('CZ-01');
  await desktop.locator('#duty-form select[name=family]').selectOption('XC');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#results').getByRole('button', {name: 'XC 50-200', exact: true}).first().click();
  await printCurve('CZ-Curve-Report.pdf');
  await verifyMarkers(desktop);
  await desktop.locator('#export-curve').click();
  await desktop.locator('#selection-folder-status').getByText('Curve/Curve_CZ-01_Test service_XC50-200_2950RPM_V0.pdf', { exact: false }).waitFor();
  const directPdf = await desktop.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const directory = await (await root.getDirectoryHandle('CFL Test PDFs')).getDirectoryHandle('Curve');
    const file = await (await directory.getFileHandle('Curve_CZ-01_Test service_XC50-200_2950RPM_V0.pdf')).getFile();
    return new Promise(resolve => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result.split(',')[1]);
      reader.readAsDataURL(file);
    });
  });
  await writeFile(new URL('direct-curve.pdf', out), Buffer.from(directPdf, 'base64'));
  assert.equal((await savedPdfs('Curve')).length, 1);
  const withoutDetails = await desktop.request.post(url + '/api/pumps/export-pdf', {
    ...request, data: { duty: { flow: 35, head: 55, family: 'XC' }, index: 0 }
  });
  assert.ok(withoutDetails.ok());
  assert.match(withoutDetails.headers()['content-disposition'], /Curve_XC50-200_2950RPM_V0\.pdf/);
  await desktop.locator('#open-submission').click();
  await docs.locator('#transfer-model option[value=CZ50-200]').waitFor({state:'attached'});
  await docs.locator('#new-from-pump').click();
  await docs.locator('#device option').filter({hasText:'CZ50-200'}).waitFor({state:'attached'});
  await docs.locator('#project-folder-status').getByText('PDF 工作目录：CFL Test PDFs').waitFor();
  await fill('motor', { S21: 'YBX3-160M2-2', U21: '160M', V21: '380V' });
  await fill('configuration', { E21: '10版', AA21: 'CZ轻轴', Y21: '11', Z21: '无' });
  const czResponse = desktop.waitForResponse(response => response.url().endsWith('/command/calculate'));
  await docs.locator('#calculate').click();
  const cz = await czResponse;
  assert.ok(cz.ok(), await cz.text());
  assert.match((await cz.json()).rows[0].template, /^CZ/);
  const drawCzResponse = desktop.waitForResponse(response => response.url().endsWith('/command/generate'), { timeout: 120000 });
  await docs.locator('#generate').click();
  const drawCz = await drawCzResponse;
  assert.ok(drawCz.ok(), await drawCz.text());
  assert.deepEqual((await drawCz.json()).files.map(file => file.kind).sort(), ['GA', 'PID', 'SEC']);
  await docs.locator('#creationComplete[open]').waitFor();
  for (const kind of ['GA', 'PID', 'SEC'])
    assert.deepEqual(await savedPdfs(kind), [[kind + '__CZ-01_Test service_CFL100-300_Plan11_YBX3-160M2-2-11kW_V0.pdf', '%PDF-']]);
  await docs.locator('#creationComplete button').click();
  assert.deepEqual(errors, []);

  await desktop.locator('nav [data-view=pumps]').click();
  await desktop.locator('#submission-details-form [name=plannedItems]').fill('1');
  await desktop.locator('#submission-details-form [name=tag]').fill('BATCH-Z');
  await desktop.locator('#duty-form select[name=family]').selectOption('X');
  await subtype.selectOption('XGP');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#results').getByRole('button', { name: 'XGP 50-200', exact: true }).first().click();
  await desktop.locator('#batch-add').click();
  await desktop.locator('#submission-details-form [name=tag]').fill('BATCH-CZ');
  await desktop.locator('#duty-form select[name=family]').selectOption('XC');
  assert.equal(await subtype.isVisible(), false);
  await desktop.locator('#batch-add').click();
  await desktop.locator('#batch-search').click();
  await desktop.locator('#batch-status').getByText('2 / 2', { exact: false }).waitFor();
  await desktop.locator('#batch-export').click();
  await desktop.locator('#batch-status').getByText('已导出 2 项曲线。').waitFor();
  const batchCurves = await savedPdfs('Curve');
  assert.ok(batchCurves.some(([name, signature]) => name.startsWith('Curve_BATCH-CZ_') && signature === '%PDF-'));
  assert.ok(batchCurves.some(([name, signature]) => name.startsWith('Curve_BATCH-Z_') && name.includes('_XGP50-200_') && signature === '%PDF-'));
  await desktop.locator('#batch-transfer').click();
  const modelChoices = docs.locator('#batch-models select');
  await modelChoices.first().selectOption('ZE50-2200');
  assert.equal(await modelChoices.nth(1).inputValue(), 'CZ50-200');
  await docs.locator('#create-batch').click();
  await docs.locator('#device option').filter({ hasText: 'BATCH-CZ' }).waitFor({ state: 'attached' });
  assert.equal(await docs.locator('#device option').count(), 2);
  await docs.locator('#open-batch-settings').click();
  const panels = docs.locator('#batch-settings fieldset');
  for (const [row, [edition, bearing]] of [['8版','QWL'], ['10版','CZ轻轴']].entries()) {
    const panel = panels.nth(row);
    await panel.getByLabel('API 610 图纸版本').selectOption(edition);
    await panel.locator('[data-group=configuration]').getByLabel('轴承形式', { exact: true }).fill(bearing);
    await panel.getByLabel('API 冲洗方案').fill('11');
    await panel.getByLabel('电机系列 / 能效系列').fill('YBX5');
    await panel.getByLabel('机座 / GA 底座号').fill('160M');
    await panel.getByLabel('电机铭牌电压').selectOption('380V');
  }
  const didSave = desktop.waitForResponse(response => response.url().endsWith('/command/save-batch'));
  await docs.locator('#save-batch-settings').click();
  assert.ok((await didSave).ok());
  await docs.locator('#batch-settings').waitFor({ state: 'hidden' });
  const batchCalc = desktop.waitForResponse(response => response.url().endsWith('/command/calculate'));
  await docs.locator('#calculate').click();
  const batchCalculated = await batchCalc;
  assert.ok(batchCalculated.ok(), await batchCalculated.text());
  const batchDraw = desktop.waitForResponse(response => response.url().endsWith('/command/generate'), { timeout: 120000 });
  await docs.locator('#generate').click();
  const batchGenerated = await batchDraw;
  assert.ok(batchGenerated.ok(), await batchGenerated.text());
  assert.equal((await batchGenerated.json()).files.length, 6);
  await docs.locator('#creationComplete[open]').waitFor({ timeout: 120000 });
  assert.match(await docs.locator('#creationSummary').innerText(), /6 个 PDF 保存/);
  await docs.locator('#creationComplete button').click();
  const drawingZip = await desktop.request.get(url + '/api/submissions/pdf-batch');
  assert.ok(drawingZip.ok());
  const zipText = (await drawingZip.body()).toString('latin1');
  assert.ok(zipText.startsWith('PK'));
  for (const kind of ['GA', 'PID', 'SEC'])
    for (const tag of ['BATCH-Z', 'BATCH-CZ'])
      assert.ok(zipText.includes(kind + '/' + kind + '__' + tag));

  for (const device of ['iPhone 13', 'Pixel 7']) {
    const phone = await browser.newPage(devices[device]);
    await phone.goto(url);
    await phone.locator('#duty-form button[type=submit]').click();
    await phone.locator('#plots .screen-plot').last().waitFor({ timeout: 30000 });
    assert.equal(await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, device);
    assert.ok(await phone.getByRole('button', { name: '退出', exact: true }).isVisible());
    await phone.screenshot({ path: new URL(device.replaceAll(' ', '-') + '.png', out).pathname.slice(1), fullPage: true });
    await phone.locator('#open-submission').click();
    const frame = phone.frameLocator('#submission-frame');
    await frame.locator('#duty-transfer').waitFor();
    const state = await phone.request.post(url + '/api/submissions/command/state', { ...request, data: {} });
    assert.equal((await state.json()).job, null, 'Independent browser sessions should start with no open project.');
    assert.equal(await frame.locator('body').evaluate(body => body.scrollWidth <= innerWidth + 1), true, device + ' SDOC');
    await phone.close();
  }
  const exit = desktop.getByRole('button', { name: '退出', exact: true });
  desktop.once('dialog', dialog => dialog.dismiss());
  await exit.click();
  assert.ok(await exit.isVisible());
  await desktop.evaluate(() => { window.close = () => {}; });
  desktop.once('dialog', dialog => dialog.accept());
  await exit.click();
  await desktop.getByRole('heading', { name: '已退出工作台' }).waitFor();
  assert.equal(await desktop.evaluate(() => sessionStorage.getItem('cfl.selectedDuty')), null);
  assert.ok((await (await desktop.request.post(url + '/api/submissions/command/projects', { ...request, data: {} })).json()).length > 0);
  await desktop.getByRole('link', { name: '重新打开工作台' }).click();
  await desktop.locator('#duty-form').waitFor();
  await desktop.locator('#duty-form select[name=family]').selectOption('A');
  await desktop.locator('#duty-form [name=flow]').fill('30');
  await desktop.locator('#duty-form [name=head]').fill('150');
  await desktop.locator('#duty-form [name=rpm]').fill('2980');
  await desktop.locator('#duty-form button[type=submit]').click();
  await desktop.locator('#results').getByRole('button', { name: 'A 23-40', exact: true }).waitFor();
  assert.equal(await desktop.locator('#trim-preview, #trim-graphs').count(), 0);
  await desktop.locator('#selection-original').filter({ hasText: '叶轮直径为插值' }).waitFor();
  await desktop.locator('#metrics').filter({ hasText: '308.79' }).waitFor();
  await desktop.locator('#submission-details-form [name=tag]').fill('');
  await desktop.locator('#submission-details-form [name=service]').fill('');
  await desktop.locator('#submission-details-form [name=plannedItems]').fill('1');
  const aName = 'Curve_A23-40_2980RPM_V0.pdf';
  await desktop.locator('#export-curve').click();
  await desktop.locator('#selection-folder-status').getByText(aName, { exact: false }).waitFor();
  const aPdf = await desktop.evaluate(async name => {
    const folder = await (await navigator.storage.getDirectory()).getDirectoryHandle('CFL Test PDFs');
    const file = await (await folder.getFileHandle(name)).getFile();
    return Array.from(new Uint8Array(await file.arrayBuffer()));
  }, aName);
  assert.equal(Buffer.from(aPdf).subarray(0, 5).toString(), '%PDF-');
  await writeFile(new URL(aName, out), Buffer.from(aPdf));
  await desktop.locator('nav [data-view=catalog]').click();
  await desktop.locator('#catalog-form select[name=family]').selectOption('APP');
  await desktop.locator('#catalog-form input[name=query]').fill('K14130');
  await desktop.locator('#catalog-form button[type=submit]').click();
  const appRecord = desktop.locator('#catalog-results tr').filter({ hasText: 'K14130' }).first();
  await appRecord.getByText('53-200', { exact: true }).waitFor();
  await appRecord.locator('summary').click();
  await appRecord.getByText('250-200-500', { exact: false }).waitFor();
  console.log('Passed desktop and phone layouts, A4 curve data sheet, Z and CZ handoff, motor specification and rating locks, calculation, CAD preview, DWG and PDF generation.');
} finally { await browser.close(); }
