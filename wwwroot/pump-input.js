const form = document.querySelector('#pump-input');
const fieldList = document.querySelector('#pump-fields');
const save = form.querySelector('button[type=submit]');
const device = document.querySelector('#device');
const status = document.querySelector('#pump-input-status');
let version = 0;
let busy = false;
let fields = [];
let focusAddress;

function focusInput() {
  const target = fields.find(({ field }) => field.sheet + '!' + field.address === focusAddress);
  if (!target) return;
  focusAddress = undefined;
  target.input.focus();
}

function syncControls() {
  save.disabled = busy || !fields.length;
  for (const input of form.querySelectorAll('input')) input.disabled = busy;
}

async function loadFields() {
  const current = ++version;
  fields = [];
  form.hidden = device.value === '';
  fieldList.replaceChildren();
  status.textContent = '';
  syncControls();
  if (form.hidden) return;
  try {
    const parameters = await window.cflSubmissionRequest('parameters', { group: 'hydraulics', row: Number(device.value) });
    if (current !== version) return;
    fields = parameters.fields.map(field => {
      const column = field.address[0];
      const label = document.createElement('label');
      label.textContent = field.label;
      const input = document.createElement('input');
      input.dataset.address = field.address;
      input.readOnly = field.readOnly;
      if (column !== 'N') {
        input.type = 'number';
        input.step = column === 'R' ? '1' : 'any';
        input.min = '0';
        if (column === 'Q') input.max = '100';
      }
      input.value = column === 'Q' && field.value !== '' ? Number((Number(field.value) * 100).toFixed(8)) : field.value;
      label.append(input);
      fieldList.append(label);
      return { field, input, original: input.value };
    });
    syncControls();
    focusInput();
  } catch (error) {
    if (current === version) status.textContent = error.message;
  }
}

form.onsubmit = async event => {
  event.preventDefault();
  if (busy || device.value === '') return;
  const changes = fields.filter(({ input, original }) => input.value !== original)
    .map(({ field, input }) => ({
      sheet: field.sheet, address: field.address,
      value: field.address.startsWith('Q') && input.value !== '' ? String(Number(input.value) / 100) : input.value
    }));
  if (!changes.length) return;
  const current = version;
  busy = true;
  syncControls();
  try {
    const project = await window.cflSubmissionRequest('save', { group: 'hydraulics', row: Number(device.value), changes });
    if (current === version) {
      window.dispatchEvent(new CustomEvent('cfl-project-updated', { detail: project }));
      status.textContent = '运行参数已保存。';
    }
  } catch (error) {
    if (current === version) status.textContent = error.message;
  } finally {
    busy = false;
    syncControls();
  }
};

device.addEventListener('change', loadFields);
window.addEventListener('cfl-data-rendered', loadFields);
window.addEventListener('cfl-focus-hydraulics', event => {
  focusAddress = event.detail;
  form.scrollIntoView({ block: 'nearest' });
  focusInput();
});
window.addEventListener('cfl-control-state', event => {
  busy = event.detail;
  syncControls();
});
loadFields();
