import { fillReport } from './pump-report.js';
import { supportsProjectFolders, chooseDirectory, storedFolder, checkDirectory, savePdf } from './pdf-files.js';
import { mountBatch } from './pump-batch.js';

const $ = (selector) => document.querySelector(selector);
const number = (value, digits = 2) => value == null ? '—' : value.toLocaleString('en-US', { maximumFractionDigits: digits });
const form = $('#duty-form');
const fields = form.elements;
const projectForm = $('#submission-details-form');
const draftKey = sessionStorage.getItem('cfl.exportDraft') ??
  'draft:' + [...crypto.getRandomValues(new Uint32Array(4))].join('-');
sessionStorage.setItem('cfl.exportDraft', draftKey);
let curveDirectory = null;
let directoryVersion = 0;
let exporting = false;
const folderStatus = $('#selection-folder-status');
try {
  const projectFields = JSON.parse(sessionStorage.getItem('cfl.projectFields') || '{}');
  for (const [name, value] of Object.entries(projectFields))
    if (projectForm.elements.namedItem(name)) projectForm.elements.namedItem(name).value = value;
} catch {}
projectForm.oninput = () => sessionStorage.setItem('cfl.projectFields', JSON.stringify(Object.fromEntries(new FormData(projectForm))));
const itemCount = () => Math.max(Number(projectForm.elements.plannedItems.value) || 1,
  Number(sessionStorage.getItem('cfl.linkedItems')) || 1, batch.count());

function projectDetails() {
  const details = Object.fromEntries([...new FormData(projectForm)]
    .filter(([name, value]) => name !== 'plannedItems' && value.trim() !== '')
    .map(([name, value]) => [name, value.trim()]));
  if (details.quantity) details.quantity = Number(details.quantity);
  return details;
}

async function loadCurveFolder() {
  if (!supportsProjectFolders) return;
  const version = ++directoryVersion;
  const saved = await storedFolder(draftKey).catch(() => null);
  if (version !== directoryVersion) return;
  curveDirectory = saved?.directory ?? null;
  folderStatus.textContent = curveDirectory ? '工作目录：' + curveDirectory.name : '';
}

async function selectCurveFolder() {
  const selected = await chooseDirectory();
  ++directoryVersion;
  curveDirectory = selected;
  await storedFolder(draftKey, { directory: selected }).catch(() => {});
  folderStatus.textContent = '工作目录：' + selected.name;
  $('#submission-frame').contentWindow?.postMessage({ type: 'cfl-folder-selected', key: draftKey }, location.origin);
  return selected;
}

$('#selection-folder').hidden = !supportsProjectFolders;
$('#selection-folder').onclick = async () => {
  try { await selectCurveFolder(); }
  catch (error) { if (error.name !== 'AbortError') announce('无法选择工作目录，请检查写入权限。'); }
};
window.addEventListener('message', event => {
  const update = event.data;
  if (event.origin !== location.origin || event.source !== $('#submission-frame').contentWindow ||
      update?.type !== 'cfl-project-layout' || update.key !== draftKey) return;
  if (Number.isInteger(update.items)) sessionStorage.setItem('cfl.linkedItems', String(update.items));
  if (update.directoryChanged) loadCurveFolder();
});
loadCurveFolder();
let previousDuty, selected, searchVersion = 0, curveVersion = 0, catalogVersion = 0;
let printData, trimPreviewDuty;
let catalogPage = 1, catalogLoaded = false, installPrompt;

async function api(url, body) {
  const response = await fetch(url, body ? { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CFL-Request': '1' }, body: JSON.stringify(body) } : {});
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}));
    throw new Error(detail.error || '无法完成请求，请检查连接和输入。');
  }
  return response.json();
}

function text(tag, value) {
  const element = document.createElement(tag);
  element.textContent = value;
  return element;
}

function announce(message = '') {
  $('#pump-message').textContent = message;
  $('#pump-message').hidden = !message;
}

function showView() {
  const hash = location.hash.slice(1);
  const exited = new URLSearchParams(location.search).has('exited');
  const view = exited ? 'exited' : ['pumps', 'catalog', 'submissions'].includes(hash) ? hash : 'pumps';
  $('.masthead').hidden = exited;
  for (const panel of document.querySelectorAll('main')) panel.hidden = panel.id !== view;
  for (const link of document.querySelectorAll('nav a')) {
    if (link.dataset.view === view) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  if (view === 'catalog' && !catalogLoaded) loadCatalog();
  if (view === 'submissions') {
    const frame = $('#submission-frame');
    if (!frame.getAttribute('src')) frame.src = '/submissions.html';
    else frame.contentWindow?.postMessage('cfl-update-duty', location.origin);
  }
}
window.addEventListener('hashchange', showView);

async function init() {
  showView();
  if (!$('#exited').hidden) return;
  try {
    const options = await api('/api/pumps/options');
    for (const family of options.families) fields.family.add(new Option(family));
    for (const type of options.apiTypes) fields.apiType.add(new Option(type));
    for (const subtype of options.xSubtypes) fields.xSubtype.add(new Option(subtype));
    syncXSubtype();
  } catch (error) { announce(error.message); }
}
window.addEventListener('DOMContentLoaded', () => window.lucide?.createIcons());

function clearDetails() {
  selected = undefined;
  printData = undefined;
  $('#pump-report').replaceChildren();
  $('#selection-title').textContent = '性能曲线';
  $('#selection-subtitle').textContent = '';
  $('#selection-original').textContent = '';
  $('#selection-original').hidden = true;
  $('#metrics').hidden = true;
  $('#print').disabled = true;
  $('#export-curve').disabled = true;
  $('#plots').replaceChildren();
}

function syncXSubtype() {
  const isX = fields.family.value === 'X';
  $('#x-subtype-label').hidden = !isX;
  fields.xSubtype.disabled = !isX;
  if (!isX) fields.xSubtype.value = '';
}

function invalidateSelection() {
  ++searchVersion;
  ++curveVersion;
  previousDuty = undefined;
  clearDetails();
  announce();
  $('#results').replaceChildren();
  clearTrimPreview();
  $('#count').textContent = '';
  $('#empty').textContent = '输入工况后开始选型。';
  $('#empty').hidden = false;
}

fields.family.addEventListener('change', () => {
  const wasX = !fields.xSubtype.disabled;
  syncXSubtype();
  if (wasX !== !fields.xSubtype.disabled) invalidateSelection();
});
fields.xSubtype.addEventListener('change', invalidateSelection);

const readDuty = () => Object.fromEntries([...new FormData(form)].filter(([, value]) => value !== '')
    .map(([key, value]) => [key, ['family', 'apiType', 'xSubtype'].includes(key) ? value : Number(value)]));
const viscous = duty => duty.viscosityCp != null && duty.viscosityCp * 1000 / duty.density > 1;

function clearTrimPreview() {
  trimPreviewDuty = undefined;
  $('#trim-preview').hidden = true;
  $('#trim-graphs').hidden = true;
  $('#export-trim-data').disabled = true;
}

form.addEventListener('input', clearTrimPreview);

async function showTrimPreview(duty, version) {
  try {
    const data = await api('/api/pumps/trim-preview', duty);
    if (version !== searchVersion || JSON.stringify(duty) !== JSON.stringify(readDuty()) || !data.length) return;
    const sourceNames = { pdf: '原图', 'supplied-reference': '参考文件',
      interpolated: '插值', 'interpolated-with-reference': '参考插值' };
    $('#trim-curve-comparisons').replaceChildren(...data.map(curve => {
      const group = document.createElement('div');
      const title = text('h3', curve.series + ' ' + curve.size + ' · ' + curve.curve + ' · ' +
        curve.rpm + ' rpm · ' + number(curve.flow) + ' m³/h');
      const citation = text('p', '原图第 ' + curve.sourcePage + ' 页 · ' + curve.sourceRpm + ' rpm' +
        (curve.rpm === curve.sourceRpm ? '' : ' → ' + curve.rpm + ' rpm（换速）'));
      citation.className = 'trim-preview-source';
      const holder = document.createElement('div');
      holder.className = 'table-scroll';
      const table = document.createElement('table');
      const heading = table.createTHead().insertRow();
      heading.append(...['叶轮 mm', '扬程 m', '差值 m', '效率 %', 'NPSHr m', 'NPSHr 来源'].map(label => text('th', label)));
      const body = table.createTBody();
      for (const point of curve.points) {
        const row = body.insertRow();
        row.dataset.source = point.npshrSource ?? '';
        const difference = point.head == null ? null : Math.round((point.head - curve.requestedHead) * 100) / 100;
        const source = sourceNames[point.npshrSource] ?? '—';
        row.append(...['Ø' + number(point.diameter), number(point.head),
          (difference > 0 ? '+' : '') + number(difference), number(point.efficiency),
          number(point.npshr), source].map(value => text('td', value)));
      }
      holder.append(table);
      group.append(title, citation, holder);
      return group;
    }));
    const plotLabels = ['流量–扬程', '流量–效率', '流量–NPSHr'];
    $('#trim-graph-list').replaceChildren(...data.flatMap(curve => curve.plots.map((svg, index) => {
      const figure = document.createElement('figure');
      const image = new Image();
      image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
      image.alt = curve.series + ' ' + curve.size + ' ' + plotLabels[index] + '（原图及换算）';
      figure.append(image);
      return figure;
    })));
    trimPreviewDuty = duty;
    $('#trim-preview').hidden = false;
    $('#trim-graphs').hidden = false;
    $('#export-trim-data').disabled = false;
    if (!$('#empty').hidden) $('#empty').textContent = '原始曲线未匹配该工况。';
  } catch (error) { if (version === searchVersion) announce(error.message); }
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!projectForm.checkValidity()) { $('#submission-details').open = true; projectForm.reportValidity(); return; }
  const version = ++searchVersion;
  ++curveVersion;
  const duty = readDuty();
  clearDetails();
  announce();
  $('#results').replaceChildren();
  clearTrimPreview();
  $('#empty').hidden = true;
  $('#count').textContent = '正在选型…';
  try {
    const results = await api('/api/pumps/search', duty);
    if (version !== searchVersion) return;
    previousDuty = duty;
    if (duty.family === 'A' && !viscous(duty)) showTrimPreview(duty, version);
    $('#count').textContent = results.length + ' 个结果';
    $('#empty').hidden = results.length > 0;
    $('#empty').textContent = viscous(duty)
      ? '在适用的黏度修正范围内未找到匹配泵型。请核对工况或向制造商确认。'
      : '当前工况无匹配泵型。';
    for (const pump of results) {
      const row = document.createElement('tr');
      row.setAttribute('aria-selected', 'false');
      const name = pump.modelSeries + ' ' + pump.size;
      const first = document.createElement('td');
      const button = text('button', name);
      button.type = 'button';
      first.append(button);
      row.append(first, ...[pump.stages, number(pump.rpm, 0), number(pump.diameter), number(pump.efficiency),
        number(pump.npshr), number(pump.power)].map(value => text('td', value)));
      row.addEventListener('click', () => showPump(pump, row, duty));
      $('#results').append(row);
    }
    if (results.length) {
      const index = results.findIndex(pump => pump.index === batch.preferred(duty));
      const preferred = Math.max(0, index);
      showPump(results[preferred], $('#results').rows[preferred], duty);
    }
  } catch (error) {
    if (version !== searchVersion) return;
    $('#count').textContent = '';
    announce(error.message);
  }
});

async function showPump(pump, row, duty) {
  const version = ++curveVersion;
  clearDetails();
  selected = pump;
  batch.setPump(pump, duty);
  $('#results tr[aria-selected="true"]')?.setAttribute('aria-selected', 'false');
  row.setAttribute('aria-selected', 'true');
  $('#selection-title').textContent = pump.modelSeries + ' ' + pump.size;
  $('#selection-subtitle').textContent = [pump.apiType, pump.stages + ' 级', pump.rpm + ' rpm',
    pump.flowType && '流型 ' + pump.flowType, pump.group && '水力组 ' + pump.group, pump.curve].filter(Boolean).join(' · ');
  const source = pump.sourceRpm ? [
    '原图 ' + pump.sourceRpm + ' rpm', pump.sourceRpm !== pump.rpm && '已换算转速',
    pump.interpolatedTrim && '叶轮直径为插值', 'NPSHr 估算，需向厂家核实'
  ].filter(Boolean).join(' · ') : '';
  $('#selection-original').textContent = [pump.originalCurve && '原曲线编号：' + pump.originalCurve, source]
    .filter(Boolean).join(' · ');
  $('#selection-original').hidden = !$('#selection-original').textContent;
  $('#metrics').replaceChildren(...[
    ['额定流量', number(duty.flow) + ' m³/h'], ['扬程（要求 / 实际）', number(duty.head) + ' / ' + number(pump.head) + ' m'],
    ['最小 / 额定 / 最大叶轮', [pump.minDiameter, pump.diameter, pump.maxDiameter].map(x => number(x)).join(' / ') + ' mm'],
    ['轴功率 / 推荐电机', number(pump.power) + ' / ' + number(pump.motorKw) + ' kW'],
    ['效率', number(pump.efficiency) + ' %'], [pump.sourceRpm || viscous(duty) ? 'NPSHr（估算）' : 'NPSHr', number(pump.npshr) + ' m'],
    ['吸入型式', pump.suction || '—'], ['布置方式', pump.orientation || '—']
  ].map(([label, value]) => { const field = document.createElement('div'); field.append(text('dt', label), text('dd', value)); return field; }));
  $('#metrics').hidden = false;
  $('#plots').append(text('p', '正在绘制性能曲线…'));
  $('#plots').firstChild.className = 'empty';
  try {
    const width = Math.max(410, Math.round($('#plots').clientWidth));
    const result = await api('/api/pumps/curves', { duty, index: pump.index, width });
    if (version !== curveVersion) return;
    printData = { pump: result.pump, duty, report: result.report };
    const labels = ['流量 – 扬程与效率', '流量 – NPSHr', '流量 – 轴功率'];
    $('#plots').replaceChildren(...result.plots.map((svg, index) => {
      const figure = document.createElement('figure');
      const image = new Image();
      image.className = 'screen-plot';
      image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
      image.alt = labels[index];
      const printed = new Image();
      printed.className = 'print-plot';
      printed.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(result.printPlots?.[index] ?? svg);
      printed.alt = labels[index];
      figure.append(image, printed);
      return figure;
    }));
    $('#print').disabled = false;
    $('#export-curve').disabled = exporting;
  } catch (error) {
    if (version === curveVersion) $('#plots').firstChild.textContent = error.message;
  }
}

window.addEventListener('beforeprint', () => {
  if (!printData) return;
  fillReport($('#pump-report'), printData.pump, printData.duty, printData.report,
    new FormData($('#submission-details-form')));
});
$('#print').onclick = () => window.cflOfflineSave ? $('#export-curve').click() : window.print();
const exportRequest = (path, data) => fetch(path, { method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-CFL-Request': '1' }, body: JSON.stringify(data) });

async function downloadExport(response, fallback) {
  if (!response.ok) throw new Error('无法生成 PDF，请重新选型后重试。');
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const utf8 = disposition.match(/filename\*=UTF-8''([^;]+)/i);
  const name = utf8 ? decodeURIComponent(utf8[1]) : disposition.match(/filename="?([^";]+)"?/i)?.[1] ?? fallback;
  return { name, blob: await response.blob() };
}

function downloadBlob({ name, blob }) {
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 30000);
}

$('#export-trim-data').onclick = async () => {
  const duty = trimPreviewDuty;
  if (!duty) return;
  $('#export-trim-data').disabled = true;
  try {
    const path = '/api/pumps/export-trim-data';
    if (window.cflOfflineSave) await window.cflOfflineSave(path, duty);
    else {
      const response = await exportRequest(path, duty);
      if (!response.ok) throw new Error('无法导出叶轮估算数据。');
      downloadBlob(await downloadExport(response, 'Impeller_Trim_Curves.json'));
    }
  } catch (error) { if (error.name !== 'AbortError') announce(error.message); }
  finally { if (trimPreviewDuty === duty) $('#export-trim-data').disabled = false; }
};

async function exportCurves(items, count) {
  if (exporting) throw new Error('另一个导出正在进行。');
  exporting = true;
  $('#export-curve').disabled = true;
  $('#selection-folder').disabled = true;
  try {
    if (window.cflOfflineSave) {
      const multiple = items.length > 1;
      await window.cflOfflineSave(multiple ? '/api/pumps/export-batch' : '/api/pumps/export-pdf',
        multiple ? items : items[0]);
      return;
    }
    const directory = supportsProjectFolders ?
      (curveDirectory ? await checkDirectory(curveDirectory) : await selectCurveFolder()) : null;
    if (!directory && items.length > 1) {
      downloadBlob(await downloadExport(await exportRequest('/api/pumps/export-batch', items), 'Curves_V0.zip'));
      return;
    }
    let saved = 0;
    const failed = [];
    for (const item of items) {
      try {
        const file = await downloadExport(await exportRequest('/api/pumps/export-pdf', item), 'Curve.pdf');
        if (directory) {
          const name = await savePdf(directory, file.name.replace(/\.pdf$/i, ''), file.blob,
            count > 1 ? 'Curve' : undefined);
          folderStatus.textContent = '已保存：' + directory.name + '/' + name;
        } else downloadBlob(file);
        saved++;
      } catch (error) { failed.push((item.details.tag || '设备 ' + (saved + failed.length + 1)) + '：' + error.message); }
    }
    if (failed.length) throw new Error('已导出 ' + saved + ' 项。' + failed.join('；'));
  } finally {
    exporting = false;
    $('#export-curve').disabled = !printData;
    $('#selection-folder').disabled = false;
  }
}

$('#export-curve').onclick = async () => {
  if (!printData || exporting) return;
  if (!projectForm.checkValidity()) { $('#submission-details').open = true; projectForm.reportValidity(); return; }
  const { pump, duty } = printData;
  announce();
  try { await exportCurves([{ duty, index: pump.index, details: projectDetails() }], itemCount()); }
  catch (error) { if (error.name !== 'AbortError') announce(error.message); }
};
$('#submission-details-form').onsubmit = event => event.preventDefault();
$('#open-submission').onclick = () => {
  sessionStorage.removeItem('cfl.selectedBatch');
  if (selected) {
    if (!projectForm.checkValidity()) {
      $('#submission-details').open = true;
      projectForm.reportValidity();
      return;
    }
    sessionStorage.setItem('cfl.selectedDuty', JSON.stringify({ pump: selected, duty: previousDuty,
      details: projectDetails(), folderKey: draftKey, plannedItems: itemCount() }));
  } else sessionStorage.removeItem('cfl.selectedDuty');
  location.hash = 'submissions';
};

function withProjectDetails(item) {
  const { project, client, customerOrder, company } = projectDetails();
  return { ...item.details, project, client, customerOrder, company };
}

const batch = mountBatch({
  readItem() {
    if (!form.reportValidity()) return null;
    if (!projectForm.checkValidity()) { $('#submission-details').open = true; projectForm.reportValidity(); return null; }
    const duty = readDuty();
    return { duty, details: projectDetails(),
      pump: JSON.stringify(duty) === JSON.stringify(previousDuty) ? selected ?? null : null };
  },
  openItem(item) {
    const optional = ['tag', 'service', 'quantity', 'liquid', 'material', 'drawingModel'];
    for (const name of optional) projectForm.elements[name].value = item.details[name] ?? '';
    projectForm.oninput();
    for (const input of fields) if (input.name) input.value = item.duty[input.name] ?? '';
    syncXSubtype();
    form.requestSubmit();
  },
  search: duty => api('/api/pumps/search', duty),
  exportItems: items => exportCurves(items.map(item => ({ duty: item.duty, index: item.pump.index,
    details: withProjectDetails(item) })), itemCount()),
  transfer(items) {
    const selections = items.map(item => ({ duty: item.duty, index: item.pump.index,
      pump: item.pump, details: withProjectDetails(item) }));
    sessionStorage.removeItem('cfl.selectedDuty');
    sessionStorage.setItem('cfl.selectedBatch', JSON.stringify({ items: selections, folderKey: draftKey,
      plannedItems: itemCount() }));
    location.hash = 'submissions';
  }
});

async function loadCatalog() {
  const version = ++catalogVersion;
  const form = $('#catalog-form');
  $('#catalog-count').textContent = '正在查询…';
  const query = new URLSearchParams(new FormData(form));
  query.set('page', catalogPage);
  try {
    const data = await api('/api/pumps/catalog?' + query);
    if (version !== catalogVersion) return;
    if (!catalogLoaded) for (const family of data.families) form.elements.family.add(new Option(family));
    catalogLoaded = true;
    $('#catalog-count').textContent = data.total + ' 条曲线';
    const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
    $('#catalog-page').textContent = catalogPage + ' / ' + pages;
    $('#previous').disabled = catalogPage <= 1;
    $('#next').disabled = catalogPage >= pages;
    $('#catalog-results').replaceChildren(...data.records.map(curve => {
      const row = document.createElement('tr');
      const notes = [...(curve.availableForSelection ? [] : ['未开放选型']), ...curve.issues];
      row.append(...[curve.series, curve.size, curve.curveNumber, curve.originalCurveNumber, number(curve.rpm, 0),
        number(curve.minDiameterMm) + ' / ' + number(curve.maxDiameterMm),
        curve.points, notes.join(' · ') || '—'].map(value => text('td', value)));
      return row;
    }));
  } catch (error) { if (version === catalogVersion) $('#catalog-count').textContent = error.message; }
}
$('#catalog-form').onsubmit = (event) => { event.preventDefault(); catalogPage = 1; loadCatalog(); };
$('#previous').onclick = () => { if (catalogPage > 1) { catalogPage--; loadCatalog(); } };
$('#next').onclick = () => { catalogPage++; loadCatalog(); };

window.addEventListener('beforeinstallprompt', (event) => {
  event.preventDefault();
  installPrompt = event;
  $('#install').hidden = false;
});
$('#install').onclick = async () => {
  if (!installPrompt) return;
  await installPrompt.prompt();
  installPrompt = undefined;
  $('#install').hidden = true;
};
$('#exit').onclick = () => {
  if (!window.confirm('退出工作台？已保存的项目会保留，未保存的输入将丢失。')) return;
  sessionStorage.removeItem('cfl.selectedDuty');
  sessionStorage.removeItem('cfl.projectFields');
  sessionStorage.removeItem('cfl.exportDraft');
  sessionStorage.removeItem('cfl.linkedItems');
  sessionStorage.removeItem('cfl.selectedBatch');
  sessionStorage.removeItem('cfl.pumpBatch');
  window.close();
  // Browsers can prevent an app from closing a tab that the user opened.
  location.replace('/?exited');
};
if (!window.cflOfflineSave && 'serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(console.warn);
init();
