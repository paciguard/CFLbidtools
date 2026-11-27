const key = 'cfl.pumpBatch';
const $ = selector => document.querySelector(selector);
const dutyKey = duty => JSON.stringify(Object.entries(duty ?? {})
  .filter(([, value]) => value != null && value !== '').sort(([a], [b]) => a.localeCompare(b)));
const samePump = (pump, previous) => previous && ['family', 'size', 'curve', 'stages', 'rpm', 'diameter', 'group']
  .every(field => pump[field] === previous[field]);
const sameRating = (pump, previous) => ['efficiency', 'npshr']
  .every(field => (pump.ratingOverride?.[field] ?? null) === (previous.ratingOverride?.[field] ?? null));
const pumpName = pump => (pump.modelSeries ?? pump.series ?? pump.family) + ' ' + pump.size;

export function mountBatch({ readItem, readSelection, openItem, search, isConnectionError, importFile, exportItems, transfer }) {
  let items = [];
  try { items = JSON.parse(sessionStorage.getItem(key) || '[]'); } catch {}
  let active;
  let busy = false;
  const list = $('#batch-items');
  const status = $('#batch-status');
  const picker = $('#batch-file');
  const confirm = $('#confirm-pump');
  const activeItem = () => items.find(item => item.id === active);

  function refreshConfirmation() {
    const item = activeItem();
    const preview = item && !busy ? readSelection() : null;
    const saved = preview && item.confirmed && samePump(preview.pump, item.pump) && sameRating(preview.pump, item.pump) &&
      dutyKey(preview.duty) === dutyKey(item.duty);
    confirm.disabled = !preview || !!saved;
    confirm.classList.toggle('is-confirmed', !!saved);
    confirm.lastElementChild.textContent = saved ? '已确认选型' : '确认选型';
  }

  function updateItem(item, entry) {
    if (item.details.proposalNotes) entry.details.proposalNotes = item.details.proposalNotes;
    if (item.importIssues?.length) item.reviewedImportIssues = item.importIssues;
    Object.assign(item, entry, { error: null, importIssues: [] });
  }

  function refresh() {
    sessionStorage.setItem(key, JSON.stringify(items));
    const ready = items.length > 0 && items.every(item => item.pump);
    $('#batch-search').disabled = busy || !items.length;
    $('#batch-export').disabled = busy || !ready;
    $('#batch-transfer').disabled = busy || !items.some(item => item.pump);
    $('#batch-add').disabled = busy || (!active && items.length >= 30);
    $('#batch-import').disabled = busy || items.length >= 30;
    $('#batch-add').textContent = active ? '更新设备项' : '添加设备项';
    $('#batch-new').hidden = !active;
    refreshConfirmation();
    const editing = activeItem();
    const source = editing?.sourceRow != null ? editing : null;
    $('#batch-source').hidden = !source;
    $('#batch-source-values').replaceChildren(...(source ? [
      'Excel 第 ' + source.sourceRow + ' 行', ...(source.importIssues ?? []),
      ...(source.reviewedImportIssues ?? []).map(issue => '原表复核项：' + issue), ...(source.details.proposalNotes ?? [])
    ].map(value => {
      const note = document.createElement('li');
      note.textContent = value;
      return note;
    }) : []));
    list.replaceChildren(...items.map((item, index) => {
      const row = document.createElement('tr');
      if (item.id === active) row.setAttribute('aria-selected', 'true');
      const label = [item.details.tag, item.details.service].filter(Boolean).join(' · ') ||
        (item.sourceRow ? 'Excel 第 ' + item.sourceRow + ' 行' : '设备 ' + (index + 1));
      const open = document.createElement('button');
      open.textContent = label;
      open.type = 'button'; open.disabled = busy;
      open.onclick = () => { active = item.id; refresh(); openItem(item); };
      row.insertCell().append(open);
      for (const value of [item.duty.flow, item.duty.head, item.pump ?
        pumpName(item.pump) + ' / ' + item.pump.rpm + ' rpm' :
        item.importIssues?.join(' ') || item.error || '待选型'])
        row.insertCell().textContent = value ?? '—';
      const remove = document.createElement('button');
      remove.type = 'button'; remove.textContent = '移除'; remove.disabled = busy;
      remove.onclick = () => { if (active === item.id) active = undefined; items = items.filter(i => i !== item); refresh(); };
      row.insertCell().append(remove);
      return row;
    }));
  }

  async function work(action) {
    if (busy) return;
    busy = true; refresh(); status.textContent = '';
    try { await action(); }
    catch (error) { if (error.name !== 'AbortError') status.textContent = error.message; }
    finally { busy = false; refresh(); }
  }

  $('#batch-add').onclick = () => {
    const entry = readItem();
    if (!entry) return;
    const existing = activeItem();
    if (existing) {
      if (dutyKey(existing.duty) !== dutyKey(entry.duty)) {
        existing.pump = null;
        existing.confirmed = false;
      }
      updateItem(existing, entry);
    }
    else if (items.length < 30) items.push({ ...entry, id: Math.random().toString(36).slice(2) });
    active = undefined;
    refresh();
  };
  confirm.onclick = () => {
    const item = activeItem();
    const preview = readSelection();
    if (busy || !item || !preview) return;
    const entry = readItem();
    if (!entry || dutyKey(preview.duty) !== dutyKey(entry.duty)) return;
    updateItem(item, { ...entry, pump: preview.pump, confirmed: true });
    refresh();
    status.textContent = '已确认：' + [item.details.tag, pumpName(item.pump)].filter(Boolean).join(' · ');
  };
  $('#batch-import').onclick = () => picker.click();
  picker.onchange = () => {
    const file = picker.files[0];
    picker.value = '';
    if (!file) return;
    work(async () => {
      status.textContent = '正在读取 Excel…';
      const imported = await importFile(file);
      if (items.length + imported.length > 30) throw new Error('批量最多 30 个设备项，请先移除部分设备。');
      items.push(...imported.map(row => ({
        id: Math.random().toString(36).slice(2), sourceRow: row.sourceRow, details: row.details,
        duty: Object.fromEntries(Object.entries(row.duty).filter(([, value]) => value != null)),
        importIssues: row.issues, pump: null
      })));
      const missing = imported.filter(row => row.issues.length).length;
      status.textContent = '已导入 ' + imported.length + ' 项' + (missing ? '；' + missing + ' 项需补充或核对。' : '。');
    });
  };
  $('#batch-new').onclick = () => { active = undefined; refresh(); };
  $('#batch-search').onclick = () => work(async () => {
    for (const [index, item] of items.entries()) {
      status.textContent = '批量选型：' + (index + 1) + ' / ' + items.length;
      if (item.importIssues?.length) continue;
      try {
        const results = await search(item.duty);
        const previous = item.pump;
        const matched = results.find(pump => samePump(pump, previous));
        item.pump = matched && previous.ratingOverride ? { ...previous, index: matched.index } :
          matched ?? (item.confirmed ? null : results[0] ?? null);
        const restrictions = [item.duty.stages != null && item.duty.stages + ' 级', item.duty.clearance].filter(Boolean).join(' / ');
        item.error = item.pump ? null : item.confirmed ? '已确认泵型未匹配，请重新确认。' :
          restrictions ? '无匹配泵型（' + restrictions + '）' : '无匹配泵型';
      } catch (error) {
        if (isConnectionError(error)) throw error;
        item.pump = null;
        item.error = error.message;
      }
    }
    status.textContent = '已完成 ' + items.filter(item => item.pump).length + ' / ' + items.length + ' 项选型。';
  });
  $('#batch-export').onclick = () => work(async () => {
    status.textContent = await exportItems(items);
  });
  $('#batch-transfer').onclick = () => {
    const selected = items.filter(item => item.pump);
    if (!selected.length || (selected.length !== items.length &&
        !window.confirm('仅将已完成选型的设备提交到 ZTools。继续？'))) return;
    transfer(selected);
  };
  refresh();
  return {
    count: () => items.length,
    preferred(duty) {
      const item = activeItem();
      return item && dutyKey(item.duty) === dutyKey(duty) ? item.pump : null;
    },
    details: () => activeItem()?.details,
    needsReview: () => !!activeItem()?.importIssues?.length,
    refreshConfirmation
  };
}

export { samePump, dutyKey };
