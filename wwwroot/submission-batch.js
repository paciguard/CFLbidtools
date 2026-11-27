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
      label.append(select);
      const include = document.createElement('input');
      include.type = 'checkbox';
      include.checked = choices[index].length > 0;
      include.disabled = !choices[index].length;
      include.onchange = sync;
      const includeLabel = document.createElement('label');
      includeLabel.className = 'batch-include';
      includeLabel.append(include, '加入项目');
      const block = document.createElement('div');
      block.append(label, includeLabel);
      models.push({ select, include, supported: choices[index].length > 0 });
      form.append(block);
    }
    status.textContent = unmatched.length ? '这些设备无法在 ZTools 中生成图纸：' + unmatched.join('、') : '';
    sync();
  } catch (error) { if (current === version) status.textContent = error.message; }
  return true;
}

function sync() {
  const included = models.filter(model => model.include.checked);
  $('#create-batch').disabled = busy || !included.length || included.some(model => !model.select.value);
  for (const model of models) {
    model.select.disabled = busy || !model.include.checked;
    model.include.disabled = busy || !model.supported;
  }
}

window.addEventListener('cfl-control-state', event => { busy = event.detail; sync(); });
$('#create-batch').onclick = () => {
  if ($('#create-batch').disabled) return;
  const selected = batch.items.flatMap((item, index) => models[index].include.checked
    ? [{ ...item, model: models[index].select.value }] : []);
  if (selected.length !== batch.items.length && !window.confirm('仅为已勾选的设备创建项目。继续？')) return;
  window.dispatchEvent(new CustomEvent('cfl-new-from-batch', {
    detail: { ...batch, items: selected }
  }));
};

let edits = [];
let reviews = [];
$('#open-batch-settings').onclick = async () => {
  $('#open-batch-settings').disabled = true;
  try {
    const rows = await request('batch-parameters', {});
    controls.replaceChildren();
    edits = [];
    reviews = [];
    for (const row of rows) {
      const panel = document.createElement('fieldset');
      const legend = document.createElement('legend');
      legend.textContent = [row.tag, row.model].filter(Boolean).join(' · ') || '设备 ' + (row.row + 1);
      panel.append(legend);
      let confirmation;
      if (row.review) {
        if (row.proposalNotes?.length) {
          const source = document.createElement('details');
          const summary = document.createElement('summary');
          summary.textContent = '原表参数';
          const notes = document.createElement('ul');
          for (const value of row.proposalNotes.filter(Boolean)) {
            const note = document.createElement('li');
            note.textContent = value;
            notes.append(note);
          }
          source.append(summary, notes);
          panel.append(source);
        }
        confirmation = document.createElement('input');
        confirmation.type = 'checkbox';
        confirmation.checked = row.review === 'confirmed';
        reviews.push({ row: row.row, original: confirmation.checked, input: confirmation });
      }
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
          if (confirmation) input.addEventListener('input', () => confirmation.checked = false);
          label.append(input);
          block.append(label);
          edits.push({ row: row.row, group, field, input });
        }
        if (block.childElementCount > 1) panel.append(block);
      }
      if (confirmation) {
        const label = document.createElement('label');
        label.className = 'batch-include';
        label.append(confirmation, '已核对此设备的图纸参数');
        panel.append(label);
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
  const edited = new Set([...grouped.values()].map(item => item.row));
  for (const { row, original, input } of reviews) {
    if (input.checked === original && !edited.has(row)) continue;
    grouped.set('review' + row, { group: 'review', row, changes: [
      { sheet: 'CFL-App', address: 'I' + (21 + row), value: input.checked ? 'confirmed' : 'pending' }
    ] });
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
