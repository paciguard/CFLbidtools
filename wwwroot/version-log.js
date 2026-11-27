const dialog = document.querySelector('#version-log');
const status = document.querySelector('#version-log-status');
const content = document.querySelector('#version-log-content');
let loading = false;

function renderNotes(notes) {
  const panel = document.querySelector('#version-notes');
  panel.replaceChildren();
  let list = null;
  const append = (parent, tag, text) => {
    const element = document.createElement(tag);
    element.textContent = text;
    parent.append(element);
    return element;
  };
  for (const raw of notes.split('\n')) {
    const line = raw.trim();
    if (line.startsWith('- ')) {
      list ??= append(panel, 'ul', '');
      append(list, 'li', line.slice(2));
    } else if (line && !line.startsWith('# ')) {
      list = null;
      if (line.startsWith('## ')) append(panel, 'h4', line.slice(3));
      else append(panel, 'p', line);
    }
  }
}

document.querySelector('#open-version-log').onclick = async () => {
  dialog.showModal();
  if (loading || !content.hidden) return;
  loading = true;
  status.textContent = '正在读取版本记录…';
  try {
    const response = await fetch('/api/version');
    if (!response.ok) throw new Error('Version log unavailable');
    const { version, buildNumber, notes, revisions } = await response.json();
    renderNotes(notes);
    document.querySelector('#version-revisions').textContent = revisions.join('\n') || '此构建未包含 Git 修订记录。';
    status.textContent = 'v' + version + ' · 构建 ' + buildNumber;
    content.hidden = false;
  } catch {
    status.textContent = '暂时无法读取此构建的版本记录。';
  } finally {
    loading = false;
  }
};
