(() => {
  if (!window.CflDevice?.request) return;
  if (window === parent) {
    let sequence = 0;
    const pending = new Map();
    window.cflOfflineCall = (path, data, save) => new Promise((resolve, reject) => {
      const id = ++sequence;
      pending.set(id, { resolve, reject });
      try { window.CflDevice.request(JSON.stringify({ id, path, data, save })); }
      catch (error) { pending.delete(id); reject(error); }
    });

    window.cflReply = ({ id, data, error, issues }) => {
      const request = pending.get(id);
      if (!request) return;
      pending.delete(id);
      if (error) request.reject(Object.assign(new Error(error), { issues }));
      else request.resolve(data);
    };
    window.cflReplyPart = (id, index, total, content) => {
      const request = pending.get(id);
      if (!request || index < 0 || index >= total || total < 1 || total > 4096) return;
      const parts = request.parts ??= new Map();
      parts.set(index, content);
      if (parts.size !== total) return;
      try { window.cflReply(JSON.parse(Array.from({ length: total }, (_, i) => parts.get(i)).join(''))); }
      catch (error) { pending.delete(id); request.reject(error); }
    };
  }

  window.cflOfflineSave = async (path, data = null) => {
    const result = await parent.cflOfflineCall(path, data, true);
    if (!result.saved) throw new DOMException('已取消保存', 'AbortError');
  };
  const nativeFetch = window.fetch.bind(window);
  window.fetch = async (input, options) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    if (url.origin !== location.origin || !url.pathname.startsWith('/api/')) return nativeFetch(input, options);
    const headers = { 'Content-Type': 'application/json' };
    try {
      const data = options?.body ? JSON.parse(options.body) : null;
      const result = await parent.cflOfflineCall(url.pathname + url.search, data, false);
      return new Response(JSON.stringify(result), { headers });
    } catch (error) {
      return new Response(JSON.stringify({ error: error.message, issues: error.issues }), { status: 400, headers });
    }
  };
})();
