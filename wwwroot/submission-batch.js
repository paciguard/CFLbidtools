import { updateProjectFolder } from './project-folder.js';

const $ = selector => document.querySelector(selector);
const section = $('#batch-handoff');
const form = $('#batch-models');
const status = $('#batch-handoff-status');
const dialog = $('#batch-settings');
const controls = $('#batch-settings-fields');
const settingStatus = $('#batch-settings-status');
let batch;
let version = 0;
let busy = false;
let models = [];
let request;

export async function loadBatchHandoff(api) {
  request = api;
  const current = ++version;
  try { batch = JSON.parse(sessionStorage.getItem('cfl.selectedBatch')); } catch { batch = null; }
  section.hidden = !batch?.items?.length;
  form.replaceChildren();
  models = [];
  $('#create-batch').disabled = true;
  if (section.hidden) return false;
  status.textContent = '正在匹配批量泵型…';
  try {
    const choices = await api('batch-models', batch);
    if (current !== version) return true;
    const unmatched = [];
    for (const [index, item] of batch.items.entries()) {
      const label = document.createElement('label');
      label.textContent = (item.details.tag || '设备 ' + (index + 1)) + ' · ' + (item.pump.modelSeries ?? item.pump.series ?? item.pump.family) + ' ' + item.pump.size;
      const select = document.createElement('select');
      if (choices[index].length !== 1) select.add(new Option(choices[index].length ? '请选择型号' : '无匹配 ZTools 型号', ''));
      for (const model of choices[index]) select.add(new Option(model));
      if (!choices[index].length) { select.disabled = true; unmatched.push(item.details.tag || '设备 ' + (index + 1)); }
      select.onchange = sync;
      models.push(select);
      label.append(select);
      form.append(label);
    }
    status.textContent = unmatched.length ? '这些设备无法在 ZTools 中生成图纸：' + unmatched.join('、') : '';
    sync();
  } catch (error) { if (current === version) status.textContent = error.message; }
  return true;
}

function sync() {
  $('#create-batch').disabled = busy || !models.length || models.some(select => !select.value);
}

window.addEventListener('cfl-control-state', event => { busy = event.detail; sync(); });
$('#create-batch').onclick = () => {
  if (busy || models.some(select => !select.value)) return;
  window.dispatchEvent(new CustomEvent('cfl-new-from-batch', {
    detail: { ...batch, items: batch.items.map((item, index) => ({ ...item, model: models[index].value })) }
  }));
};

let edits = [];
$('#open-batch-settings').onclick = async () => {
  $('#open-batch-settings').disabled = true;
  try {
    const rows = await request('batch-parameters', {});
    controls.replaceChildren();
    edits = [];
    for (const row of rows) {
      const panel = document.createElement('fieldset');
      const legend = document.createElement('legend');
      legend.textContent = [row.tag, row.model].filter(Boolean).join(' · ') || '设备 ' + (row.row + 1);
      panel.append(legend);
      const seen = new Set();
      for (const [group, { title, fields }] of Object.entries(row.forms)) {
        const block = document.createElement('div');
        block.className = 'batch-parameter-group';
        block.dataset.group = group;
        const heading = document.createElement('h3');
        heading.textContent = title;
        block.append(heading);
        for (const field of fields) {
          const key = field.sheet + '!' + field.address;
          if (field.readOnly || seen.has(key)) continue;
          seen.add(key);
          const label = document.createElement('label');
          label.textContent = field.label;
          const input = document.createElement(field.strictChoices ? 'select' : 'input');
          input.setAttribute('aria-label', field.label);
          if (field.strictChoices) {
            input.add(new Option('请选择', ''));
            for (const choice of new Set([...field.choices, field.value].filter(Boolean))) input.add(new Option(choice));
          } else if (field.choices.length) {
            const options = document.createElement('datalist');
            options.id = 'batch-options-' + edits.length;
            for (const choice of field.choices) options.append(new Option(choice));
            input.setAttribute('list', options.id);
            label.append(options);
          }
          input.value = field.value;
          label.append(input);
          block.append(label);
          edits.push({ row: row.row, group, field, input });
        }
        if (block.childElementCount > 1) panel.append(block);
      }
      controls.append(panel);
    }
    settingStatus.textContent = '';
    dialog.showModal();
  } catch (error) {
    $('#batch-settings-note').textContent = error.message;
  } finally { $('#open-batch-settings').disabled = false; }
};
$('#close-batch-settings').onclick = () => dialog.close();
$('#batch-settings-form').onsubmit = async event => {
  event.preventDefault();
  const submit = $('#save-batch-settings');
  if (submit.disabled) return;
  const grouped = new Map();
  for (const { row, group, field, input } of edits) {
    if (input.value === field.value) continue;
    const key = group + row;
    if (!grouped.has(key)) grouped.set(key, { group, row, changes: [] });
    grouped.get(key).changes.push({ sheet: field.sheet, address: field.address, value: input.value });
  }
  if (!grouped.size) { dialog.close(); return; }
  submit.disabled = true;
  try {
    const state = await request('save-batch', [...grouped.values()]);
    await updateProjectFolder(state);
    window.dispatchEvent(new CustomEvent('cfl-project-updated', { detail: state }));
    dialog.close();
  } catch (error) { settingStatus.textContent = error.message; }
  finally { submit.disabled = false; }
};
