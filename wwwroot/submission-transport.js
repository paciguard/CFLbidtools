const upload = document.getElementById('workbook-upload');
const actions = document.querySelector('#duty-transfer');
const transfer = document.querySelector('#apply-duty');
const device = document.querySelector('#device');
let importedSelection;
let transferring = false;
let documentBusy = false;

async function request(command, data) {
  const headers = { 'X-CFL-Request': '1' };
  let route = '/api/submissions/command/' + encodeURIComponent(command);
  let body = JSON.stringify(data);
  if (command === 'import') {
    upload.value = '';
    const workbook = await new Promise(resolve => {
      upload.onchange = () => resolve(upload.files[0]);
      upload.oncancel = () => resolve(null);
      upload.click();
    });
    if (!workbook) return request('state', {});
    route = '/api/submissions/import';
    body = new FormData();
    body.append('workbook', workbook);
  } else headers['Content-Type'] = 'application/json';
  const response = await fetch(route, { method: 'POST', headers, body });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.error || '请求未完成，请检查连接。');
  return result;
}

window.CflNative = {
  async postMessage(message) {
    const { id, command, data } = JSON.parse(message);
    try {
      if (command === 'export') {
        const link = document.createElement('a');
        link.href = '/api/submissions/file/' + encodeURIComponent(data.format)
          + (Number.isInteger(data.id) ? '?id=' + data.id : '');
        link.download = '';
        document.body.append(link);
        link.click();
        link.remove();
        window.cflReply({ id, data: {} });
      } else window.cflReply({ id, data: await request(command, data) });
    } catch (error) { window.cflReply({ id, error: error.message }); }
  }
};

function loadSelection() {
  try { importedSelection = JSON.parse(sessionStorage.getItem('cfl.selectedDuty')); }
  catch { importedSelection = null; }
  actions.hidden = !importedSelection;
  if (!importedSelection) return;
  const { pump, duty } = importedSelection;
  document.querySelector('#transfer-status').textContent = '';
  document.querySelector('#duty-details').textContent = [
    pump.family + ' ' + pump.size, duty.flow + ' m³/h', duty.head + ' m',
    pump.npshr.toFixed(2) + ' m NPSHr', pump.efficiency.toFixed(2) + ' %', pump.rpm + ' rpm'
  ].filter(Boolean).join(' · ');
  updateTransferButton();
}
function updateTransferButton() {
  const supported = ['Z', 'CZ'].includes(importedSelection?.pump?.family);
  transfer.disabled = transferring || documentBusy || !supported || device.value === '';
  document.querySelector('#transfer-policy').textContent = supported
    ? '将流量、要求扬程、NPSHr、效率和泵转速写入匹配的设备。电机与底座等参数在本页面设置。'
    : '此系列暂未录入 ZTools 数据库，选型结果仅供参考。';
}
device.addEventListener('change', updateTransferButton);
window.addEventListener('cfl-project-updated', () => queueMicrotask(updateTransferButton));
window.addEventListener('cfl-control-state', event => { documentBusy = event.detail; updateTransferButton(); });
window.addEventListener('message', event => {
  if (event.origin === location.origin && event.source === window.parent && event.data === 'cfl-update-duty') loadSelection();
});
transfer.onclick = async () => {
  if (!importedSelection || device.value === '') return;
  const { pump, duty } = importedSelection;
  const row = Number(device.value);
  transferring = true;
  updateTransferButton();
  try {
    const state = await request('apply-pump', { row, duty, index: pump.index });
    window.dispatchEvent(new CustomEvent('cfl-project-updated', { detail: state }));
    document.querySelector('#transfer-status').textContent = '已写入当前设备。';
  } catch (error) { document.querySelector('#transfer-status').textContent = error.message; }
  finally { transferring = false; updateTransferButton(); }
};
window.addEventListener('DOMContentLoaded', () => window.lucide?.createIcons());
loadSelection();
