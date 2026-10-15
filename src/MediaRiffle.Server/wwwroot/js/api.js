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
  let data = null;
  try { data = text ? JSON.parse(text) : null; }
  catch { if (r.ok) throw new ApiError(r.status, 'The server sent something unexpected.'); }
  if (!r.ok) throw new ApiError(r.status, data?.error ?? data?.detail ?? `Request failed (${r.status})`, data);
  return data;
}
