import { api } from './api.js';

// What's playing in a row: a category, library or show, in order or shuffled. Kept per browser tab
// (sessionStorage), so a reload carries on and another tab has its own.

const KEY = 'mediariffle.queue';

export function getQueue() {
  try { return JSON.parse(sessionStorage.getItem(KEY)) ?? null; } catch { return null; }
}

function setQueue(q) {
  try { q ? sessionStorage.setItem(KEY, JSON.stringify(q)) : sessionStorage.removeItem(KEY); } catch { }
}

export function clearQueue() { setQueue(null); }

// `what`: { tag } | { library } | { show }
export async function startQueue(what, shuffle, label) {
  const params = new URLSearchParams({ ...what, shuffle: String(!!shuffle) });
  const items = await api(`/api/queue?${params}`);
  if (!items.length) throw new Error('Nothing to play here yet.');
  setQueue({ label: (shuffle ? 'Shuffle: ' : '') + label, items, index: 0 });
  location.hash = `#/play/${items[0].id}`;
}

// The queue's place for this file (the file being opened makes it current), or null if it isn't in the queue
export function positionOf(fileId) {
  const q = getQueue();
  if (!q) return null;
  let i = q.items[q.index]?.id === fileId ? q.index : q.items.findIndex(x => x.id === fileId);
  if (i < 0) return null;
  q.index = i;
  setQueue(q);
  return { label: q.label, index: i, count: q.items.length, next: q.items[i + 1] ?? null, previous: q.items[i - 1] ?? null };
}
