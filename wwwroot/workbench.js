const $ = (selector) => document.querySelector(selector);
const number = (value, digits = 2) => value == null ? '—' : value.toLocaleString('en-US', { maximumFractionDigits: digits });
const form = $('#duty-form');
const fields = form.elements;
let previousDuty, selected, searchVersion = 0, curveVersion = 0, catalogVersion = 0;
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
  const view = ['pumps', 'catalog', 'submissions'].includes(hash) ? hash : 'pumps';
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
  try {
    const options = await api('/api/pumps/options');
    for (const family of options.families) fields.family.add(new Option(family));
    for (const type of options.apiTypes) fields.apiType.add(new Option(type));
  } catch (error) { announce(error.message); }
}
window.addEventListener('DOMContentLoaded', () => window.lucide?.createIcons());

function clearDetails() {
  selected = undefined;
  $('#selection-title').textContent = '性能曲线';
  $('#selection-subtitle').textContent = '';
  $('#metrics').hidden = true;
  $('#print').disabled = true;
  $('#plots').replaceChildren();
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  const version = ++searchVersion;
  ++curveVersion;
  const duty = Object.fromEntries([...new FormData(form)].filter(([, value]) => value !== '')
    .map(([key, value]) => [key, ['family', 'apiType'].includes(key) ? value : Number(value)]));
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
    $('#empty').textContent = '当前工况无匹配泵型。';
    for (const pump of results) {
      const row = document.createElement('tr');
      row.setAttribute('aria-selected', 'false');
      const name = pump.family + ' ' + pump.size;
      const first = document.createElement('td');
      const button = text('button', name);
      button.type = 'button';
      first.append(button);
      row.append(first, ...[pump.stages, number(pump.rpm, 0), number(pump.diameter), number(pump.efficiency),
        number(pump.npshr), number(pump.power)].map(value => text('td', value)));
      row.addEventListener('click', () => showPump(pump, row, duty));
      $('#results').append(row);
    }
    if (results.length) showPump(results[0], $('#results tr'), duty);
  } catch (error) {
    if (version !== searchVersion) return;
    $('#count').textContent = '';
    announce(error.message);
  }
});

async function showPump(pump, row, duty) {
  const version = ++curveVersion;
  clearDetails();
  $('#results tr[aria-selected="true"]')?.setAttribute('aria-selected', 'false');
  row.setAttribute('aria-selected', 'true');
  $('#selection-title').textContent = pump.family + ' ' + pump.size;
  $('#selection-subtitle').textContent = [pump.apiType, pump.stages + ' 级', pump.rpm + ' rpm',
    pump.flowType && '流型 ' + pump.flowType, pump.group && '水力组 ' + pump.group, pump.curve].filter(Boolean).join(' · ');
  $('#metrics').replaceChildren(...[
    ['额定流量', number(duty.flow) + ' m³/h'], ['扬程（要求 / 实际）', number(duty.head) + ' / ' + number(pump.head) + ' m'],
    ['最小 / 额定 / 最大叶轮', [pump.minDiameter, pump.diameter, pump.maxDiameter].map(x => number(x)).join(' / ') + ' mm'],
    ['轴功率 / 推荐电机', number(pump.power) + ' / ' + number(pump.motorKw) + ' kW'],
    ['效率', number(pump.efficiency) + ' %'], ['NPSHr', number(pump.npshr) + ' m'],
    ['吸入型式', pump.suction || '—'], ['布置方式', pump.orientation || '—']
  ].map(([label, value]) => { const field = document.createElement('div'); field.append(text('dt', label), text('dd', value)); return field; }));
  $('#metrics').hidden = false;
  $('#plots').append(text('p', '正在绘制性能曲线…'));
  $('#plots').firstChild.className = 'empty';
  try {
    const width = Math.max(410, Math.round($('#plots').clientWidth));
    const result = await api('/api/pumps/curves', { duty, index: pump.index, width });
    if (version !== curveVersion) return;
    const labels = ['流量 – 扬程与效率', '流量 – NPSHr', '流量 – 轴功率'];
    $('#plots').replaceChildren(...result.plots.map((svg, index) => {
      const figure = document.createElement('figure');
      const image = new Image();
      image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
      image.alt = labels[index];
      figure.append(image);
      return figure;
    }));
    selected = pump;
    $('#print').disabled = false;
  } catch (error) {
    if (version === curveVersion) $('#plots').firstChild.textContent = error.message;
  }
}

$('#print').onclick = () => window.print();
$('#open-submission').onclick = () => {
  if (selected) sessionStorage.setItem('cfl.selectedDuty', JSON.stringify({ pump: selected, duty: previousDuty }));
  else sessionStorage.removeItem('cfl.selectedDuty');
  location.hash = 'submissions';
};

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
      row.append(...[curve.family, curve.size, curve.name + ' #' + curve.id, number(curve.rpm, 0),
        number(curve.minDiameterMm) + ' / ' + number(curve.maxDiameterMm),
        curve.points, curve.issues.join(' · ') || '—'].map(value => text('td', value)));
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
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(console.warn);
init();
