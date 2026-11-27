import { fillReport } from './pump-report.js';
import { supportsProjectFolders, chooseDirectory, storedFolder, checkDirectory, savePdf } from './pdf-files.js';
import { mountBatch, samePump, dutyKey } from './pump-batch.js';

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
const exportStatus = $('#curve-export-status');
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
let printData;
let catalogRating, ratingLoading = false, selectedRow;
const ratingForm = $('#rating-form');
let catalogPage = 1, catalogLoaded = false, installPrompt;
let pumpOptionsReady = false, pumpOptionsLoad;

class PumpConnectionError extends Error {}

function connectionError() {
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  return new PumpConnectionError(local
    ? '无法连接本机工作台服务。请从 Windows 开始菜单重新打开 CFL 工程工作台，或启动正在开发的工作台，然后重试。'
    : '无法连接工作台服务。请检查网络和工作台地址，然后重试。');
}

function setPumpConnection(ready, message = '', loading = false) {
  pumpOptionsReady = ready;
  $('#search-pumps').disabled = !ready;
  for (const select of [fields.family, fields.apiType])
    select.disabled = !ready && select.options.length === 1;
  $('#pump-connection').hidden = !message;
  $('#pump-connection-status').textContent = message;
  $('#retry-pump-options').disabled = loading;
}

async function api(url, body, signal) {
  let response;
  try {
    response = await fetch(url, body ? { method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CFL-Request': '1' }, body: JSON.stringify(body), signal } : { signal });
  } catch {
    const error = connectionError();
    if (!pumpOptionsLoad) setPumpConnection(false, error.message);
    throw error;
  }
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}));
    if ([502, 503, 504].includes(response.status)) {
      const error = connectionError();
      if (!pumpOptionsLoad) setPumpConnection(false, error.message);
      throw error;
    }
    throw new Error(detail?.error || '请求失败（HTTP ' + response.status + '）。');
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

function loadPumpOptions() {
  if (pumpOptionsLoad) return pumpOptionsLoad;
  setPumpConnection(false, '正在连接工作台并加载泵系列…', true);
  pumpOptionsLoad = (async () => {
    const controller = new AbortController();
    const timeout = window.cflOfflineSave ? undefined : setTimeout(() => controller.abort(), 15000);
    try {
      const options = await api('/api/pumps/options', undefined, controller.signal);
      if (!options?.families?.length || ![options.families, options.apiTypes, options.xSubtypes]
          .every(list => Array.isArray(list) && list.every(value => typeof value === 'string'))) {
        throw new Error('服务未返回有效的泵系列。');
      }
      for (const [select, values] of [[fields.family, options.families],
        [fields.apiType, options.apiTypes], [fields.xSubtype, options.xSubtypes]]) {
        const current = select.value;
        select.options.length = 1;
        for (const value of values) select.add(new Option(value));
        select.value = values.includes(current) ? current : '';
      }
      fields.clearance.querySelector('option[value="Hot"]').textContent =
        options.hotClearanceAvailable ? 'Hot' : 'Hot（暂无曲线）';
      syncXSubtype();
      setPumpConnection(true);
      return true;
    } catch (error) {
      const message = error instanceof PumpConnectionError ? error.message :
        '无法加载泵系列。' + (error instanceof SyntaxError ? '服务未返回有效的目录数据。' : error.message);
      setPumpConnection(false, message);
      return false;
    } finally {
      clearTimeout(timeout);
      pumpOptionsLoad = undefined;
    }
  })();
  return pumpOptionsLoad;
}
$('#retry-pump-options').onclick = loadPumpOptions;
window.addEventListener('online', () => {
  if (!pumpOptionsReady && $('#exited').hidden) loadPumpOptions();
});
window.addEventListener('DOMContentLoaded', () => window.lucide?.createIcons());

function clearDetails() {
  selected = undefined;
  catalogRating = undefined;
  selectedRow = undefined;
  ratingLoading = false;
  printData = undefined;
  batch.refreshConfirmation();
  $('#pump-report').replaceChildren();
  $('#selection-title').textContent = '性能曲线';
  $('#selection-subtitle').textContent = '';
  $('#selection-original').textContent = '';
  $('#selection-original').hidden = true;
  $('#metrics').hidden = true;
  ratingForm.hidden = true;
  $('#rating-status').textContent = '';
  showExportStatus();
  $('#print').disabled = true;
  $('#export-curve').disabled = true;
  $('#open-submission').disabled = false;
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
fields.stages.addEventListener('change', invalidateSelection);
fields.clearance.addEventListener('change', invalidateSelection);

const readDuty = () => Object.fromEntries([...new FormData(form)].filter(([, value]) => value !== '')
    .map(([key, value]) => [key, ['family', 'apiType', 'xSubtype', 'clearance'].includes(key) ? value : Number(value)]));
const viscous = duty => duty.viscosityCp != null && duty.viscosityCp * 1000 / duty.density > 1;

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!pumpOptionsReady) return;
  if (!projectForm.checkValidity()) { $('#submission-details').open = true; projectForm.reportValidity(); return; }
  const version = ++searchVersion;
  ++curveVersion;
  const duty = readDuty();
  clearDetails();
  announce();
  $('#results').replaceChildren();
  $('#empty').hidden = true;
  $('#count').textContent = '正在选型…';
  try {
    const results = await api('/api/pumps/search', duty);
    if (version !== searchVersion) return;
    previousDuty = duty;
    $('#count').textContent = results.length + ' 个结果';
    $('#empty').hidden = results.length > 0;
    const restrictions = [duty.stages != null && duty.stages + ' 级', duty.clearance].filter(Boolean).join(' / ');
    $('#empty').textContent = restrictions
      ? '当前工况在指定条件（' + restrictions + '）下无匹配泵型。'
      : viscous(duty)
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
      const preferredPump = batch.preferred(duty);
      const index = results.findIndex(pump => samePump(pump, preferredPump));
      const preferred = Math.max(0, index);
      showPump(results[preferred], $('#results').rows[preferred], duty);
    }
  } catch (error) {
    if (version !== searchVersion) return;
    $('#count').textContent = '';
    if (!(error instanceof PumpConnectionError)) announce(error.message);
  }
});

function showPump(pump, row, duty) {
  clearDetails();
  catalogRating = selected = pump;
  selectedRow = row;
  $('#results tr[aria-selected="true"]')?.setAttribute('aria-selected', 'false');
  row.setAttribute('aria-selected', 'true');
  $('#selection-title').textContent = pump.modelSeries + ' ' + pump.size;
  const limits = pump.stageLimits;
  const range = limits && (limits.minimum === limits.maximum ? limits.minimum : limits.minimum + '–' + limits.maximum);
  $('#selection-subtitle').textContent = [pump.apiType, pump.stages + ' 级', pump.rpm + ' rpm',
    range && '目录级数 ' + range, pump.clearance && '间隙 ' + pump.clearance,
    pump.flowType && '流型 ' + pump.flowType, pump.group && '水力组 ' + pump.group, pump.curve].filter(Boolean).join(' · ');
  const source = pump.sourceRpm ? [
    (pump.sourcePage ? '原图第 ' + pump.sourcePage + ' 页' : '原图') + ' · ' + pump.sourceRpm + ' rpm',
    pump.sourceRpm !== pump.rpm && '已换算转速',
    pump.interpolatedTrim && '叶轮直径为插值',
    pump.npshrSource === 'affinity-estimated' ? 'NPSHr 按相似定律估算，需向厂家核实' :
    pump.npshrSource === 'pdf-shared' ? '共用 NPSHr 曲线，需核实所选叶轮' : 'NPSHr 估算，需向厂家核实'
  ].filter(Boolean).join(' · ') : '';
  $('#selection-original').textContent = [pump.originalCurve && '原曲线编号：' + pump.originalCurve,
    pump.sharedHydraulicApiType && '共用 ' + pump.sharedHydraulicApiType + ' 水力曲线，仅供初选', source]
    .filter(Boolean).join(' · ');
  $('#selection-original').hidden = !$('#selection-original').textContent;
  renderRating(pump, duty);
  $('#plots').append(text('p', '正在绘制性能曲线…'));
  $('#plots').firstChild.className = 'empty';
  const saved = batch.preferred(duty);
  loadRating(duty, samePump(pump, saved) ? saved.ratingOverride : undefined);
}

function renderRating(pump, duty) {
  const ratingField = (key, label) => {
    const input = document.createElement('input');
    input.type = 'number';
    input.name = key;
    input.min = '.000001';
    input.step = 'any';
    if (key === 'efficiency') input.max = '100';
    input.className = 'rating-input';
    input.setAttribute('form', ratingForm.id);
    input.setAttribute('aria-label', label);
    input.placeholder = number(catalogRating[key]);
    input.title = '计算值：' + input.placeholder;
    input.value = pump.ratingOverride?.[key] ?? '';
    return [label + (input.value ? '（手动）' : ''), input];
  };
  $('#metrics').replaceChildren(...[
    ['额定流量', number(duty.flow) + ' m³/h'], ['扬程（要求 / 实际）', number(duty.head) + ' / ' + number(pump.head) + ' m'],
    ['最小 / 额定 / 最大叶轮', [pump.minDiameter, pump.diameter, pump.maxDiameter].map(x => number(x)).join(' / ') + ' mm'],
    ['轴功率 / 推荐电机', number(pump.power) + ' / ' + number(pump.motorKw) + ' kW'],
    ratingField('efficiency', '效率 (%)'),
    ratingField('npshr', !pump.ratingOverride?.npshr && (pump.sourceRpm || viscous(duty)) ? 'NPSHr（估算, m）' : 'NPSHr (m)'),
    ['吸入型式', pump.suction || '—'], ['布置方式', pump.orientation || '—']
  ].map(([label, value]) => {
    const field = document.createElement('div');
    const definition = document.createElement('dd');
    definition.append(value);
    field.append(text('dt', label), definition);
    return field;
  }));
  for (const [index, key] of [[4, 'efficiency'], [5, 'npshr'], [6, 'power']])
    selectedRow.cells[index].textContent = number(pump[key]);
  $('#metrics').hidden = false;
  ratingForm.hidden = false;
}

function syncRatingControls() {
  const locked = !selected || ratingLoading || dutyKey(readDuty()) !== dutyKey(previousDuty);
  for (const input of ratingForm.elements) if (input.matches('input')) input.disabled = locked;
  $('#apply-rating').disabled = locked;
  $('#reset-rating').disabled = locked || !selected.ratingOverride;
  $('#print').disabled = ratingLoading || !printData;
  $('#export-curve').disabled = ratingLoading || exporting || !printData;
  $('#open-submission').disabled = ratingLoading;
  batch.refreshConfirmation();
}

async function loadRating(duty, ratingOverride, editing = false) {
  const version = ++curveVersion;
  ratingLoading = true;
  if (editing) $('#rating-status').textContent = '正在更新…';
  syncRatingControls();
  try {
    const width = Math.max(410, Math.round($('#plots').clientWidth));
    const result = await api('/api/pumps/curves', { duty, index: catalogRating.index, width, ratingOverride });
    if (version !== curveVersion) return;
    selected = result.pump;
    printData = { pump: result.pump, duty, report: result.report };
    renderRating(selected, duty);
    $('#rating-status').textContent = selected.ratingOverride ? '已应用手动修正' : editing ? '已恢复计算值' : '';
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
  } catch (error) {
    if (version !== curveVersion) return;
    $('#rating-status').textContent = error.message;
    if (!printData) $('#plots').firstChild.textContent = error.message;
  } finally {
    if (version === curveVersion) {
      ratingLoading = false;
      syncRatingControls();
    }
  }
}

ratingForm.onsubmit = event => {
  event.preventDefault();
  if ($('#apply-rating').disabled) return;
  const rating = Object.fromEntries([...new FormData(ratingForm)].filter(([, value]) => value !== '')
    .map(([name, value]) => [name, Number(value)]));
  loadRating(previousDuty, rating, true);
};
$('#reset-rating').onclick = () => {
  if (!$('#reset-rating').disabled) loadRating(previousDuty, null, true);
};

window.addEventListener('beforeprint', () => {
  if (!printData) return;
  fillReport($('#pump-report'), printData.pump, printData.duty, printData.report,
    new FormData($('#submission-details-form')));
});
$('#print').onclick = () => window.cflOfflineSave ? $('#export-curve').click() : window.print();
const exportRequest = (path, data) => fetch(path, { method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-CFL-Request': '1' }, body: JSON.stringify(data) });

async function downloadExport(response, fallback) {
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}));
    throw new Error(detail?.error || '无法生成 PDF（HTTP ' + response.status + '）。');
  }
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
      return '已导出 ' + items.length + ' 项曲线。';
    }
    let directory = null;
    let folderFailed = false;
    if (supportsProjectFolders) {
      try { directory = curveDirectory ? await checkDirectory(curveDirectory) : await selectCurveFolder(); }
      catch (error) {
        if (error.name === 'AbortError') throw error;
        folderFailed = true;
      }
    }
    const downloadMessage = amount => (folderFailed ? '无法写入项目目录，' : '') +
      '已将 ' + amount + ' 项曲线发送到浏览器下载。';
    if (!directory && items.length > 1) {
      downloadBlob(await downloadExport(await exportRequest('/api/pumps/export-batch', items), 'Curves_V0.zip'));
      return downloadMessage(items.length);
    }
    let saved = 0;
    let downloaded = 0;
    const failed = [];
    for (const item of items) {
      try {
        const file = await downloadExport(await exportRequest('/api/pumps/export-pdf', item), 'Curve.pdf');
        if (directory) {
          try {
            const name = await savePdf(directory, file.name.replace(/\.pdf$/i, ''), file.blob,
              count > 1 ? 'Curve' : undefined);
            folderStatus.textContent = '已保存：' + directory.name + '/' + name;
            saved++;
            continue;
          } catch {
            folderFailed = true;
            directory = null;
          }
        }
        downloadBlob(file);
        downloaded++;
      } catch (error) {
        failed.push((item.details?.tag || '设备 ' + (saved + downloaded + failed.length + 1)) + '：' + error.message);
      }
    }
    const status = (saved ? '已导出 ' + saved + ' 项曲线。' : '') + (downloaded ? downloadMessage(downloaded) : '');
    if (failed.length) throw new Error(status + failed.join('；'));
    return status;
  } finally {
    exporting = false;
    $('#export-curve').disabled = ratingLoading || !printData;
    $('#selection-folder').disabled = false;
  }
}

function showExportStatus(message = '', isError = false) {
  exportStatus.textContent = message;
  exportStatus.hidden = !message;
  exportStatus.classList.toggle('is-error', isError);
}

$('#export-curve').onclick = async () => {
  if (!printData || ratingLoading || exporting) return;
  if (!projectForm.checkValidity()) {
    showExportStatus('请先核对项目基本信息。', true);
    $('#submission-details').open = true;
    projectForm.reportValidity();
    return;
  }
  const { pump, duty } = printData;
  showExportStatus('正在导出 PDF…');
  try {
    showExportStatus(await exportCurves([{ duty, index: pump.index, details: projectDetails(),
      ratingOverride: pump.ratingOverride }], itemCount()));
  } catch (error) {
    showExportStatus(error.name === 'AbortError' ? '已取消导出，未保存 PDF。' : error.message, error.name !== 'AbortError');
  }
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
      details: { ...projectDetails(), proposalNotes: batch.details()?.proposalNotes },
      folderKey: draftKey, plannedItems: itemCount() }));
  } else sessionStorage.removeItem('cfl.selectedDuty');
  location.hash = 'submissions';
};

function withProjectDetails(item) {
  const shared = projectDetails();
  return { ...item.details, project: shared.project ?? item.details.project, client: shared.client ?? item.details.client,
    customerOrder: shared.customerOrder ?? item.details.customerOrder, company: shared.company ?? item.details.company };
}

const batch = mountBatch({
  readItem() {
    if (!form.reportValidity()) return null;
    if (!projectForm.checkValidity()) { $('#submission-details').open = true; projectForm.reportValidity(); return null; }
    return { duty: readDuty(), details: projectDetails() };
  },
  readSelection: () => !ratingLoading && selected && dutyKey(readDuty()) === dutyKey(previousDuty) ?
    { pump: selected, duty: previousDuty } : null,
  openItem(item) {
    const optional = ['tag', 'service', 'quantity', 'liquid', 'material', 'drawingModel'];
    for (const name of optional) projectForm.elements[name].value = item.details[name] ?? '';
    if (!projectForm.elements.client.value && item.details.client) projectForm.elements.client.value = item.details.client;
    projectForm.oninput();
    for (const input of fields) if (input.name) input.value = item.duty[input.name] ?? '';
    syncXSubtype();
    invalidateSelection();
    if (!batch.needsReview()) form.requestSubmit();
  },
  async importFile(file) {
    if (!/\.xlsx$/i.test(file.name) || !file.size || file.size > 360 * 1024)
      throw new Error('请选择不超过 360 KB 的选型一览表 .xlsx 文件。');
    const content = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result.split(',')[1]);
      reader.onerror = () => reject(new Error('无法读取所选 Excel 文件。'));
      reader.readAsDataURL(file);
    });
    return api('/api/pumps/import-datasheet', { content });
  },
  search: duty => api('/api/pumps/search', duty),
  isConnectionError: error => error instanceof PumpConnectionError,
  exportItems: items => exportCurves(items.map(item => ({ duty: item.duty, index: item.pump.index,
    details: withProjectDetails(item), ratingOverride: item.pump.ratingOverride })), itemCount()),
  transfer(items) {
    const selections = items.map(item => ({ duty: item.duty, index: item.pump.index,
      pump: item.pump, details: withProjectDetails(item), ratingOverride: item.pump.ratingOverride }));
    sessionStorage.removeItem('cfl.selectedDuty');
    sessionStorage.setItem('cfl.selectedBatch', JSON.stringify({ items: selections, folderKey: draftKey,
      plannedItems: itemCount() }));
    location.hash = 'submissions';
  }
});
form.addEventListener('input', syncRatingControls);

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
      const source = curve.source;
      const notes = [...(curve.availableForSelection ? [] : [source ? '仅图册记录，未开放选型' : '未开放选型']),
        ...(curve.clearance ? ['间隙 ' + curve.clearance] : []), ...curve.issues];
      const rpm = source?.minRpm && source?.maxRpm ? number(source.minRpm, 0) + '–' + number(source.maxRpm, 0) : number(curve.rpm, 0);
      row.append(...[curve.series, curve.size, curve.curveNumber, curve.originalCurveNumber, rpm,
        number(curve.minDiameterMm) + ' / ' + number(curve.maxDiameterMm),
        curve.points || '—'].map(value => text('td', value)));
      const provenance = text('td', notes.join(' · ') || (source ? 'AHLSTAR' : '—'));
      if (source) {
        const details = document.createElement('details');
        details.append(text('summary', 'AHLSTAR PDF · 第 ' + source.page + ' 页'));
        if (source.nominalSize) details.append(text('p', '原图规格：' + source.nominalSize));
        if (source.impeller) details.append(text('p', '叶轮：' + source.impeller));
        if (source.trimDiametersMm.length)
          details.append(text('p', '直径表（mm）：' + source.trimDiametersMm.map(d => number(d)).join('、')));
        if (curve.tracedDiametersMm?.length)
          details.append(text('p', '图示直径（mm）：' + curve.tracedDiametersMm.map(d => number(d)).join('、')));
        provenance.append(details);
      } else if (curve.sourceFile) {
        provenance.append(text('p', curve.sourceFile + (curve.sourcePage ? ' · 第 ' + curve.sourcePage + ' 页' : '')));
      }
      row.append(provenance);
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
showView();
if (!window.cflOfflineSave && 'serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(console.warn);
if ($('#exited').hidden) loadPumpOptions();
