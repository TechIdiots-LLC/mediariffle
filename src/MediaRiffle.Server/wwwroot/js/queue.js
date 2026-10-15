import { api } from './api.js';
import { pageFor } from './music.js';

// What's playing in a row: a category, one of the user's lists, a library, a show, an album or an artist, in order or shuffled.
// Kept per browser tab (sessionStorage), so a reload carries on and another tab has its own.

const KEY = 'mediariffle.queue';

export function getQueue() {
  try { return JSON.parse(sessionStorage.getItem(KEY)) ?? null; } catch { return null; }
}

function setQueue(q) {
  try { q ? sessionStorage.setItem(KEY, JSON.stringify(q)) : sessionStorage.removeItem(KEY); } catch { }
}

export function clearQueue() { setQueue(null); }

// `what`: { tag } | { list } | { library } | { show } | { album } | { artist }. `startId` starts at that item instead
// of the first (a track picked on an album page, with the rest of the album after it).
export async function startQueue(what, shuffle, label, startId = null) {
  const params = new URLSearchParams({ ...what, shuffle: String(!!shuffle) });
  const items = await api(`/api/queue?${params}`);
  if (!items.length) throw new Error('Nothing to play here yet.');
  const index = Math.max(0, items.findIndex(x => x.id === startId));
  setQueue({ label: (shuffle ? 'Shuffle: ' : '') + label, items, index });
  location.hash = pageFor(items[index]);
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
