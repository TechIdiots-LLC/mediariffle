// Calls to the MediaRiffle API. Every request that changes something carries the antiforgery token, which the
// server checks on top of its SameSite=Strict cookies.

let csrf = null;

export async function refreshCsrf() {
  const r = await fetch('/api/auth/csrf', { credentials: 'same-origin' });
  csrf = (await r.json()).token;
}

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

export async function api(path, { method = 'GET', body, keepalive = false } = {}) {
  const headers = { 'Accept': 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') {
    if (!csrf) await refreshCsrf();
    headers['X-CSRF-TOKEN'] = csrf;
  }
  const r = await fetch(path, {
    method, headers, credentials: 'same-origin', keepalive,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (r.status === 401 && !path.startsWith('/api/auth/')) {
    location.hash = '#/login';
    throw new ApiError(401, 'Please sign in.');
  }
  const text = await r.text();
  const data = text ? JSON.parse(text) : null;
  if (!r.ok) throw new ApiError(r.status, data?.error ?? data?.detail ?? `Request failed (${r.status})`);
  return data;
}
