// Lists and categories as a spreadsheet (CSV) file: Export to keep or edit them, Import to add to them or update them.
// An import is checked first (nothing changes) and said in words, to go ahead with or not.
import { api, download } from './api.js';
import { h, mount } from './dom.js';

const plural = (n, one, many = one + 's') => `${n} ${n === 1 ? one : many}`;

// What an import would do (or did), in words
function summary(r, noun, replace, done) {
  const parts = [];
  if (r.added) parts.push(`${r.added} ${done ? 'added' : 'to add'}`);
  if (r.removed) parts.push(`${r.removed} ${done ? 'taken off' : 'to take off'}`);
  if (r.kept) parts.push(`${r.kept} already on ${replace ? 'them (in the file\'s order)' : 'them'}`);
  const lines = [`The file names ${plural(r.lists, noun)}${parts.length ? ': ' + parts.join(', ') : ''}.`];
  if (r.unmatchedCount) lines.push(`${plural(r.unmatchedCount, 'row')} found nothing in the libraries${done ? '' : ' and will be skipped'}.`);
  return lines.join(' ');
}

// `exportPath` and `importPath`: the API's file endpoint; `noun`: "list" or "category"; `onDone` after an import
export function listFileBox({ noun, nouns = noun + 's', exportPath, importPath, onDone = null }) {
  const report = h('div');
  const mode = h('select', { 'aria-label': 'How to import' },
    h('option', { value: 'add' }, `Add to the ${nouns} the file names`),
    h('option', { value: 'replace' }, `Make the ${nouns} the file names what it says`));
  const input = h('input', { type: 'file', accept: '.csv,.tsv,.txt,text/csv', hidden: true });
  input.onchange = async () => {
    const file = input.files[0];
    input.value = '';
    if (!file) return;
    const replace = mode.value === 'replace';
    const path = `${importPath}?mode=${mode.value}`;
    try {
      const check = await api(`${path}&preview=true`, { method: 'POST', body: file });
      if (!check.added && !check.removed && !(replace && check.kept)) {
        mount(report, h('p', { class: 'muted' }, summary(check, noun, replace, false), ' Nothing to change.'), unmatchedTable(check));
        return;
      }
      if (!confirm(`${summary(check, noun, replace, false)}\n\nGo ahead?`)) return;
      const done = await api(path, { method: 'POST', body: file });
      mount(report, h('p', { class: 'ok' }, 'Imported. ', summary(done, noun, replace, true)), unmatchedTable(done));
      onDone?.();
    } catch (e) { mount(report, h('p', { class: 'error' }, e.message)); }
  };
  return h('div', {},
    h('div', { class: 'row' },
      h('button', { class: 'secondary', onclick: () => download(exportPath, `mediariffle-${nouns}.csv`).catch(e => alert(e.message)) }, 'Export'),
      h('button', { class: 'secondary', onclick: () => input.click() }, 'Import…'), mode, input),
    report);
}

// The rows that found nothing (the server sends the first 200)
function unmatchedTable(r) {
  if (!r.unmatched?.length) return null;
  return [
    h('p', {}, r.unmatchedCount > r.unmatched.length ? `The first ${r.unmatched.length} rows that found nothing:` : 'Rows that found nothing:'),
    h('table', {}, h('tr', {}, h('th', {}, 'Line'), h('th', {}, 'List'), h('th', {}, 'Kind'), h('th', {}, 'Title'), h('th', {}, 'Why')),
      r.unmatched.map(u => h('tr', {}, h('td', {}, String(u.line)), h('td', {}, u.list), h('td', {}, u.kind), h('td', {}, u.title), h('td', { class: 'muted' }, u.why)))),
  ];
}

// What the file holds, for the help under the buttons
export const LIST_FILE_HELP = 'A row for each show, movie, artist, album, song or game, by name: Kind is Show, Movie, Artist, Album, Song ' +
  'or Game, and Year, Artist, Album, System, Library and File help find the right one. Edit it in a spreadsheet and import it again; ' +
  'rows can be added, removed or moved (a list of songs plays in the file\'s order).';
