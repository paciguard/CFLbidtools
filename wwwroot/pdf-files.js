export const supportsProjectFolders = !window.cflOfflineSave && typeof window.showDirectoryPicker === 'function';
const mode = { mode: 'readwrite' };

export const chooseDirectory = () => window.showDirectoryPicker({ ...mode, id: 'cfl-project-folder' });
export async function checkDirectory(directory) {
  if (typeof directory.requestPermission === 'function' && await directory.requestPermission(mode) !== 'granted')
    throw new Error('工作目录没有写入权限。请重新选择目录后重试。');
  return directory;
}

export function storedFolder(key, replacement) {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open('CflProjectFolders', 1);
    open.onupgradeneeded = () => open.result.createObjectStore('folders');
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const database = open.result;
      try {
        const transaction = database.transaction('folders', replacement ? 'readwrite' : 'readonly');
        const store = transaction.objectStore('folders');
        const request = replacement ? store.put(replacement, key) : store.get(key);
        transaction.oncomplete = () => { database.close(); resolve(request.result); };
        transaction.onabort = () => { database.close(); reject(transaction.error); };
      } catch (error) {
        database.close();
        reject(error);
      }
    };
  });
}

export async function savePdf(directory, name, blob, subfolder) {
  const target = subfolder ? await directory.getDirectoryHandle(subfolder, { create: true }) : directory;
  const base = name.replace(/[\\/:*?"<>|]/g, '_').replace(/[. ]+$/, '') || 'Drawing';
  let destination;
  for (let index = 1; !destination; index++) {
    const candidate = base + (index === 1 ? '' : ' (' + index + ')') + '.pdf';
    try { await target.getFileHandle(candidate); }
    catch (error) {
      if (error.name === 'NotFoundError') destination = await target.getFileHandle(candidate, { create: true });
      else if (error.name !== 'TypeMismatchError') throw error;
    }
  }
  const writer = await destination.createWritable();
  try {
    await writer.write(blob);
    await writer.close();
  } catch (error) {
    await writer.abort().catch(() => {});
    throw error;
  }
  return subfolder ? subfolder + '/' + destination.name : destination.name;
}
