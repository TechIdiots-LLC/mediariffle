// Builds elements without HTML strings, so names from the library (file and folder names) can never be read as
// markup or script.

export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, String(v));
  }
  for (const c of children.flat(Infinity)) {
    if (c === undefined || c === null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function mount(container, ...children) {
  container.replaceChildren(...children.flat(Infinity).filter(Boolean));
}

export function formatTime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) seconds = 0;
  const s = Math.floor(seconds % 60), m = Math.floor(seconds / 60) % 60, hr = Math.floor(seconds / 3600);
  const two = n => String(n).padStart(2, '0');
  return hr > 0 ? `${hr}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

// The Content Security Policy blocks style="" attributes; styles set through the CSSOM (el.style.x) are allowed
// `kind` 'music' shows a note where there's no cover art
export function art(src, progress, kind = null) {
  let bar = null;
  if (progress > 0) {
    bar = h('div', { class: 'bar' });
    bar.style.width = `${Math.min(100, progress * 100).toFixed(1)}%`;
  }
  return h('div', { class: kind ? `art ${kind}` : 'art' },
    h('img', { src, alt: '', loading: 'lazy', onerror: e => { e.target.style.visibility = 'hidden'; } }),
    bar);
}
