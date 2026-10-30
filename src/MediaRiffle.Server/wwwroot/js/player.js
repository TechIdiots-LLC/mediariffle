import { api } from './api.js';
import { h, formatTime } from './dom.js';
import { castAvailable, castButton, castMedia, onCastChange, isCasting } from './cast.js';

// Plays one file. The server picks how (Direct: the file as it is; Remux: an HLS stream of the copied video that
// starts at `startSeconds` into the file; adaptive: a transcode at several levels, the whole file on its own clock),
// and the controls here run on the file's clock either way: file time = session.startSeconds + video.currentTime.
//
// Auto is the adaptive stream: hls.js (or Safari, or the Chromecast) measures each segment's download and moves
// between levels by itself, as DASH players do; the server makes a level's segments when they're asked for. When
// the connection clearly has room for the file as it is, Auto plays that instead (no transcode), and falls back to
// the adaptive stream if it stops keeping up. Picking Original or one level is one stream at a time: changing it
// restarts the stream at the same spot.

const QUALITY_KEY = 'mediariffle.quality';
const SPEED_KEY = 'mediariffle.kbps';

// hls.js's segment loader, told how long each segment of an adaptive stream waited for the server's ffmpeg
// (X-Encode-Wait). That time is left out of its measure of the connection: otherwise a level just started (ffmpeg
// seeking and warming up) looks like a slow connection and one ffmpeg has run ahead on like a fast one, and Auto
// switches back and forth. Hands on how fast that level's ffmpeg is going (X-Encode-Speed, video seconds a second).
function encoderAwareLoader(Hls, onSpeed) {
  return class extends Hls.DefaultConfig.loader {
    load(context, config, callbacks) {
      super.load(context, config, { ...callbacks, onSuccess: (response, stats, ctx, details) => {
        const header = name => details?.getResponseHeader?.(name) ?? details?.headers?.get?.(name) ?? null;
        const wait = Number(header('X-Encode-Wait')) || 0;
        if (wait > 0) {
          for (const st of new Set([stats, ctx?.frag?.stats])) {
            if (st?.loading?.start) st.loading.start = Math.min(st.loading.start + wait, st.loading.first || st.loading.end);
          }
        }
        const speed = parseFloat(header('X-Encode-Speed'));
        if (Number.isFinite(speed)) onSpeed(ctx?.frag?.level, speed);
        callbacks.onSuccess(response, stats, ctx, details);
      } });
    }
  };
}

// What this browser decodes besides H.264, so the server can send x265 files as they are
function decoderCaps() {
  const mse = window.ManagedMediaSource || window.MediaSource;
  const test = document.createElement('video');
  const ok = codecs => !!(mse?.isTypeSupported?.(`video/mp4; codecs="${codecs}"`) ||
    test.canPlayType(`video/mp4; codecs="${codecs}"`) === 'probably');
  return { hevc: ok('hvc1.1.6.L120.90'), hevc10: ok('hvc1.2.4.L120.90') };
}

// Ways to get the picture onto a TV, the ones that fit this device first. Casting and AirPlay hand the TV the stream
// (best quality, the phone or laptop is free); mirroring sends the screen (works with more TVs, at lower quality).
function tvHelp() {
  const ua = navigator.userAgent;
  const apple = /Macintosh|iPhone|iPad/.test(ua) && !!window.WebKitPlaybackTargetAvailabilityEvent;
  const windows = /Windows/.test(ua), android = /Android/.test(ua);
  const chromium = !!window.chrome;
  const tips = [
    chromium && ['Chromecast or Google TV', 'the Cast button above (Chrome or Edge, on an HTTPS address)'],
    apple && ['Apple TV, or a TV or Roku with AirPlay 2', 'the AirPlay button above (appears when Safari finds one)'],
    windows && ['A TV or Roku that accepts screen mirroring (Miracast)', 'press Win+K and pick it. On a Roku, allow it under Settings → System → Screen mirroring'],
    android && ['A TV or Roku that accepts screen mirroring', 'Quick settings → Screen cast / Smart View / Cast'],
    apple && ['Any TV with an HDMI input', 'an HDMI adapter for the phone or Mac'],
    ['Then', 'press Full screen (or F): the video is centred, with a margin for TVs that cut off the edges'],
  ].filter(Boolean);
  return h('div', { class: 'tv-help muted', hidden: true },
    h('strong', {}, 'Watching on a TV'),
    h('ul', {}, tips.map(([what, how]) => h('li', {}, h('span', { class: 'tv-what' }, what), ': ', how))));
}

function remember(key, value) { try { localStorage.setItem(key, value); } catch { } }
function recall(key) { try { return localStorage.getItem(key); } catch { return null; } }

// Files whose original stalled on this device (a 60 Mbit/s 4K disc rip, say), and when: Auto then starts on the
// adaptive stream and doesn't step back up to the file as it is, which would only stall again, over and over. For
// 30 days, so a better connection or device gets another go; Original from the menu always plays it.
const STALLED_KEY = 'mediariffle.originalStalled';
const STALLED_DAYS = 30;
function stalledFiles() {
  try {
    const all = JSON.parse(recall(STALLED_KEY) || '{}');
    const cutoff = Date.now() - STALLED_DAYS * 86400000;
    return Object.fromEntries(Object.entries(all).filter(([, at]) => at > cutoff));
  } catch { return {}; }
}
const originalStalled = fileId => !!stalledFiles()[fileId];
function markOriginalStalled(fileId) {
  const all = stalledFiles();
  all[fileId] = Date.now();
  // The most recent few hundred: enough, and localStorage stays small
  const kept = Object.entries(all).sort((a, b) => b[1] - a[1]).slice(0, 300);
  remember(STALLED_KEY, JSON.stringify(Object.fromEntries(kept)));
}

// Full screen is the whole page's (the document), not the player's own: going on to the next one (from the controls, a
// play queue, a media key or a voice assistant's "next track") builds a new player inside the page, and full screen
// stays. While it lasts the page is "theater": the player lies over everything (see body.theater in site.css).
const fullscreenElement = () => document.fullscreenElement ?? document.webkitFullscreenElement ?? null;
export const inTheater = () => fullscreenElement() === document.documentElement;
const followTheater = () => document.body.classList.toggle('theater', inTheater());
document.addEventListener('fullscreenchange', followTheater);
document.addEventListener('webkitfullscreenchange', followTheater);

// Whether this browser lets a page set a video's volume: iOS keeps it at 1 (the hardware buttons set it)
let volumeCheck = null;
function volumeWorks() {
  if (volumeCheck === null) {
    const v = document.createElement('video');
    v.volume = 0.5;
    volumeCheck = v.volume === 0.5;
  }
  return volumeCheck;
}

function mountFlash(el, text, sub) {
  el.replaceChildren(h('div', { class: 'flash-title' }, text), sub ? h('div', { class: 'flash-sub' }, sub) : '');
  el.classList.add('on');
}

/** Leaves full screen (a page that isn't a player). */
export function exitTheater() {
  if (fullscreenElement()) (document.exitFullscreen ?? document.webkitExitFullscreen)?.call(document)?.catch?.(() => {});
}

export class Player {
  constructor(details) {
    this.details = details;              // GET /api/files/{id}
    this.fileId = details.summary.id;
    this.audio = details.audio.find(a => a.isDefault)?.index ?? details.audio[0]?.index ?? null;
    this.subtitle = null;                // "s3" / "x0" or null
    this.session = null;
    this.hls = null;
    this.dragging = false;
    this.lastSaved = 0;
    this.caps = decoderCaps();
    this.forCast = false;                // casting: H.264 only, as most Chromecasts can't decode HEVC

    // Qualities best first: [{id, label, kbps}], "original" first when the user may have it
    this.qualities = details.qualities;
    const saved = recall(QUALITY_KEY);
    this.choice = saved === 'auto' || this.qualities.some(q => q.id === saved) ? saved : 'auto';
    this.quality = this.choice === 'auto' ? null : this.choice;   // what's streaming; Auto fills it in
    // How the user likes Auto to pick (Profile → Video quality, or set by an admin): auto, high, medium or low, and the
    // ceiling that puts on it (with the admin's cap): a level id, or null
    this.preference = details.preferredQuality ?? 'auto';
    this.autoCap = details.autoCap ?? null;
    this.stalls = [];
    this.lastSwitch = 0;

    // AirPlay (Safari on Mac, iPhone, iPad): to an Apple TV, or a TV or Roku with AirPlay 2. The button shows only when
    // Safari sees a receiver; while the video plays there, it's H.264 (not every receiver decodes HEVC)
    this.airplayCapable = !!window.WebKitPlaybackTargetAvailabilityEvent;
    this.video = this.newVideo();
    this.status = h('div', { class: 'spinner' }, 'Starting…');
    // Previous/next: the play queue's, or the show's episode order (setNavigation); stop ends the stream
    this.nav = { previous: details.previous ?? null, next: details.next ?? null };
    const btn = (label, title, onclick) => h('button', { class: 'secondary icon', title, 'aria-label': title, onclick }, label);
    this.prevBtn = btn('⏮', 'Previous (P)', () => this.previous());
    this.backBtn = btn('↺10', 'Back 10 seconds (←)', () => this.seekBy(-10));
    this.playBtn = h('button', { class: 'icon', title: 'Play / pause (space)', onclick: () => this.togglePlay() }, '▶');
    this.fwdBtn = btn('10↻', 'Forward 10 seconds (→)', () => this.seekBy(10));
    // Hide this one (the page sets onHide) and go on to the next, in or out of full screen. Just the icon: written out,
    // "Hide" was taken for hiding the controls
    this.hideBtn = h('button', { class: 'secondary icon', title: 'Hide this video (from lists and Continue watching) and go to the next one',
      'aria-label': 'Hide this video and go to the next one', onclick: e => { e.stopPropagation(); this.onHide?.(); } }, '🚫');
    // Full screen: hides the controls now, without pausing (a tap or the mouse brings them back)
    this.hideControlsBtn = h('button', { class: 'secondary icon hide-controls', title: 'Hide the controls (tap or move the mouse to bring them back)',
      'aria-label': 'Hide the controls', onclick: e => { e.stopPropagation(); this.hideControls(); } }, '⌄');
    this.stopBtn = btn('⏹', 'Stop', () => this.stop());
    this.nextBtn = btn('⏭', 'Next (N)', () => this.goNext());
    this.muteBtn = btn('🔊', 'Mute (M)', () => { this.video.muted = !this.video.muted; });
    // iPhone and iPad ignore a page's volume (it's the hardware buttons'): no slider there, just Mute
    this.volume = h('input', { type: 'range', class: 'volume', min: 0, max: 1, step: 0.05, title: 'Volume (↑ ↓)',
      hidden: !volumeWorks(), oninput: () => { this.video.volume = +this.volume.value; this.video.muted = false; } });
    this.setNavigation(this.nav);
    const savedVolume = +recall('mediariffle.volume');
    this.video.volume = recall('mediariffle.volume') !== null && savedVolume >= 0 && savedVolume <= 1 ? savedVolume : 1;
    this.volume.value = String(this.video.volume);
    this.time = h('span', { class: 'time' }, '0:00 / ' + formatTime(details.summary.duration));
    this.seekBar = h('input', { type: 'range', class: 'seek', min: 0, max: Math.max(1, Math.floor(details.summary.duration)), step: 1, value: 0,
      oninput: () => { this.dragging = true; this.showTime(+this.seekBar.value); },
      onchange: () => { this.dragging = false; this.seek(+this.seekBar.value); } });
    this.audioSelect = h('select', { onchange: () => { this.audio = +this.audioSelect.value; this.restart(this.position(), { keepPaused: true, handover: true }); } },
      details.audio.map(a => h('option', { value: a.index, selected: a.index === this.audio }, a.label)));
    this.subSelect = h('select', { onchange: () => this.pickSubtitle(this.subSelect.value) },
      h('option', { value: '' }, 'Subtitles off'),
      details.subtitles.map(s => h('option', { value: s.id }, s.label + (s.text || s.pgs ? '' : ' (burned in)'))));
    this.autoOption = h('option', { value: 'auto' }, 'Auto');
    this.qualitySelect = h('select', { title: 'Quality', onchange: () => this.pickQuality(this.qualitySelect.value) },
      this.autoOption, this.qualities.map(q => h('option', { value: q.id }, q.label)));
    this.qualitySelect.value = this.choice;
    this.info = h('span', { class: 'muted' });
    this.castSlot = h('span', { class: 'cast-btn', hidden: true });
    this.next = h('span');

    this.wireVideo(this.video);

    this.onPageHide = () => this.saveProgress(true);
    addEventListener('pagehide', this.onPageHide);
    this.onKey = e => this.key(e);
    addEventListener('keydown', this.onKey);
    this.setupMediaSession();
    this.monitor = setInterval(() => { this.checkQuality(); this.keepAlive(); this.checkPgs(); }, 2000);
    this.lastAlive = 0;
    this.playingLevel = null;   // the adaptive stream's level playing now: { id, label }

    this.airplayBtn = h('button', { class: 'secondary', hidden: true, title: 'AirPlay to a TV',
      onclick: () => this.video.webkitShowPlaybackTargetPicker?.() }, 'AirPlay');

    castAvailable().then(ok => {
      if (!ok) return;
      this.castSlot.hidden = false;
      this.castSlot.append(castButton());
      this.offCast = onCastChange(casting => { if (casting) this.castHere(); else this.forCast = false; });
    });
  }

  // Full screen takes the video and the controls together, see .player-wrap in site.css. There the controls lie over
  // the video and fade out when the mouse is still for a few seconds and not over them, while playing.
  element() {
    this.root = h('div', { class: 'player-wrap',
      onpointermove: e => { this.pointerType = e.pointerType; this.wake(); },
      // A tap while the controls are hidden only brings them back (see the click below): on a phone, a tap is the only
      // way to, and it shouldn't pause the film as well
      onpointerdown: e => { this.pointerType = e.pointerType; this.revealing = this.root.classList.contains('idle'); this.wake(); } },
      h('div', { class: 'player', onclick: () => { if (this.revealing) { this.revealing = false; return; } this.togglePlay(); },
        ondblclick: () => this.fullscreen() },
        this.video, this.status, this.flashEl = h('div', { class: 'flash', 'aria-live': 'polite' })),
      this.controls = h('div', { class: 'controls' },
        this.seekBar,
        // In groups, so a narrow screen wraps them whole: playing, time and sound; the picture's settings; screens
        h('div', { class: 'row controls-row' },
          h('span', { class: 'row transport' }, this.playBtn, this.prevBtn, this.nextBtn, this.backBtn, this.fwdBtn, this.stopBtn, this.hideBtn),
          this.time,
          h('span', { class: 'row sound' }, this.muteBtn, this.volume),
          h('span', { class: 'row settings' },
            this.qualitySelect,
            this.details.audio.length > 1 ? this.audioSelect : null,
            this.details.subtitles.length > 0 ? this.subSelect : null),
          h('span', { class: 'row screens' },
            this.castSlot,
            this.airplayBtn,
            h('button', { class: 'secondary', title: 'Full screen (F)', onclick: () => this.fullscreen() }, '⛶ Full screen'),
            this.hideControlsBtn),
          this.info,
          this.next)),
      // Other ways to watch, under the controls (not in full screen)
      h('div', { class: 'player-extras' },
        h('div', { class: 'row' },
          h('button', { class: 'link', title: 'Ways to watch on a TV', onclick: () => this.tvHelp.toggleAttribute('hidden') }, 'Watching on a TV…'),
          h('button', { class: 'link', title: 'The file as it is, in the VLC app', onclick: () => this.playInVlc() }, 'Play in VLC…')),
        this.vlcPanel = h('div', { class: 'vlc-panel', hidden: true }),
        this.tvHelp = tvHelp()));
    return this.root;
  }

  // Play in VLC: the file as it is, from where this is now. VLC decodes it all itself (4K HEVC, HDR, Dolby Vision,
  // TrueHD) and the server only reads the file. On Android and an iPhone or iPad the VLC app opens with it; elsewhere
  // a small playlist (.m3u) is saved, which opens in VLC. The link can be copied too, for VLC's Open Network Stream.
  async playInVlc() {
    const panel = this.vlcPanel;
    if (!panel.hidden) { panel.hidden = true; return; }
    panel.hidden = false;
    panel.replaceChildren(h('span', { class: 'muted' }, 'Making a link…'));
    let link;
    try { link = await api(`/api/external/${this.fileId}`, { method: 'POST' }); }
    catch (e) { panel.replaceChildren(h('span', { class: 'error' }, e.message)); return; }
    const at = Math.max(0, Math.floor(this.position()));
    const title = (this.details.summary.title || link.name).replace(/[\r\n]+/g, ' ');
    const ua = navigator.userAgent;
    const ios = /iPhone|iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    const open = () => {
      this.video.pause();
      if (/Android/.test(ua)) {
        const u = new URL(link.url);
        location.href = `intent://${u.host}${u.pathname}#Intent;scheme=${u.protocol.slice(0, -1)};package=org.videolan.vlc;` +
          `type=video/*;S.title=${encodeURIComponent(title)};l.position=${at * 1000};end`;
      } else if (ios) {
        location.href = `vlc-x-callback://x-callback-url/stream?url=${encodeURIComponent(link.url)}`;
      } else {
        const m3u = `#EXTM3U\n#EXTINF:-1,${title}\n${at > 0 ? `#EXTVLCOPT:start-time=${at}\n` : ''}${link.url}\n`;
        const a = h('a', { href: URL.createObjectURL(new Blob([m3u], { type: 'audio/x-mpegurl' })),
          download: `${title.replace(/[\\/:*?"<>|]+/g, '').trim() || 'video'}.m3u` });
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 60000);
      }
    };
    const copied = h('span', { class: 'muted' });
    const copy = async () => {
      try { await navigator.clipboard.writeText(link.url); copied.textContent = 'Copied'; }
      catch { copied.textContent = link.url; }
    };
    const desktop = !/Android/.test(ua) && !ios;
    panel.replaceChildren(
      h('div', { class: 'row' },
        h('button', { class: 'secondary', onclick: open }, desktop ? 'Save playlist for VLC' : 'Open in VLC'),
        h('button', { class: 'secondary', onclick: copy }, 'Copy link'), copied),
      h('p', { class: 'muted' },
        desktop ? `Open the saved .m3u with VLC${at > 0 ? `; it starts at ${formatTime(at)}` : ''}. Or in VLC, Media → Open Network Stream and paste the link. `
          : 'Needs the VLC app. ',
        'VLC fetches the file from this server\'s address itself, so it has to be able to reach it. The link works for 24 hours; ' +
        'what you watch in VLC isn\'t remembered here.'));
  }

  // Shows the controls, and hides them again after 3 s unless paused, not full screen, or the mouse is over them. Only
  // a mouse: on a touch screen the last control tapped stays :hover, and the controls then never went away
  wake() {
    if (!this.root) return;
    this.root.classList.remove('idle');
    clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => {
      const full = inTheater();
      if (!full || this.video.paused || (this.pointerType === 'mouse' && this.controls.matches(':hover'))) return;
      this.root.classList.add('idle');
    }, 3000);
  }

  // The ⌄ button in full screen: the controls out of the way now, playing or paused
  hideControls() {
    if (!this.root || !inTheater()) return;
    clearTimeout(this.idleTimer);
    this.root.classList.add('idle');
  }

  // Where in the file we are
  position() {
    return (this.session?.startSeconds ?? 0) + (this.video.currentTime || 0);
  }

  async start(startAt = null, { autoplay = true } = {}) {
    this.status.textContent = 'Starting…';
    if (!this.quality) this.quality = await this.autoStart();
    if (this.destroyed) return;
    const s = await this.requestStream(startAt);
    // Closed while the server was starting it (the page moved on): close the stream, rather than play it unseen
    if (this.destroyed) { api(`/api/play/${s.sessionId}`, { method: 'DELETE', keepalive: true }).catch(() => {}); return; }
    this.useSession(s);
    this.attach(this.session);
    this.setTextTrack();
    this.setPgs();
    if (autoplay) { try { await this.video.play(); } catch { /* autoplay blocked: the Play button still works */ } }
    if (isCasting() && !this.forCast) this.castHere();
  }

  // A stream from `startAt` with the current audio, subtitles and quality; `replacing` keeps that stream running on
  // the server until this one takes over from it (handover)
  requestStream(startAt, replacing = null) {
    const burn = !!this.subtitle && this.burns(this.subtitle);
    const caps = this.forCast ? { hevc: false, hevc10: false } : this.caps;
    return api('/api/play', { method: 'POST', body: {
      fileId: this.fileId, audio: this.audio, subtitle: this.subtitle, burn: !!burn, start: startAt,
      quality: this.quality, hevc: caps.hevc, hevc10: caps.hevc10,
      // Auto trying Original: if the file has to be transcoded anyway, the adaptive stream instead
      adaptive: this.choice === 'auto', startLevel: this.levelFor(this.estimate()), replacing } });
  }

  // The stream that's playing now, and everything measured about the one before forgotten
  useSession(s) {
    this.session = s;
    this.stale = false;
    this.quality = s.quality;   // the server may have lowered it to the user's cap
    this.playingLevel = null;
    this.playingSince = null;
    this.stalls = [];
    this.aheadSamples = [];
    this.encodeSpeeds = new Map();   // adaptive level index → its ffmpeg's latest speed
    this.slowLevels = new Map();     // levels ffmpeg couldn't keep up with → when that was seen
    this.levelSince = 0;
    this.fit = null;      // the level the picture's shown size calls for, last seen
    this.forced = null;   // a step up made for a bigger picture: { level, at }
    this.seekBar.max = String(Math.max(1, Math.floor(this.duration())));
    this.showQuality();
  }

  // ── Quality ──────────────────────────────────────────────────────────────

  // Auto's first pick: the file as it is when the measured connection has plenty of room for it, else the adaptive
  // stream (which then follows the connection by itself). The user's preference changes that: High tries the file as it
  // is first, whatever was measured (the server sends the adaptive stream if it would need converting anyway, and the
  // checks below step down if the connection can't keep up); Medium and Low stay on the adaptive stream, below their
  // ceiling.
  async autoStart() {
    const original = this.qualities.find(q => q.id === 'original');
    // The file as it is stalled here before: the adaptive stream, whatever the preference
    if (originalStalled(this.fileId)) return 'auto';
    if (this.preference === 'high') return original ? 'original' : 'auto';
    let kbps = this.estimate();
    if (!kbps) kbps = await this.speedTest();
    if (kbps) remember(SPEED_KEY, String(Math.round(kbps)));
    if (this.underCeiling()) return 'auto';
    return original && original.kbps > 0 && original.kbps * 1.5 <= kbps ? 'original' : 'auto';
  }

  // Medium or Low: Auto stays on the adaptive stream, at or below their ceiling (never the file as it is)
  underCeiling() { return this.preference === 'medium' || this.preference === 'low'; }

  // The connection's speed as last measured (kbit/s), or 0
  estimate() { return +recall(SPEED_KEY) || 0; }

  // The best level that fits a connection of `kbps` with room to spare: where an adaptive stream starts. High starts at
  // the best; a ceiling (Medium, Low, the admin's cap) leaves out the levels above it.
  levelFor(kbps) {
    // The qualities come best first, so the cap's place in them splits them (not there: the picture is smaller)
    const capAt = this.qualities.findIndex(q => q.id === this.autoCap);
    const levels = this.qualities.filter((q, i) => q.id !== 'original' && (capAt < 0 || i >= capAt));
    if (this.preference === 'high') return levels[0]?.id ?? null;
    if (!kbps) return levels[Math.min(1, levels.length - 1)]?.id ?? null;   // not measured yet: one below the best
    return (levels.find(q => q.kbps * 1.3 <= kbps) ?? levels[levels.length - 1])?.id ?? null;
  }

  async speedTest() {
    try {
      const t0 = performance.now();
      const r = await fetch('/api/speedtest', { credentials: 'same-origin', cache: 'no-store' });
      const bytes = (await r.arrayBuffer()).byteLength;
      const ms = performance.now() - t0;
      return bytes * 8 / ms;   // bits per ms = kbit/s
    } catch { return 0; }
  }

  pickQuality(choice) {
    this.choice = choice;
    remember(QUALITY_KEY, choice);
    if (choice === 'auto') {
      // The file as it is: keep it, Auto takes over from here. One transcoded level: to the adaptive stream.
      if (this.session && !this.isAdaptiveAuto() && this.session.mode === 'Transcode') this.switchTo('auto');
      else this.showQuality();
      return;
    }
    if (choice !== this.quality) this.switchTo(choice);
  }

  isAdaptiveAuto() { return !!this.session?.adaptive && this.session.levels.length > 1; }

  async switchTo(id, why) {
    this.quality = id;
    this.lastSwitch = performance.now();
    if (why) this.info.textContent = why;
    await this.restart(this.position(), { keepPaused: true, handover: true });
  }

  showQuality() {
    const label = this.isAdaptiveAuto() ? this.playingLevel?.label
      : this.qualities.find(q => q.id === this.quality)?.label?.replace(/ \(.*\)$/, '') ?? this.quality;
    const pref = { high: 'High', medium: 'Medium', low: 'Low' }[this.preference];
    this.autoOption.textContent = `Auto${pref ? ' · ' + pref : ''}${this.choice === 'auto' && label ? ` (${label})` : ''}`;
    if (!this.session) return;
    this.info.textContent = this.session.adaptive
      ? `Transcode${this.playingLevel ? ' · ' + this.playingLevel.label : ''}`
      : `${this.session.mode}${this.session.mode === 'Transcode' ? '' : ' · ' + (this.session.videoCodec ?? '')}`;
  }

  // Every 2 s, for Auto. The adaptive stream switches levels by itself; this only moves between it and the file as
  // it is: to the adaptive stream when the file stops keeping up, back to the file after a long stretch at the best
  // level with plenty of room (when the file plays without a transcode)
  checkQuality() {
    if (this.choice !== 'auto' || !this.session || this.video.paused || this.forCast) return;
    const now = performance.now();
    this.stalls = this.stalls.filter(t => now - t < 30000);
    const est = this.hls?.bandwidthEstimate ? this.hls.bandwidthEstimate / 1000 : 0;
    if (est) remember(SPEED_KEY, String(Math.round(est)));
    const ahead = this.bufferAhead();
    const sinceSwitch = now - this.lastSwitch;
    const original = this.qualities.find(q => q.id === 'original');

    if (!this.session.adaptive) {
      // The buffer ahead shrinking while playing means it downloads slower than it plays; how fast it shrinks
      // says how much slower (one big segment can take long enough that no download speed is measured meanwhile)
      this.aheadSamples = [...(this.aheadSamples ?? []), { t: now, ahead }].filter(x => now - x.t <= 12000);
      const first = this.aheadSamples[0];
      const span = (now - first.t) / 1000;
      const slope = span >= 9 ? (ahead - first.ahead) / span : 0;   // buffer seconds gained per second
      const draining = ahead < 30 && slope < -0.3;
      const need = this.session.streamKbps || original?.kbps || 0;
      if ((this.stalls.length >= 2 || draining) && sinceSwitch > 15000) {
        // What it really downloads at: the speed the adaptive stream starts from
        if (need > 0) remember(SPEED_KEY, String(Math.round(need * Math.max(0.05, 1 + Math.min(0, slope)))));
        this.aheadSamples = [];
        markOriginalStalled(this.fileId);
        this.switchTo('auto', 'Slower connection: adapting to it');
      }
      return;
    }
    this.capLevels(now);
    const top = this.hls && this.hls.levels.length && this.hls.currentLevel === this.topLevel();
    // Back up to the file as it is: never under a Medium or Low ceiling, nor once it stalled here; sooner for High
    if (!this.underCeiling() && original && original.kbps > 0 && this.session.originalMode !== 'Transcode' && top &&
        !originalStalled(this.fileId) &&
        est > original.kbps * 1.6 && ahead > 20 && this.stalls.length === 0 && sinceSwitch > (this.preference === 'high' ? 30000 : 120000)) {
      this.lastSwitch = now;
      this.switchTo('original', 'Faster connection: the file as it is');
    }
  }

  // Auto never steps up to a level the server's ffmpeg has been seen making slower than it plays (it would stall,
  // step down, and try again, over and over); that's tried again after 10 minutes, as the server's load changes.
  // It also stays on a level 20 s before stepping up from it. Stepping down isn't held back.
  // Nor does it go above the size the picture is shown at (except for High): a small window doesn't need 1080p, and
  // going full screen steps up at once, when the connection allows, instead of after what's buffered plays out.
  capLevels(now) {
    const levels = this.hls?.levels.length ?? 0;
    if (!levels) return;
    const cur = this.hls.currentLevel;
    // Under 1.1: not enough ahead of playing to make up for a hiccup (the file server busy, a hard scene)
    if (cur > 0 && this.encodeSpeeds.get(cur) < 1.1) {
      this.slowLevels.set(cur, now);
      this.encodeSpeeds.delete(cur);
    }
    for (const [l, t] of this.slowLevels) if (now - t > 600000) this.slowLevels.delete(l);
    let cap = levels - 1;
    for (const l of this.slowLevels.keys()) cap = Math.min(cap, l - 1);
    const fit = this.preference === 'high' ? levels - 1 : this.fitLevel();
    const grew = this.fit != null && fit > this.fit;
    this.fit = fit;
    cap = Math.min(cap, fit);
    if (cur >= 0 && now - this.levelSince < 20000 && !grew) cap = Math.min(cap, cur);
    const want = cap >= levels - 1 ? -1 : Math.max(0, cap);
    if (this.hls.autoLevelCapping !== want) this.hls.autoLevelCapping = want;
    // Back to choosing by itself once the step up is made (or didn't come)
    if (this.forced && (this.hls.currentLevel === this.forced.level || now - this.forced.at > 20000)) {
      this.hls.loadLevel = -1;
      this.forced = null;
    }
    if (grew && cur >= 0 && cur < cap) {
      // The best level up to the cap its measured connection carries with room to spare
      const est = this.hls.bandwidthEstimate || 0;
      let to = cur;
      for (let i = cur + 1; i <= cap; i++) if (this.hls.levels[i].bitrate * 1.5 <= est) to = i;
      if (to > cur) {
        this.hls.nextLevel = to;   // drops what's buffered ahead at the smaller size, from the next segment
        this.forced = { level: to, at: now };
      }
    }
  }

  // hls.js's index of the best level of the smallest size at least as wide as the picture is shown, in device pixels
  // (less a tenth): of two at that size (1080p and 1080p High), the better
  fitLevel() {
    const levels = this.hls.levels, top = levels[levels.length - 1], v = this.video;
    if (!v.clientWidth || !v.clientHeight || !top?.width || !top.height) return levels.length - 1;
    const shown = Math.min(v.clientWidth, v.clientHeight * top.width / top.height) * (devicePixelRatio || 1);
    const width = levels.find(l => l.width >= shown * 0.9)?.width;
    return width ? levels.findLastIndex(l => l.width <= width) : levels.length - 1;
  }

  // hls.js's index of the best level (it orders levels by bitrate, lowest first)
  topLevel() {
    let best = 0;
    this.hls.levels.forEach((l, i) => { if (l.bitrate > this.hls.levels[best].bitrate) best = i; });
    return best;
  }

  // While paused the player fetches nothing; this keeps the stream from being stopped as idle
  keepAlive() {
    if (!this.session || !this.video.paused || Date.now() - this.lastAlive < 30000) return;
    this.lastAlive = Date.now();
    fetch(`/stream/${this.session.sessionId}/alive`, { cache: 'no-store' }).catch(() => {});
  }

  bufferAhead() {
    const t = this.video.currentTime, b = this.video.buffered;
    for (let i = 0; i < b.length; i++) if (b.start(i) <= t + 0.5 && b.end(i) >= t) return b.end(i) - t;
    return 0;
  }

  // ── Playback ─────────────────────────────────────────────────────────────

  newVideo() {
    const video = h('video', { playsinline: true, preload: 'auto' });
    if (this.airplayCapable) video.setAttribute('x-webkit-airplay', 'allow');
    return video;
  }

  // The player's listeners on the video element that's playing, all taken off together when another one takes over
  // (see handover)
  wireVideo(video) {
    this.videoEvents?.abort();
    this.videoEvents = new AbortController();
    const on = (type, fn) => video.addEventListener(type, fn, { signal: this.videoEvents.signal });
    // Full screen, theater, a resized window: Auto's levels follow the size the picture is shown at
    this.sizeWatch?.disconnect();
    this.sizeWatch = new ResizeObserver(() => {
      if (this.choice === 'auto' && this.session?.adaptive && !this.destroyed) this.capLevels(performance.now());
    });
    this.sizeWatch.observe(video);
    on('volumechange', () => {
      this.volume.value = String(video.muted ? 0 : video.volume);
      this.muteBtn.textContent = video.muted || video.volume === 0 ? '🔇' : '🔊';
      remember('mediariffle.volume', String(video.volume));
    });
    on('timeupdate', () => this.onTime());
    on('play', () => { this.playBtn.textContent = '⏸'; this.mediaState('playing'); this.wake(); });
    on('pause', () => { this.pausedAt = Date.now(); this.playBtn.textContent = '▶'; this.mediaState('paused'); this.saveProgress(); this.wake(); });
    on('waiting', () => {
      this.status.textContent = 'Loading…';
      // A stall while playing (not right after starting or seeking) is what Auto steps down for
      if (this.playingSince && performance.now() - this.playingSince > 4000 && !video.seeking) this.stalls.push(performance.now());
    });
    on('playing', () => { this.status.textContent = ''; this.playingSince ??= performance.now(); this.recovering = false; });
    on('ended', () => this.onEnded());
    on('error', () => {
      if (!this.session) return;
      // The file as it is failing after it had played: most likely its stream went away (stopped as idle while the
      // computer slept, the server restarted), not a file this browser can't play
      if (this.nearEnd()) { this.onEnded(); return; }   // ran out just before the end it said: finished
      if (this.session.mode === 'Direct' && this.playingSince && this.recoverStream()) return;
      this.status.textContent = 'This video can\'t be played here.';
    });
    if (this.airplayCapable) {
      on('webkitplaybacktargetavailabilitychanged', e => { this.airplayBtn.hidden = e.availability !== 'available'; });
      on('webkitcurrentplaybacktargetiswirelesschanged', () => this.airplayChanged());
    }
  }

  // Changing stream while playing (a quality, Auto moving between the file as it is and the adaptive stream, an audio
  // track, a burned-in subtitle): the new stream starts in a second video element, hidden and muted, a few seconds
  // ahead of where this one is, while this one goes on playing. Once the new one has the next few seconds after where
  // this one has got to, it takes over in one step: it jumps to that moment and plays, this one stops and its stream is
  // closed. No pause, no Loading…. If the new one isn't ready within 25 s (or something else changes meanwhile) it's
  // dropped and the change made the plain way instead.
  async handover() {
    const old = { video: this.video, hls: this.hls, session: this.session };
    const token = this.pending = {};
    const lead = 4;   // seconds ahead: about how long a stream takes to start
    let s, video, hls;
    const drop = () => {
      hls?.destroy();
      video?.remove();
      if (s) api(`/api/play/${s.sessionId}`, { method: 'DELETE' }).catch(() => {});
      if (this.pending === token) this.pending = null;
    };
    try {
      s = await this.requestStream(this.position() + lead, old.session.sessionId);
      if (this.pending !== token || this.session !== old.session) { drop(); return; }
      video = this.newVideo();
      video.muted = true;
      video.classList.add('standby');
      old.video.after(video);
      hls = this.attach(s, video);
      const ready = await this.whenReady(video, s, hls, () => this.position(), token);
      if (this.pending !== token || this.session !== old.session) { drop(); return; }
      if (!ready) throw new Error('not ready');
      // Where this one has got to, on the new stream's clock
      video.currentTime = Math.max(0, this.position() - (s.startSeconds ?? 0));
      await new Promise(r => { video.addEventListener('seeked', r, { once: true }); setTimeout(r, 1500); });
    } catch {
      const superseded = this.pending !== token || this.session !== old.session;
      drop();
      // Not ready in time, or it failed: the change made the plain way (unless something newer took its place)
      if (!superseded) await this.restart(this.position(), { keepPaused: true });
      return;
    }
    // The swap
    this.pending = null;
    this.wireVideo(video);
    this.video = video;
    this.hls = hls;
    this.useSession(s);
    video.volume = old.video.volume;
    video.muted = old.video.muted;
    video.playbackRate = old.video.playbackRate;
    video.classList.remove('standby');
    try { await video.play(); } catch { /* blocked: the Play button still works */ }
    old.video.pause();
    old.hls?.destroy();
    old.video.removeAttribute('src');
    old.video.load();
    old.video.remove();
    api(`/api/play/${old.session.sessionId}`, { method: 'DELETE' }).catch(() => {});
    this.setTextTrack();
    this.setPgs();
  }

  // Resolves true once `video` has the next few seconds after `at()` (where the playing stream is, on the file's
  // clock) buffered; false if its stream fails, after 25 s, or when it's dropped for something newer
  whenReady(video, s, hls, at, token) {
    return new Promise(resolve => {
      const started = performance.now();
      const timer = setInterval(() => {
        if (this.pending !== token || hls?.failed || video.error) { clearInterval(timer); resolve(false); return; }
        // A remux has only what ffmpeg has written so far, and starts slowly: more in hand before it takes over
        const need = s.adaptive || s.mode === 'Direct' ? 3 : 6;
        const rel = at() - (s.startSeconds ?? 0), b = video.buffered;
        for (let i = 0; i < b.length; i++) {
          if (b.start(i) <= rel + 0.25 && b.end(i) >= rel + need) { clearInterval(timer); resolve(true); return; }
        }
        if (performance.now() - started > 25000) { clearInterval(timer); resolve(false); }
      }, 200);
    });
  }

  attach(s, video = this.video) {
    const active = video === this.video;
    if (active) this.detach();
    // The file as it is, or an adaptive stream played by the browser itself (Safari), starts at seekTo once loaded
    const native = url => {
      video.src = url;
      if (s.seekTo > 0) video.addEventListener('loadedmetadata', () => { video.currentTime = s.seekTo; }, { once: true });
    };
    let hls = null;
    if (s.mode === 'Direct') {
      native(s.url);
    } else if (this.airplayCapable && video.canPlayType('application/vnd.apple.mpegurl')) {
      // Safari: its own HLS player, not hls.js. AirPlay can only hand a TV a real stream URL (which it then fetches
      // itself, as a Chromecast does), not media fed through Media Source Extensions
      native(s.url);
    } else if (window.Hls?.isSupported()) {
      const Hls = window.Hls;
      const est = this.estimate();
      // A browser holds only so much of a stream, behind and ahead together (Chrome: about 150 MB of video). A 4K
      // file as it is, at 50–100 Mbit/s, fills that in seconds: what's kept behind shrinks with the bitrate, so the
      // room goes to what's ahead, and that's asked for only as far as fits, instead of running into the limit
      const mbPerSec = (s.streamKbps || this.qualities.find(q => q.id === 'original')?.kbps || 0) / 8000;
      const fit = (mb, min, max) => mbPerSec > 0 ? Math.max(min, Math.min(max, Math.round(mb / mbPerSec))) : max;
      hls = new Hls({
        startPosition: s.adaptive ? (s.seekTo || 0) : 0, maxBufferLength: fit(110, 10, 60), backBufferLength: fit(20, 5, 90),
        // Its bandwidth estimate starts from the last one measured here, so the first level fits the connection
        ...(est ? { abrEwmaDefaultEstimate: est * 1000 } : {}),
        // Steps up only with plenty of room, judged over a longer stretch, so a few quick segments don't move it
        abrEwmaFastVoD: 4, abrEwmaSlowVoD: 15, abrBandWidthUpFactor: 0.5,
        ...(s.adaptive ? { fLoader: encoderAwareLoader(Hls, (level, speed) => {
          if (level >= 0 && video === this.video) this.encodeSpeeds.set(level, speed);
        }) } : {}),
      });
      // Its events count only while it's the one playing, not while it's getting ready to take over (handover)
      hls.on(Hls.Events.LEVEL_SWITCHED, (_, data) => {
        if (video !== this.video) return;
        this.levelSince = performance.now();
        const name = hls.levels[data.level]?.name;
        this.playingLevel = s.levels?.find(l => l.id === name) ?? null;
        this.showQuality();
      });
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return;
        if (video !== this.video) { hls.failed = true; return; }
        // Failing in the last stretch: the file ran out a little before the length it said (a container's duration
        // counts trailing audio, a last segment that never comes): that's the end, not an error to recover from
        if (this.nearEnd()) { this.onEnded(); return; }
        // The stream went away (stopped as idle, the server restarted)
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR && this.recoverStream()) return;
        this.status.textContent = 'Playback error: ' + (data.details ?? data.type);
      });
      hls.loadSource(s.url);
      hls.attachMedia(video);
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      native(s.url);   // Safari plays HLS itself
    } else {
      this.status.textContent = 'This browser can\'t play HLS.';
    }
    if (active) this.hls = hls;
    return hls;
  }

  detach() {
    if (this.hls) { this.hls.destroy(); this.hls = null; }
    this.video.removeAttribute('src');
    this.video.load();
  }

  // Text subtitles are a <track> of WebVTT, already shifted to the stream's start by the server
  setTextTrack() {
    for (const t of [...this.video.querySelectorAll('track')]) t.remove();
    const sub = this.session?.subtitles?.find(x => x.id === this.subtitle);
    if (!sub) return;
    const track = h('track', { kind: 'subtitles', src: sub.url, label: sub.label, srclang: sub.lang || 'und', default: true });
    track.addEventListener('load', () => { track.track.mode = 'showing'; });
    this.video.append(track);
  }

  // Whether a subtitle has to be burned into the video: an image one the browser can't draw itself (a DVD's), or any
  // image one on a TV, which plays the stream on its own (the PGS drawn here would stay on this screen)
  burns(id) {
    const s = this.details.subtitles.find(x => x.id === id);
    return !!s && !s.text && !(s.pgs && !this.forCast);
  }

  // PGS (Blu-ray) subtitles, drawn over the video by libpgs from the track's data (see PgsSubtitles on the server).
  // Until the server has the whole track (extracting it reads the whole file: minutes, for a 4K disc rip), it hands
  // out five minutes at a time from where we are; checkPgs asks again as those run out, and for the whole track.
  // Kept per player, so a new stream (a handover, a seek) draws from the same data straight away.
  async setPgs() {
    this.pgs?.renderer?.dispose();
    if (this.pgs) this.pgs.renderer = null;
    const sub = this.details.subtitles.find(x => x.id === this.subtitle);
    if (!sub?.pgs || this.forCast || !this.session) { this.pgs = null; return; }
    if (this.pgs?.id !== sub.id) this.pgs = { id: sub.id, data: null, complete: false, until: 0, asked: 0 };
    const pgs = this.pgs, video = this.video;
    try {
      const { PgsRenderer } = await import('/lib/libpgs/libpgs.js');
      if (this.pgs !== pgs || this.video !== video) return;   // changed meanwhile
      pgs.renderer = new PgsRenderer({ workerUrl: '/lib/libpgs/libpgs.worker.js', video });
      // Its clock is the video's; the subtitles' is the file's, which a stream started part-way is ahead of
      pgs.renderer.timeOffset = this.session.startSeconds ?? 0;
      if (pgs.data) pgs.renderer.loadFromBuffer(pgs.data.slice(0));
      if (!pgs.data || (!pgs.complete && this.position() > pgs.until - 60)) await this.loadPgs();
    } catch (e) { console.warn('PGS subtitles', e); }
  }

  // The whole track if the server has it, else the next five minutes from here (`completeOnly`: the whole track or nothing)
  async loadPgs(completeOnly = false) {
    const pgs = this.pgs;
    if (!pgs || pgs.complete || pgs.loading || !this.session) return;
    pgs.loading = true;
    pgs.asked = Date.now();
    try {
      const from = Math.max(0, Math.floor(this.position()) - 2);
      const r = await fetch(`/stream/${this.session.sessionId}/pgs/${pgs.id}.sup?from=${from}${completeOnly ? '&complete=true' : ''}`,
        { cache: 'no-store' });
      if (r.status === 204 || !r.ok) return;
      const data = await r.arrayBuffer();
      if (this.pgs !== pgs) return;
      pgs.data = data;
      pgs.complete = r.headers.get('X-Subtitle-Complete') === '1';
      pgs.until = pgs.complete ? Infinity : +(r.headers.get('X-Subtitle-Until') ?? 0);
      // A copy: the renderer may hand its buffer to its worker
      await pgs.renderer?.loadFromBuffer(data.slice(0));
    } catch { /* tried again by checkPgs */ }
    finally { pgs.loading = false; }
  }

  // Whether a PGS subtitle is on and drawn by this page (not burned in)
  drawsPgs() { return !!this.details.subtitles.find(x => x.id === this.subtitle)?.pgs; }

  // Every 2 s: five minutes running out, the next five; every 30 s, whether the whole track is there yet
  checkPgs() {
    const pgs = this.pgs;
    if (!pgs || pgs.complete || pgs.loading || !this.session) return;
    if (this.position() > pgs.until - 60) this.loadPgs();
    else if (Date.now() - pgs.asked > 30000) this.loadPgs(true);
  }

  pickSubtitle(id) {
    const before = this.subtitle;
    this.subtitle = id || null;
    const wasBurned = !!before && this.burns(before);
    const isBurned = !!this.subtitle && this.burns(this.subtitle);
    if (wasBurned || isBurned) this.restart(this.position(), { keepPaused: true, handover: true });   // image subtitles are part of the video
    else { this.setTextTrack(); this.setPgs(); }
  }

  async seek(t) {
    if (!this.session) return;
    if (this.session.mode === 'Direct') { this.video.currentTime = t; return; }
    // Inside what this stream has already made: seek in it. Anywhere else: a new stream from there
    const rel = t - this.session.startSeconds;
    const end = this.video.seekable.length ? this.video.seekable.end(this.video.seekable.length - 1) : 0;
    if (rel >= 0 && rel <= end - 1) this.video.currentTime = rel;
    else await this.restart(t);
  }

  // A new stream from `at` (where we are, by default) with the current audio, subtitles and quality. `handover`:
  // while playing, the new one takes over without a pause (see handover); paused, casting or on AirPlay, the plain way
  async restart(at = this.position(), { keepPaused = false, handover = false } = {}) {
    // (Not in the last seconds, where there isn't enough left for a new stream to get ready in)
    if (handover && this.session && !this.video.paused && !this.forCast && !this.video.webkitCurrentPlaybackTargetIsWireless &&
        this.duration() - this.position() > 15) {
      await this.handover();
      return;
    }
    this.pending = null;   // a handover getting ready is dropped
    const wasPaused = this.video.paused;
    const old = this.session?.sessionId;
    this.session = null;
    if (old) api(`/api/play/${old}`, { method: 'DELETE' }).catch(() => {});
    try { await this.start(at, { autoplay: !(keepPaused && wasPaused) }); }
    catch (e) { this.status.textContent = e.message; }
  }

  togglePlay() {
    if (!this.session) { this.resume(); return; }   // after Stop: start again where it stopped
    if (this.video.paused) this.play(); else this.video.pause();
  }

  // Play from a pause. A stream paused for long may be gone: the server stops one nobody has fetched from for a
  // minute or two, and the "still here" while paused (keepAlive) doesn't reach it once the computer sleeps or the
  // browser freezes the tab. Then a new one from the same place, rather than a player stuck on Loading… until the
  // page is reloaded. A short pause just plays.
  async play() {
    const longPause = Date.now() - (this.pausedAt ?? Date.now()) > 60000;
    if (this.stale || (longPause && !(await this.streamAlive()))) {
      this.stale = false;
      await this.restart(this.position());
      return;
    }
    try { await this.video.play(); } catch { /* blocked: the Play button still works */ }
  }

  // Whether the server still has this stream (asking counts as activity, so it's then kept a while longer)
  async streamAlive() {
    if (!this.session) return false;
    try { return (await fetch(`/stream/${this.session.sessionId}/alive`, { cache: 'no-store' })).ok; }
    catch { return true; }   // the server unreachable: let the player try, and say what fails
  }

  // The stream failed, most likely gone from the server. Paused, nothing needs it yet: a new one now would only go
  // idle too, so the next Play starts it. Playing: a new one from here at once, and again later if that one goes
  // too (`recovering` is cleared when it plays). False when it already tried and the new one failed before playing.
  recoverStream() {
    if (this.video.paused) { this.stale = true; this.status.textContent = ''; return true; }
    if (this.recovering) return false;
    this.recovering = true;
    this.restart(this.position());
    return true;
  }

  // ── Transport ──────────────────────────────────────────────────────────────

  // Where previous/next go: the play queue's neighbours when it's playing one, else the show's episode order
  setNavigation({ previous, next }) {
    this.nav = { previous: previous ?? null, next: next ?? null };
    this.prevBtn.disabled = false;   // Previous also means "back to the start" of this one
    this.nextBtn.disabled = !this.nav.next;
  }

  seekBy(seconds) {
    if (!this.session) return;
    const t = Math.min(Math.max(0, this.position() + seconds), Math.max(0, this.duration() - 1));
    this.seek(t);
  }

  // As on most players: back to the start if more than a few seconds in, else the one before
  previous() {
    if (this.session && this.position() > 5) this.seek(0);
    else if (this.nav.previous) location.hash = `#/play/${this.nav.previous}`;
    else if (this.session) this.seek(0);
  }

  goNext() { if (this.nav.next) location.hash = `#/play/${this.nav.next}`; }

  // Stop: the stream ends (freeing the server), the place is kept, and Play picks up from it
  stop() {
    if (!this.session) return;
    this.pending = null;   // a handover getting ready is dropped
    this.stoppedAt = this.position();
    this.pgs?.renderer?.dispose();
    if (this.pgs) this.pgs.renderer = null;
    this.saveProgress();
    const id = this.session.sessionId;
    this.session = null;
    this.detach();
    api(`/api/play/${id}`, { method: 'DELETE' }).catch(() => {});
    this.status.textContent = 'Stopped';
    this.playBtn.textContent = '▶';
    this.mediaState('none');
  }

  async resume() {
    try { await this.start(this.stoppedAt ?? null); }
    catch (e) { this.status.textContent = e.message; }
  }

  key(e) {
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (e.target instanceof HTMLElement && e.target.closest('input, select, textarea, button')) {
      if (e.key !== ' ' || e.target.closest('input[type=range]') === null) return;   // space on the seek bar still plays
    }
    const actions = {
      ' ': () => this.togglePlay(), k: () => this.togglePlay(),
      ArrowLeft: () => this.seekBy(-10), ArrowRight: () => this.seekBy(10),
      ArrowUp: () => { this.video.volume = Math.min(1, this.video.volume + 0.1); this.video.muted = false; },
      ArrowDown: () => { this.video.volume = Math.max(0, this.video.volume - 0.1); },
      m: () => { this.video.muted = !this.video.muted; },
      f: () => this.fullscreen(),
      n: () => this.goNext(), N: () => this.goNext(),
      p: () => this.previous(), P: () => this.previous(),
    };
    const act = actions[e.key];
    if (!act) return;
    e.preventDefault();
    act();
    this.wake();
  }

  // The keyboard's media keys and the OS media panel (Windows, Android, macOS) drive the player too
  setupMediaSession() {
    const ms = navigator.mediaSession;
    if (!ms) return;
    try {
      ms.metadata = new MediaMetadata({ title: this.details.summary.title, artwork: [{ src: this.details.summary.image }] });
      const set = (action, fn) => { try { ms.setActionHandler(action, fn); } catch { /* not supported here */ } };
      set('play', () => this.togglePlay());
      set('pause', () => this.video.pause());
      set('stop', () => this.stop());
      set('seekbackward', d => this.seekBy(-(d.seekOffset || 10)));
      set('seekforward', d => this.seekBy(d.seekOffset || 10));
      set('previoustrack', () => this.previous());
      set('nexttrack', () => this.goNext());
    } catch { /* no media session */ }
  }

  mediaState(state) {
    try { if (navigator.mediaSession) navigator.mediaSession.playbackState = state; } catch { }
  }

  fullscreen() {
    if (fullscreenElement()) { exitTheater(); return; }
    const page = document.documentElement;
    const enter = page.requestFullscreen ?? page.webkitRequestFullscreen;
    if (enter) enter.call(page);
    // iPhone Safari: only the video itself goes full screen (its own controls, and it leaves at the next one)
    else this.video.webkitEnterFullscreen?.();
  }

  /** A line over the picture for a few seconds (in full screen: what's playing, what's next). */
  flash(text, sub = null, ms = 4000) {
    mountFlash(this.flashEl, text, sub);
    clearTimeout(this.flashTimer);
    this.flashTimer = setTimeout(() => this.flashEl.classList.remove('on'), ms);
  }

  /** Starting from the beginning, with an old place saved: for a few seconds, a button to go to it instead. */
  offerResume(at, ms = 10000) {
    this.offerEl?.remove();
    const done = () => { clearTimeout(timer); this.offerEl?.remove(); this.offerEl = null; };
    const timer = setTimeout(done, ms);
    this.offerEl = h('div', { class: 'resume-offer' },
      h('span', {}, `Stopped at ${formatTime(at)} last time`),
      h('button', { type: 'button', onclick: () => { done(); this.seek(at); } }, `Resume from ${formatTime(at)}`),
      h('button', { type: 'button', class: 'secondary', onclick: done }, 'Start over'));
    this.flashEl.after(this.offerEl);
  }

  onTime() {
    const pos = this.position();
    if (!this.dragging) { this.seekBar.value = Math.floor(pos); this.showTime(pos); }
    if (!this.video.paused && Date.now() - this.lastSaved > 10000) this.saveProgress();
  }

  // How long this plays: an adaptive stream says (it counts from the first frame), else what the scan found
  duration() { return this.session?.adaptive && this.session.duration > 0 ? this.session.duration : this.details.summary.duration; }

  // In the last stretch of the file: the last 20 s, or 1% of a long one, as a file's stated length can run past what
  // there is to play by that much
  nearEnd() {
    const d = this.duration();
    return d > 0 && d - this.position() < Math.max(20, d * 0.01);
  }

  showTime(pos) {
    this.time.textContent = `${formatTime(pos)} / ${formatTime(this.duration())}`;
  }

  saveProgress(leaving = false, finished = undefined) {
    if (!this.session) return;
    // The old place is on offer (the video started at 0): saving 0:03 now would lose it before it can be picked
    if (this.offerEl && finished === undefined) return;
    this.lastSaved = Date.now();
    api('/api/progress', { method: 'POST', keepalive: leaving,
      body: { fileId: this.fileId, position: this.position(), finished } }).catch(() => {});
  }

  onEnded() {
    // Once per stream: a stream that fails at its end (nearEnd) can also say "ended", and a play queue moving on twice
    // would skip one
    if (!this.session || this.endedFor === this.session.sessionId) return;
    this.endedFor = this.session.sessionId;
    // An HLS stream also ends at its last segment when the file does
    this.saveProgress(false, true);
    if (this.onFinished?.()) return;   // a play queue moves on by itself
    if (this.details.next) {
      this.next.replaceChildren(h('a', { href: `#/play/${this.details.next}` }, 'Next episode →'));
    }
  }

  // Playing on (or back from) an AirPlay receiver. The receiver fetches the stream itself; a copied HEVC stream is
  // restarted as H.264 at the same spot, as for a Chromecast, and Auto quality rests while the TV plays
  async airplayChanged() {
    const wireless = !!this.video.webkitCurrentPlaybackTargetIsWireless;
    this.status.textContent = wireless ? 'Playing on the AirPlay device' : '';
    if (wireless && !this.forCast) {
      this.forCast = true;
      // (and PGS subtitles burned in, which this screen would draw but the TV can't)
      if (this.session && ((this.session.videoCodec !== 'h264' && this.session.mode !== 'Direct') || this.drawsPgs()))
        await this.restart(this.position(), { keepPaused: true });
    } else if (!wireless && !isCasting()) {
      this.forCast = false;
    }
  }

  // Hands the stream to the Chromecast: it fetches the same session URL itself. Most Chromecasts can't decode
  // HEVC, so a copied x265 stream is restarted as H.264 first.
  async castHere() {
    if (!this.session) return;
    // So are PGS subtitles drawn here: the Chromecast needs them burned into the video
    if (!this.forCast && ((this.session.videoCodec !== 'h264' && this.session.mode !== 'Direct') || this.drawsPgs())) {
      this.forCast = true;
      await this.restart(this.position(), { keepPaused: true });
      return;   // start() casts the new stream
    }
    this.forCast = true;
    const url = new URL(this.session.url, location.origin).href;
    const subtitle = this.session.subtitles?.find(x => x.id === this.subtitle);
    castMedia({
      url, contentType: this.session.contentType, hls: this.session.mode !== 'Direct',
      fmp4: this.session.segments === 'Fmp4',
      title: this.details.summary.title,
      currentTime: this.session.mode === 'Direct' ? this.position() : this.video.currentTime,
      subtitleUrl: subtitle ? new URL(subtitle.url, location.origin).href : null,
    });
    this.video.pause();
  }

  destroy() {
    // Anything still starting (start, a handover, a restart) stops at its next step
    this.destroyed = true;
    this.video.pause();
    this.pgs?.renderer?.dispose();
    this.pgs = null;
    this.pending = null;
    this.saveProgress(true);
    clearInterval(this.monitor);
    clearTimeout(this.idleTimer);
    this.sizeWatch?.disconnect();
    removeEventListener('pagehide', this.onPageHide);
    removeEventListener('keydown', this.onKey);
    for (const a of ['play', 'pause', 'stop', 'seekbackward', 'seekforward', 'previoustrack', 'nexttrack'])
      try { navigator.mediaSession?.setActionHandler(a, null); } catch { }
    this.offCast?.();
    const id = this.session?.sessionId;
    this.detach();
    if (id && !isCasting()) api(`/api/play/${id}`, { method: 'DELETE', keepalive: true }).catch(() => {});
  }
}
