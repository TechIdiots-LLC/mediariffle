// Profile pictures: one of the built-in ones (a cartoon animal or food on a colour; everyone starts with one their
// account picks), or the user's own photo, which is cropped square and shrunk here before it's sent.

import { h } from './dom.js';

// The same 24, in the same order, as the server's Avatars.BuiltIn counts
export const BUILT_IN = [
  ['🦙', 'llama', '#f6c177'], ['🦥', 'sloth', '#c5a880'], ['🦝', 'raccoon', '#a7b5c4'], ['🐸', 'frog', '#9ad18b'],
  ['🦖', 'T. rex', '#7fc8a9'], ['🐙', 'octopus', '#f4a6b8'], ['🦆', 'duck', '#ffe08a'], ['🐧', 'penguin', '#a8d8ff'],
  ['🦉', 'owl', '#d9b38c'], ['🐌', 'snail', '#e3c8f2'], ['🦩', 'flamingo', '#ffb3c7'], ['🦦', 'otter', '#c9a98b'],
  ['🐐', 'goat', '#d7e3a2'], ['🦔', 'hedgehog', '#e8c39e'], ['🐢', 'turtle', '#a3d9c9'], ['🦄', 'unicorn', '#d6c3ff'],
  ['👽', 'alien', '#b4f0a8'], ['🤖', 'robot', '#b8c6d9'], ['👻', 'ghost', '#d3d3e8'], ['🥔', 'potato', '#e6cfa0'],
  ['🌮', 'taco', '#ffd28a'], ['🍕', 'pizza', '#ffb38a'], ['🧀', 'cheese', '#fff09a'], ['🍩', 'donut', '#ffc2e0'],
];

/** A user's picture, as a circle: `avatar` is { photo } or { emoji }; `size` is 'sm' (menus), 'md' (lists) or 'lg'. */
export function avatarEl(avatar, size = 'sm', name = '') {
  if (avatar?.photo) return h('img', { class: `avatar avatar-${size}`, src: avatar.photo, alt: name ? `${name}'s picture` : '' });
  const [emoji, what, colour] = BUILT_IN[avatar?.emoji ?? 0] ?? BUILT_IN[0];
  const el = h('span', { class: `avatar avatar-${size}`, role: 'img', 'aria-label': name ? `${name}'s picture: a ${what}` : `a ${what}` }, emoji);
  el.style.background = colour;
  return el;
}

/**
 * A picture file (a photo from the phone or a computer) as a 256×256 JPEG: cropped to its middle square and turned the
 * right way up (createImageBitmap reads the photo's orientation, and decodes what the browser can: HEIC on iPhone).
 */
export async function photoToJpeg(file, size = 256) {
  const bitmap = await createImageBitmap(file);
  const side = Math.min(bitmap.width, bitmap.height);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d');
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size);
  bitmap.close?.();
  return new Promise((resolve, reject) =>
    canvas.toBlob(b => b ? resolve(b) : reject(new Error('That picture couldn\'t be read.')), 'image/jpeg', 0.85));
}
