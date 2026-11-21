import { supportsProjectFolders as supported, chooseDirectory, checkDirectory, storedFolder, savePdf } from './pdf-files.js';

const button = document.querySelector('#choose-project-folder');
const status = document.querySelector('#project-folder-status');
const zipButton = document.querySelector('#download-drawings');
let project;
let folder;
let draftKey;
let plannedItems = 1;
let actualItems = 0;
let files = [];
let revision = 0;
let reading = false;
let picking = false;
let busy = false;

function controls() {
  button.hidden = !supported;
  button.disabled = !project || busy || picking;
  status.hidden = !project;
  zipButton.disabled = !files.length || busy;
}

function notifyParent(directoryChanged = false) {
  if (draftKey) window.parent.postMessage({ type: 'cfl-project-layout', key: draftKey,
    items: actualItems, directoryChanged }, location.origin);
}

export async function updateProjectFolder(data, selection) {
  files = data.files;
  actualItems = data.rows.length;
  if (project === data.job) { controls(); notifyParent(); return; }
  project = data.job;
  folder = null;
  draftKey = selection?.folderKey;
  plannedItems = Number(selection?.plannedItems) || 1;
  reading = Boolean(project) && supported;
  const version = ++revision;
  status.textContent = supported
    ? '未选择 PDF 工作目录'
    : '此浏览器无法选择本机工作目录；图纸可从项目中下载。';
  controls();
  if (!reading) return;
  try {
    const saved = await storedFolder(draftKey || project);
    if (version !== revision) return;
    if (draftKey) {
      folder = saved?.directory;
      await storedFolder(project, { directory: folder, draftKey, plannedItems });
    } else if (saved) {
      folder = saved.directory ?? (saved.kind === 'directory' ? saved : null);
      draftKey = saved.draftKey;
      plannedItems = saved.plannedItems || 1;
      if (draftKey) folder = (await storedFolder(draftKey))?.directory ?? folder;
    }
    if (version === revision && folder) status.textContent = 'PDF 工作目录：' + folder.name;
  } catch {} finally {
    if (version === revision) reading = false;
  }
  if (version === revision) notifyParent();
}

button.onclick = async () => {
  if (!project || busy || picking) return;
  const key = project;
  picking = true;
  controls();
  try {
    const selected = await chooseDirectory();
    if (key !== project) return;
    ++revision;
    reading = false;
    folder = selected;
    status.textContent = 'PDF 工作目录：' + folder.name;
    try {
      await storedFolder(key, { directory: selected, draftKey, plannedItems });
      if (draftKey) await storedFolder(draftKey, { directory: selected });
      notifyParent(true);
    }
    catch { if (key === project) status.textContent += '（仅本次打开有效）'; }
  } catch (error) {
    if (error.name !== 'AbortError' && key === project)
      status.textContent = (folder ? 'PDF 工作目录仍为 ' + folder.name + '。' : '') + '无法选择新目录，请检查写入权限。';
  } finally {
    picking = false;
    controls();
  }
};
zipButton.onclick = async () => {
  if (!files.length || busy) return;
  const href = '/api/submissions/pdf-batch?multiple=' + (Math.max(actualItems, plannedItems) > 1);
  if (window.cflOfflineSave) {
    zipButton.disabled = true;
    try { await window.cflOfflineSave(href); }
    catch (error) { if (error.name !== 'AbortError') status.textContent = error.message; }
    finally { controls(); }
    return;
  }
  const download = document.createElement('a');
  download.href = href;
  download.download = 'Drawings_V0.zip';
  document.body.append(download);
  download.click();
  download.remove();
};
window.addEventListener('cfl-control-state', event => { busy = event.detail; controls(); });
window.addEventListener('message', async event => {
  if (event.origin !== location.origin || event.source !== window.parent ||
      event.data?.type !== 'cfl-folder-selected' || !draftKey || event.data.key !== draftKey) return;
  const version = ++revision;
  const saved = await storedFolder(draftKey).catch(() => null);
  if (version !== revision || !saved?.directory) return;
  folder = saved.directory;
  status.textContent = 'PDF 工作目录：' + folder.name;
});
controls();

export async function requiredProjectFolder() {
  if (!supported) return null;
  if (!folder) {
    if (reading) throw new Error('正在读取项目工作目录。请稍候再试。');
    status.textContent = '请先选择工作目录，再生成或导出 PDF。';
    button.scrollIntoView({ block: 'nearest' });
    throw new Error('请先在“项目信息”中选择工作目录，再生成或导出 PDF。');
  }
  return checkDirectory(folder);
}

export function projectOutput(id) {
  return files.find(file => file.id === id);
}

export async function saveProjectPdf(directory, file) {
  if (!file) throw new Error('未找到此图纸。请重新生成后再导出。');
  const response = await fetch('/api/submissions/file/pdf?id=' + file.id);
  if (!response.ok || !response.headers.get('Content-Type')?.startsWith('application/pdf'))
    throw new Error('无法读取生成的 PDF。');
  const kind = Math.max(actualItems, plannedItems) > 1 && ['GA', 'SEC', 'PID'].includes(file.kind) ? file.kind : undefined;
  return savePdf(directory, file.name, await response.blob(), kind);
}

export async function saveGeneratedPdfs(directory, drawings) {
  const failed = [];
  for (const file of drawings) {
    try { await saveProjectPdf(directory, file); }
    catch { failed.push(file.name + '.pdf'); }
  }
  return { folder: directory.name, saved: drawings.length - failed.length, failed };
}
