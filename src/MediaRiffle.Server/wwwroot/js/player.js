import { api } from './api.js';
import { h, formatTime } from './dom.js';
import { castAvailable, castMedia, onCastChange, isCasting } from './cast.js';

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

    this.video = h('video', { playsinline: true, preload: 'auto' });
    this.status = h('div', { class: 'spinner' }, 'Starting…');
    // Previous/next: the play queue's, or the show's episode order (setNavigation); stop ends the stream
    this.nav = { previous: details.previous ?? null, next: details.next ?? null };
    const btn = (label, title, onclick) => h('button', { class: 'secondary icon', title, 'aria-label': title, onclick }, label);
    this.prevBtn = btn('⏮', 'Previous (P)', () => this.previous());
    this.backBtn = btn('↺10', 'Back 10 seconds (←)', () => this.seekBy(-10));
    this.playBtn = h('button', { class: 'icon', title: 'Play / pause (space)', onclick: () => this.togglePlay() }, '▶');
    this.fwdBtn = btn('10↻', 'Forward 10 seconds (→)', () => this.seekBy(10));
    // Hide this one (the page sets onHide) and go on to the next, in or out of full screen
    this.hideBtn = h('button', { class: 'secondary', title: 'Hide this and go to the next one',
      onclick: e => { e.stopPropagation(); this.onHide?.(); } }, '🚫 Hide');
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
    this.video.addEventListener('volumechange', () => {
      this.volume.value = String(this.video.muted ? 0 : this.video.volume);
      this.muteBtn.textContent = this.video.muted || this.video.volume === 0 ? '🔇' : '🔊';
      remember('mediariffle.volume', String(this.video.volume));
    });
    this.time = h('span', { class: 'time' }, '0:00 / ' + formatTime(details.summary.duration));
    this.seekBar = h('input', { type: 'range', class: 'seek', min: 0, max: Math.max(1, Math.floor(details.summary.duration)), step: 1, value: 0,
      oninput: () => { this.dragging = true; this.showTime(+this.seekBar.value); },
      onchange: () => { this.dragging = false; this.seek(+this.seekBar.value); } });
    this.audioSelect = h('select', { onchange: () => { this.audio = +this.audioSelect.value; this.restart(); } },
      details.audio.map(a => h('option', { value: a.index, selected: a.index === this.audio }, a.label)));
    this.subSelect = h('select', { onchange: () => this.pickSubtitle(this.subSelect.value) },
      h('option', { value: '' }, 'Subtitles off'),
      details.subtitles.map(s => h('option', { value: s.id }, s.label + (s.text ? '' : ' (burned in)'))));
    this.autoOption = h('option', { value: 'auto' }, 'Auto');
    this.qualitySelect = h('select', { title: 'Quality', onchange: () => this.pickQuality(this.qualitySelect.value) },
      this.autoOption, this.qualities.map(q => h('option', { value: q.id }, q.label)));
    this.qualitySelect.value = this.choice;
    this.info = h('span', { class: 'muted' });
    this.castSlot = h('span', { class: 'cast-btn', hidden: true });
    this.next = h('span');

    this.video.addEventListener('timeupdate', () => this.onTime());
    this.video.addEventListener('play', () => { this.playBtn.textContent = '⏸'; this.mediaState('playing'); this.wake(); });
    this.video.addEventListener('pause', () => { this.playBtn.textContent = '▶'; this.mediaState('paused'); this.saveProgress(); this.wake(); });
    this.video.addEventListener('waiting', () => {
      this.status.textContent = 'Loading…';
      // A stall while playing (not right after starting or seeking) is what Auto steps down for
      if (this.playingSince && performance.now() - this.playingSince > 4000 && !this.video.seeking) this.stalls.push(performance.now());
    });
    this.video.addEventListener('playing', () => { this.status.textContent = ''; this.playingSince ??= performance.now(); this.recovering = false; });
    this.video.addEventListener('ended', () => this.onEnded());
    this.video.addEventListener('error', () => { if (this.session) this.status.textContent = 'This video can\'t be played here.'; });

    this.onPageHide = () => this.saveProgress(true);
    addEventListener('pagehide', this.onPageHide);
    this.onKey = e => this.key(e);
    addEventListener('keydown', this.onKey);
    this.setupMediaSession();
    this.monitor = setInterval(() => { this.checkQuality(); this.keepAlive(); }, 2000);
    this.lastAlive = 0;
    this.playingLevel = null;   // the adaptive stream's level playing now: { id, label }

    // AirPlay (Safari on Mac, iPhone, iPad): to an Apple TV, or a TV or Roku with AirPlay 2. The button shows only when
    // Safari sees a receiver; while the video plays there, it's H.264 (not every receiver decodes HEVC)
    this.airplayCapable = !!window.WebKitPlaybackTargetAvailabilityEvent;
    this.airplayBtn = h('button', { class: 'secondary', hidden: true, title: 'AirPlay to a TV',
      onclick: () => this.video.webkitShowPlaybackTargetPicker?.() }, 'AirPlay');
    if (this.airplayCapable) {
      this.video.setAttribute('x-webkit-airplay', 'allow');
      this.video.addEventListener('webkitplaybacktargetavailabilitychanged', e => {
        this.airplayBtn.hidden = e.availability !== 'available';
      });
      this.video.addEventListener('webkitcurrentplaybacktargetiswirelesschanged', () => this.airplayChanged());
    }

    castAvailable().then(ok => {
      if (!ok) return;
      this.castSlot.hidden = false;
      this.castSlot.append(document.createElement('google-cast-launcher'));
      this.offCast = onCastChange(casting => { if (casting) this.castHere(); else this.forCast = false; });
    });
  }

  // Full screen takes the video and the controls together, see .player-wrap in site.css. There the controls lie over
  // the video and fade out when the mouse is still for a few seconds and not over them, while playing.
  element() {
    this.root = h('div', { class: 'player-wrap', onpointermove: () => this.wake(), onpointerdown: () => this.wake() },
      h('div', { class: 'player', onclick: () => this.togglePlay(), ondblclick: () => this.fullscreen() },
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
            h('button', { class: 'secondary', title: 'Ways to watch on a TV', onclick: () => this.tvHelp.toggleAttribute('hidden') }, 'On a TV…')),
          this.info,
          this.next),
        this.tvHelp = tvHelp()));
    return this.root;
  }

  // Shows the controls, and hides them again after 3 s unless paused, not full screen, or the mouse is over them
  wake() {
    if (!this.root) return;
    this.root.classList.remove('idle');
    clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => {
      const full = inTheater();
      if (!full || this.video.paused || this.controls.matches(':hover')) return;
      this.root.classList.add('idle');
    }, 3000);
  }

  // Where in the file we are
  position() {
    return (this.session?.startSeconds ?? 0) + (this.video.currentTime || 0);
  }

  async start(startAt = null, { autoplay = true } = {}) {
    this.status.textContent = 'Starting…';
    if (!this.quality) this.quality = await this.autoStart();
    const burn = this.subtitle && !this.details.subtitles.find(s => s.id === this.subtitle)?.text;
    const caps = this.forCast ? { hevc: false, hevc10: false } : this.caps;
    const s = await api('/api/play', { method: 'POST', body: {
      fileId: this.fileId, audio: this.audio, subtitle: this.subtitle, burn: !!burn, start: startAt,
      quality: this.quality, hevc: caps.hevc, hevc10: caps.hevc10,
      // Auto trying Original: if the file has to be transcoded anyway, the adaptive stream instead
      adaptive: this.choice === 'auto', startLevel: this.levelFor(this.estimate()) } });
    this.session = s;
    this.quality = s.quality;   // the server may have lowered it to the user's cap
    this.playingLevel = null;
    this.playingSince = null;
    this.stalls = [];
    this.aheadSamples = [];
    this.seekBar.max = String(Math.max(1, Math.floor(this.duration())));
    this.showQuality();
    this.attach(s);
    this.setTextTrack();
    if (autoplay) { try { await this.video.play(); } catch { /* autoplay blocked: the Play button still works */ } }
    if (isCasting() && !this.forCast) this.castHere();
  }

  // ── Quality ──────────────────────────────────────────────────────────────

  // Auto's first pick: the file as it is when the measured connection has plenty of room for it, else the adaptive
  // stream (which then follows the connection by itself). The user's preference changes that: High tries the file as it
  // is first, whatever was measured (the server sends the adaptive stream if it would need converting anyway, and the
  // checks below step down if the connection can't keep up); Medium and Low stay on the adaptive stream, below their
  // ceiling.
  async autoStart() {
    const original = this.qualities.find(q => q.id === 'original');
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
    const levels = this.qualities.filter(q => q.id !== 'original' && (!this.autoCap || +q.id <= +this.autoCap));
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
    await this.restart(this.position(), { keepPaused: true });
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
        this.switchTo('auto', 'Slower connection: adapting to it');
      }
      return;
    }
    const top = this.hls && this.hls.levels.length && this.hls.currentLevel === this.topLevel();
    // Back up to the file as it is: never under a Medium or Low ceiling; sooner for High (best first)
    if (!this.underCeiling() && original && original.kbps > 0 && this.session.originalMode !== 'Transcode' && top &&
        est > original.kbps * 1.6 && ahead > 20 && this.stalls.length === 0 && sinceSwitch > (this.preference === 'high' ? 30000 : 120000)) {
      this.lastSwitch = now;
      this.switchTo('original', 'Faster connection: the file as it is');
    }
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

  attach(s) {
    this.detach();
    // The file as it is, or an adaptive stream played by the browser itself (Safari), starts at seekTo once loaded
    const native = url => {
      this.video.src = url;
      if (s.seekTo > 0) this.video.addEventListener('loadedmetadata', () => { this.video.currentTime = s.seekTo; }, { once: true });
    };
    if (s.mode === 'Direct') {
      native(s.url);
    } else if (this.airplayCapable && this.video.canPlayType('application/vnd.apple.mpegurl')) {
      // Safari: its own HLS player, not hls.js. AirPlay can only hand a TV a real stream URL (which it then fetches
      // itself, as a Chromecast does), not media fed through Media Source Extensions
      native(s.url);
    } else if (window.Hls?.isSupported()) {
      const Hls = window.Hls;
      const est = this.estimate();
      this.hls = new Hls({
        startPosition: s.adaptive ? (s.seekTo || 0) : 0, maxBufferLength: 60, backBufferLength: 90,
        // Its bandwidth estimate starts from the last one measured here, so the first level fits the connection
        ...(est ? { abrEwmaDefaultEstimate: est * 1000 } : {}),
      });
      this.hls.on(Hls.Events.LEVEL_SWITCHED, (_, data) => {
        const name = this.hls?.levels[data.level]?.name;
        this.playingLevel = s.levels?.find(l => l.id === name) ?? null;
        this.showQuality();
      });
      this.hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return;
        // Past the last segment there is (a file a little shorter than it said): that's the end
        if (s.adaptive && this.position() > this.duration() - 12) { this.onEnded(); return; }
        // The stream went away (stopped as idle, the server restarted): a new one from here, once
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR && !this.recovering) {
          this.recovering = true;
          this.restart(this.position(), { keepPaused: true });
          return;
        }
        this.status.textContent = 'Playback error: ' + (data.details ?? data.type);
      });
      this.hls.loadSource(s.url);
      this.hls.attachMedia(this.video);
    } else if (this.video.canPlayType('application/vnd.apple.mpegurl')) {
      native(s.url);   // Safari plays HLS itself
    } else {
      this.status.textContent = 'This browser can\'t play HLS.';
    }
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

  pickSubtitle(id) {
    const before = this.subtitle;
    this.subtitle = id || null;
    const wasBurned = before && !this.details.subtitles.find(s => s.id === before)?.text;
    const isBurned = this.subtitle && !this.details.subtitles.find(s => s.id === this.subtitle)?.text;
    if (wasBurned || isBurned) this.restart(this.position(), { keepPaused: true });   // image subtitles are part of the video
    else this.setTextTrack();
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

  // A new stream from `at` (where we are, by default) with the current audio, subtitles and quality
  async restart(at = this.position(), { keepPaused = false } = {}) {
    const wasPaused = this.video.paused;
    const old = this.session?.sessionId;
    this.session = null;
    if (old) api(`/api/play/${old}`, { method: 'DELETE' }).catch(() => {});
    try { await this.start(at, { autoplay: !(keepPaused && wasPaused) }); }
    catch (e) { this.status.textContent = e.message; }
  }

  togglePlay() {
    if (!this.session) { this.resume(); return; }   // after Stop: start again where it stopped
    this.video.paused ? this.video.play() : this.video.pause();
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
    this.stoppedAt = this.position();
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

  onTime() {
    const pos = this.position();
    if (!this.dragging) { this.seekBar.value = Math.floor(pos); this.showTime(pos); }
    if (!this.video.paused && Date.now() - this.lastSaved > 10000) this.saveProgress();
  }

  // How long this plays: an adaptive stream says (it counts from the first frame), else what the scan found
  duration() { return this.session?.adaptive && this.session.duration > 0 ? this.session.duration : this.details.summary.duration; }

  showTime(pos) {
    this.time.textContent = `${formatTime(pos)} / ${formatTime(this.duration())}`;
  }

  saveProgress(leaving = false, finished = undefined) {
    if (!this.session) return;
    this.lastSaved = Date.now();
    api('/api/progress', { method: 'POST', keepalive: leaving,
      body: { fileId: this.fileId, position: this.position(), finished } }).catch(() => {});
  }

  onEnded() {
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
      if (this.session && this.session.videoCodec !== 'h264' && this.session.mode !== 'Direct')
        await this.restart(this.position(), { keepPaused: true });
    } else if (!wireless && !isCasting()) {
      this.forCast = false;
    }
  }

  // Hands the stream to the Chromecast: it fetches the same session URL itself. Most Chromecasts can't decode
  // HEVC, so a copied x265 stream is restarted as H.264 first.
  async castHere() {
    if (!this.session) return;
    if (!this.forCast && this.session.videoCodec !== 'h264' && this.session.mode !== 'Direct') {
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
    this.saveProgress(true);
    clearInterval(this.monitor);
    clearTimeout(this.idleTimer);
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
