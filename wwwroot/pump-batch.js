const key = 'cfl.pumpBatch';
const $ = selector => document.querySelector(selector);

export function mountBatch({ readItem, openItem, search, exportItems, transfer }) {
  let items = [];
  try { items = JSON.parse(sessionStorage.getItem(key) || '[]'); } catch {}
  let active;
  let busy = false;
  const list = $('#batch-items');
  const status = $('#batch-status');

  function refresh() {
    sessionStorage.setItem(key, JSON.stringify(items));
    const ready = items.length > 0 && items.every(item => item.pump);
    $('#batch-search').disabled = busy || !items.length;
    $('#batch-export').disabled = busy || !ready;
    $('#batch-transfer').disabled = busy || !ready;
    $('#batch-add').disabled = busy || (!active && items.length >= 30);
    $('#batch-add').textContent = active ? '更新设备项' : '添加设备项';
    $('#batch-new').hidden = !active;
    list.replaceChildren(...items.map((item, index) => {
      const row = document.createElement('tr');
      if (item.id === active) row.setAttribute('aria-selected', 'true');
      const label = [item.details.tag, item.details.service].filter(Boolean).join(' · ') || '设备 ' + (index + 1);
      const open = document.createElement('button');
      open.textContent = label;
      open.type = 'button'; open.disabled = busy;
      open.onclick = () => { active = item.id; refresh(); openItem(item); };
      row.insertCell().append(open);
      for (const value of [item.duty.flow, item.duty.head, item.pump ?
        (item.pump.modelSeries ?? item.pump.series ?? item.pump.family) + ' ' + item.pump.size + ' / ' + item.pump.rpm + ' rpm' : item.error || '待选型'])
        row.insertCell().textContent = value;
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
    const existing = items.find(item => item.id === active);
    if (existing) Object.assign(existing, entry, { error: null });
    else if (items.length < 30) items.push({ ...entry, id: Math.random().toString(36).slice(2) });
    active = undefined;
    refresh();
  };
  $('#batch-new').onclick = () => { active = undefined; refresh(); };
  $('#batch-search').onclick = () => work(async () => {
    for (const [index, item] of items.entries()) {
      status.textContent = '批量选型：' + (index + 1) + ' / ' + items.length;
      try {
        const results = await search(item.duty);
        item.pump = results.find(pump => pump.index === item.pump?.index) ?? results[0] ?? null;
        item.error = item.pump ? null : '无匹配泵型';
      } catch (error) { item.pump = null; item.error = error.message; }
    }
    status.textContent = '已完成 ' + items.filter(item => item.pump).length + ' / ' + items.length + ' 项选型。';
  });
  $('#batch-export').onclick = () => work(async () => {
    await exportItems(items);
    status.textContent = '已导出 ' + items.length + ' 项曲线。';
  });
  $('#batch-transfer').onclick = () => transfer(items);
  refresh();
  return {
    count: () => items.length,
    preferred: duty => items.find(item => item.id === active &&
      JSON.stringify(item.duty) === JSON.stringify(duty))?.pump?.index,
    setPump(pump, duty) {
      const item = items.find(item => item.id === active);
      if (!item || JSON.stringify(item.duty) !== JSON.stringify(duty)) return;
      item.pump = pump;
      item.error = null;
      refresh();
    }
  };
}
