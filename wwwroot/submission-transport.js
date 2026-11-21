import { updateProjectFolder, requiredProjectFolder, projectOutput, saveProjectPdf, saveGeneratedPdfs } from './project-folder.js';
import { loadBatchHandoff } from './submission-batch.js';

const actions = document.querySelector('#duty-transfer');
const transfer = document.querySelector('#apply-duty');
const device = document.querySelector('#device');
const createProject = document.querySelector('#new-from-pump');
const modelField = document.querySelector('#transfer-model-field');
const modelSelect = document.querySelector('#transfer-model');
let importedSelection;
let transferring = false;
let documentBusy = false;
let modelRequest = 0;
let models = [];

async function request(command, data) {
  const headers = { 'X-CFL-Request': '1', 'Content-Type': 'application/json' };
  const route = '/api/submissions/command/' + encodeURIComponent(command);
  const response = await fetch(route, { method: 'POST', headers, body: JSON.stringify(data) });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(result.error || '请求未完成，请检查连接。'), { issues: result.issues });
  return result;
}
window.cflSubmissionRequest = request;

window.CflNative = {
  async postMessage(message) {
    const { id, command, data } = JSON.parse(message);
    try {
      if (command === 'export') {
        const href = '/api/submissions/file/' + encodeURIComponent(data.format)
          + (Number.isInteger(data.id) ? '?id=' + data.id : '');
        if (window.cflOfflineSave) {
          try { await window.cflOfflineSave(href); }
          catch (error) { if (error.name !== 'AbortError') throw error; }
          window.cflReply({ id, data: {} });
          return;
        }
        if (data.format === 'pdf') {
          const directory = await requiredProjectFolder();
          if (directory) {
            const name = await saveProjectPdf(directory, projectOutput(data.id)).catch(error => {
              throw new Error('未能保存 PDF 到工作目录。请检查目录权限和可用空间。' +
                (error.name === 'Error' ? ' ' + error.message : ''));
            });
            document.querySelector('#project-folder-status').textContent = '已保存到 ' + directory.name + '：' + name;
            window.cflReply({ id, data: {} });
            return;
          }
        }
        const link = document.createElement('a');
        link.href = href;
        link.download = '';
        document.body.append(link);
        link.click();
        link.remove();
        window.cflReply({ id, data: {} });
      } else {
        const directory = command === 'generate' ? await requiredProjectFolder() : null;
        const result = await request(command, data);
        if (result?.rows) await updateProjectFolder(result,
          ['new-from-pump', 'new-from-batch'].includes(command) ? data : undefined);
        if (directory) result.folderExport = await saveGeneratedPdfs(directory, result.files);
        window.cflReply({ id, data: result });
      }
    } catch (error) { window.cflReply({ id, error: error.message, issues: error.issues }); }
  }
};

async function loadSelection() {
  const version = ++modelRequest;
  if (sessionStorage.getItem('cfl.selectedBatch')) {
    importedSelection = null;
    actions.hidden = true;
    await loadBatchHandoff(request);
    return;
  }
  loadBatchHandoff(request);
  try { importedSelection = JSON.parse(sessionStorage.getItem('cfl.selectedDuty')); }
  catch { importedSelection = null; }
  models = [];
  modelSelect.replaceChildren();
  modelField.hidden = true;
  actions.hidden = !importedSelection;
  if (!importedSelection) return;
  const { pump, duty } = importedSelection;
  document.querySelector('#transfer-status').textContent = '';
  document.querySelector('#duty-details').textContent = [
    (pump.modelSeries ?? pump.series ?? pump.family) + ' ' + pump.size, duty.flow + ' m³/h', duty.head + ' m',
    pump.npshr.toFixed(2) + ' m NPSHr', pump.efficiency.toFixed(2) + ' %', pump.rpm + ' rpm',
    duty.viscosityCp != null && duty.viscosityCp + ' cP'
  ].filter(Boolean).join(' · ');
  updateTransferButton();
  if (!['Z', 'CZ'].includes(pump.family)) return;
  const status = document.querySelector('#transfer-status');
  status.textContent = '正在匹配 ZTools 型号…';
  try {
    const matches = await request('pump-models', { duty, index: pump.index });
    if (version !== modelRequest) return;
    models = matches;
    const options = models.map(model => new Option(model, model));
    if (models.length > 1) options.unshift(new Option('请选择型号', ''));
    modelSelect.replaceChildren(...options);
    modelField.hidden = models.length === 0;
    status.textContent = models.length ? '' : 'ZTools 数据库中没有匹配的型号。';
  } catch (error) {
    if (version === modelRequest) status.textContent = error.message;
  } finally {
    if (version === modelRequest) updateTransferButton();
  }
}
function updateTransferButton() {
  const supported = ['Z', 'CZ'].includes(importedSelection?.pump?.family);
  transfer.disabled = transferring || documentBusy || !supported || device.value === '';
  modelSelect.disabled = transferring || documentBusy;
  createProject.disabled = transferring || documentBusy || !models.includes(modelSelect.value);
  createProject.hidden = !supported;
  transfer.hidden = !supported;
  document.querySelector('#transfer-policy').textContent = supported
    ? '新建项目带入泵选型结果与已填写的项目参数。'
    : '此系列暂未录入 ZTools 数据库，选型结果仅供参考。';
}
modelSelect.addEventListener('change', updateTransferButton);
device.addEventListener('change', updateTransferButton);
window.addEventListener('cfl-project-updated', () => queueMicrotask(updateTransferButton));
window.addEventListener('cfl-control-state', event => { documentBusy = event.detail; updateTransferButton(); });
window.addEventListener('message', event => {
  if (event.origin === location.origin && event.source === window.parent && event.data === 'cfl-update-duty') loadSelection();
});
createProject.onclick = () => {
  if (!importedSelection || !models.includes(modelSelect.value)) return;
  const { pump, duty, details, folderKey, plannedItems } = importedSelection;
  window.dispatchEvent(new CustomEvent('cfl-new-from-pump', {
    detail: { duty, index: pump.index, model: modelSelect.value, details, folderKey, plannedItems }
  }));
};
transfer.onclick = async () => {
  if (!importedSelection || device.value === '') return;
  const { pump, duty } = importedSelection;
  const row = Number(device.value);
  transferring = true;
  updateTransferButton();
  try {
    const state = await request('apply-pump', { row, duty, index: pump.index });
    await updateProjectFolder(state);
    window.dispatchEvent(new CustomEvent('cfl-project-updated', { detail: state }));
    document.querySelector('#transfer-status').textContent = '已写入当前设备。';
  } catch (error) { document.querySelector('#transfer-status').textContent = error.message; }
  finally { transferring = false; updateTransferButton(); }
};
window.addEventListener('DOMContentLoaded', () => window.lucide?.createIcons());
loadSelection();
