const cacheName = 'cfl-workbench-2';
const shell = ['/', '/workbench.css', '/workbench.js', '/lucide.min.js', '/app.webmanifest',
  '/icon-192.png', '/icon-512.png', '/sdoc-assets/cfl-logo.jpg'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(shell)));
});
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(keys
    .filter(key => key.startsWith('cfl-workbench-') && key !== cacheName).map(key => caches.delete(key)))));
});
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;
  if (event.request.mode === 'navigate' && url.pathname === '/') {
    event.respondWith(fetch(event.request).catch(() => caches.match('/')));
  } else if (shell.includes(url.pathname)) {
    event.respondWith(fetch(event.request).catch(() => caches.match(url.pathname)));
  }
});
