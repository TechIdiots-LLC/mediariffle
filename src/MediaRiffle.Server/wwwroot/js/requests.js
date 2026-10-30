// Requests: a form to ask for something to be added to the library, and the list of what's been asked for. Admins see
// everyone's and tick them off; everyone else sees their own.

import { api } from './api.js';
import { h, mount } from './dom.js';

const KINDS = ['Movie', 'Show', 'Music', 'Other'];

export async function requestsView(view, { isAdmin }) {
  const list = h('div');
  const message = h('p', { class: 'muted' });

  const title = h('input', { type: 'text', maxlength: 200, required: true, placeholder: 'Title', 'aria-label': 'Title' });
  const year = h('input', { type: 'number', min: 1850, max: new Date().getFullYear() + 5, placeholder: 'Year', 'aria-label': 'Year', class: 'year' });
  const kind = h('select', { 'aria-label': 'Kind' }, KINDS.map(k => h('option', { value: k }, k)));
  const link = h('input', { type: 'url', maxlength: 500, placeholder: 'Link (store, IMDb…), optional', 'aria-label': 'Link' });
  const notes = h('textarea', { maxlength: 1000, rows: 2, placeholder: 'Notes: edition, format, season, where you saw it… (optional)', 'aria-label': 'Notes' });
  const send = h('button', { type: 'submit' }, 'Request');

  const form = h('form', { class: 'card request-form', onsubmit: async e => {
    e.preventDefault();
    send.disabled = true;
    try {
      await api('/api/requests', { method: 'POST', body: {
        title: title.value, year: year.value ? +year.value : null, kind: kind.value, link: link.value, notes: notes.value } });
      title.value = year.value = link.value = notes.value = '';
      message.className = 'muted';
      message.textContent = 'Requested.';
      await refresh();
      title.focus();
    } catch (err) { message.className = 'error'; message.textContent = err.message; }
    finally { send.disabled = false; }
  } },
    h('div', { class: 'row' }, title, year, kind),
    link, notes,
    h('div', { class: 'row' }, send, message));

  async function refresh() {
    try { mount(list, rows(await api('/api/requests'))); }
    catch (err) { mount(list, h('p', { class: 'error' }, err.message)); }
  }

  function rows(requests) {
    if (requests.length === 0) return h('p', { class: 'muted' }, isAdmin ? 'Nothing has been requested.' : 'You haven\'t requested anything.');
    const open = requests.filter(r => !r.doneUtc), done = requests.filter(r => r.doneUtc);
    return [
      h('h2', {}, `Wanted (${open.length})`),
      open.length ? table(open) : h('p', { class: 'muted' }, 'Nothing waiting.'),
      done.length ? [h('h2', {}, `Done (${done.length})`), table(done)] : null,
    ];
  }

  function table(items) {
    return h('table', { class: 'requests' },
      h('tr', {}, h('th', {}, isAdmin ? 'Done' : ''), h('th', {}, 'Title'), h('th', {}, 'Kind'),
        isAdmin ? h('th', {}, 'By') : null, h('th', {}, 'Asked'), h('th', {})),
      items.map(r => h('tr', { class: r.doneUtc ? 'done' : '' },
        h('td', {}, isAdmin
          ? h('input', { type: 'checkbox', checked: !!r.doneUtc, 'aria-label': `${r.title} is done`, onchange: async e => {
              try { await api(`/api/requests/${r.id}/done`, { method: 'PUT', body: { done: e.target.checked } }); await refresh(); }
              catch (err) { e.target.checked = !e.target.checked; alert(err.message); }
            } })
          : (r.doneUtc ? '✓' : '')),
        h('td', {},
          h('strong', {}, r.title), r.year ? ` (${r.year})` : '',
          r.link ? [' ', h('a', { href: r.link, target: '_blank', rel: 'noopener noreferrer' }, 'link')] : null,
          r.notes ? h('div', { class: 'muted notes' }, r.notes) : null),
        h('td', {}, r.kind),
        isAdmin ? h('td', {}, r.by ?? '') : null,
        h('td', { class: 'muted' }, new Date(/Z$/.test(r.createdUtc) ? r.createdUtc : r.createdUtc + 'Z').toLocaleDateString()),
        h('td', {}, (isAdmin || r.mine) ? h('button', { class: 'secondary', onclick: async () => {
          if (!confirm(`Remove the request for ${r.title}?`)) return;
          try { await api(`/api/requests/${r.id}`, { method: 'DELETE' }); await refresh(); } catch (err) { alert(err.message); }
        } }, 'Remove') : null))));
  }

  mount(view,
    h('h1', {}, 'Requests'),
    h('p', { class: 'muted' }, isAdmin
      ? 'What people would like added. Tick one off when it\'s in.'
      : 'Ask for a movie, show or album to be added. Give the title and year, and a link or notes if they help find it.'),
    form, list);
  await refresh();
}
