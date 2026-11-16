// Calls to the MediaRiffle API. Every request that changes something carries the antiforgery token, which the
// server checks on top of its SameSite=Strict cookies.

let csrf = null;

export async function refreshCsrf() {
  const r = await fetch('/api/auth/csrf', { credentials: 'same-origin' });
  if (!r.ok) throw new ApiError(r.status, `The server couldn't start a session (error ${r.status}); see its log.`);
  csrf = (await r.json()).token;
}

export class ApiError extends Error {
  // `data`: the reply itself (e.g. { mfa: "verify" } when Admin wants two-step verification first)
  constructor(status, message, data = null) { super(message); this.status = status; this.data = data; }
}

export async function api(path, { method = 'GET', body, keepalive = false } = {}) {
  const headers = { 'Accept': 'application/json' };
  // A Blob (a picture) goes as it is, with its own type; anything else as JSON
  const raw = body instanceof Blob;
  if (body !== undefined) headers['Content-Type'] = raw ? (body.type || 'application/octet-stream') : 'application/json';
  if (method !== 'GET') {
    if (!csrf) await refreshCsrf();
    headers['X-CSRF-TOKEN'] = csrf;
  }
  const r = await fetch(path, {
    method, headers, credentials: 'same-origin', keepalive,
    body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
  });
  if (r.status === 401 && !path.startsWith('/api/auth/')) {
    location.hash = '#/login';
    throw new ApiError(401, 'Please sign in.');
  }
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; }
  catch { if (r.ok) throw new ApiError(r.status, 'The server sent something unexpected.'); }
  if (!r.ok) throw new ApiError(r.status, data?.error ?? data?.detail ?? `Request failed (${r.status})`, data);
  return data;
}

// A file from the API saved by the browser, under the name the server gives it (or `fallbackName`)
export async function download(path, fallbackName) {
  const r = await fetch(path, { credentials: 'same-origin' });
  if (r.status === 401) { location.hash = '#/login'; throw new ApiError(401, 'Please sign in.'); }
  if (!r.ok) {
    let data = null;
    try { data = await r.json(); } catch { }
    throw new ApiError(r.status, data?.error ?? (r.status === 404 ? 'There\'s nothing to export.' : `Request failed (${r.status})`), data);
  }
  const disposition = r.headers.get('Content-Disposition') ?? '';
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const plain = /filename="?([^";]+)"?/i.exec(disposition)?.[1];
  const a = document.createElement('a');
  a.href = URL.createObjectURL(await r.blob());
  a.download = utf8 ? decodeURIComponent(utf8) : plain ?? fallbackName;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
}
