import { api } from './api.js';
import { h, formatTime } from './dom.js';
import { castAvailable, castMedia, onCastChange, isCasting } from './cast.js';

// Plays one file. The server picks how (Direct: the file as it is; Remux/Transcode: an HLS stream that starts
// at `startSeconds` into the file), and the controls here run on the file's clock either way:
// file time = session.startSeconds + video.currentTime.
//
// Quality is one stream at a time: picking another (or Auto deciding to) restarts the stream at the same spot.

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

function remember(key, value) { try { localStorage.setItem(key, value); } catch { } }
function recall(key) { try { return localStorage.getItem(key); } catch { return null; } }

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
    this.stalls = [];
    this.lastSwitch = 0;
    this.slowChecks = 0;

    this.video = h('video', { playsinline: true, preload: 'auto' });
    this.status = h('div', { class: 'spinner' }, 'Starting…');
    this.playBtn = h('button', { onclick: () => this.togglePlay() }, 'Play');
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
    this.video.addEventListener('play', () => { this.playBtn.textContent = 'Pause'; });
    this.video.addEventListener('pause', () => { this.playBtn.textContent = 'Play'; this.saveProgress(); });
    this.video.addEventListener('waiting', () => {
      this.status.textContent = 'Loading…';
      // A stall while playing (not right after starting or seeking) is what Auto steps down for
      if (this.playingSince && performance.now() - this.playingSince > 4000 && !this.video.seeking) this.stalls.push(performance.now());
    });
    this.video.addEventListener('playing', () => { this.status.textContent = ''; this.playingSince ??= performance.now(); });
    this.video.addEventListener('ended', () => this.onEnded());
    this.video.addEventListener('error', () => { if (this.session) this.status.textContent = 'This video can\'t be played here.'; });

    this.onPageHide = () => this.saveProgress(true);
    addEventListener('pagehide', this.onPageHide);
    this.monitor = setInterval(() => this.checkQuality(), 2000);

    castAvailable().then(ok => {
      if (!ok) return;
      this.castSlot.hidden = false;
      this.castSlot.append(document.createElement('google-cast-launcher'));
      this.offCast = onCastChange(casting => { if (casting) this.castHere(); else this.forCast = false; });
    });
  }

  element() {
    return h('div', {},
      h('div', { class: 'player' }, this.video, this.status),
      h('div', { class: 'controls' },
        this.seekBar,
        h('div', { class: 'row' },
          this.playBtn, this.time,
          this.qualitySelect,
          this.details.audio.length > 1 ? this.audioSelect : null,
          this.details.subtitles.length > 0 ? this.subSelect : null,
          this.castSlot,
          h('button', { class: 'secondary', onclick: () => this.fullscreen() }, 'Full screen'),
          this.info,
          this.next)));
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
      quality: this.quality, hevc: caps.hevc, hevc10: caps.hevc10 } });
    this.session = s;
    this.quality = s.quality;   // the server may have lowered it to the user's cap
    this.playingSince = null;
    this.stalls = [];
    this.slowChecks = 0;
    this.showQuality();
    this.attach(s);
    this.setTextTrack();
    if (autoplay) { try { await this.video.play(); } catch { /* autoplay blocked: the Play button still works */ } }
    if (isCasting() && !this.forCast) this.castHere();
  }

  // ── Quality ──────────────────────────────────────────────────────────────

  levelIndex(id) { return this.qualities.findIndex(q => q.id === id); }

  // Auto's first pick: the best quality that fits the measured connection with room to spare
  async autoStart() {
    let kbps = +recall(SPEED_KEY) || 0;
    if (!kbps) kbps = await this.speedTest();
    if (kbps) remember(SPEED_KEY, String(Math.round(kbps)));
    const fits = this.qualities.find(q => q.kbps > 0 && q.kbps * 1.4 <= kbps);
    return (fits ?? this.qualities[this.qualities.length - 1])?.id ?? 'original';
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
    if (choice === 'auto') { this.showQuality(); return; }   // keep the current stream; Auto takes over from here
    if (choice !== this.quality) this.switchTo(choice);
  }

  async switchTo(id, why) {
    this.quality = id;
    this.lastSwitch = performance.now();
    if (why) this.info.textContent = why;
    await this.restart(this.position(), { keepPaused: true });
  }

  showQuality() {
    const label = this.qualities.find(q => q.id === this.quality)?.label?.replace(/ \(.*\)$/, '') ?? this.quality;
    this.autoOption.textContent = this.choice === 'auto' && label ? `Auto (${label})` : 'Auto';
    if (this.session) this.info.textContent = `${this.session.mode}${this.session.mode === 'Transcode' ? '' : ' · ' + (this.session.videoCodec ?? '')}`;
  }

  // Every 2 s: step down when the connection can't keep up, back up after a long stretch with room to spare
  checkQuality() {
    if (this.choice !== 'auto' || !this.session || this.video.paused || this.forCast) return;
    const now = performance.now();
    this.stalls = this.stalls.filter(t => now - t < 30000);
    const est = this.hls?.bandwidthEstimate ? this.hls.bandwidthEstimate / 1000 : 0;
    if (est) remember(SPEED_KEY, String(Math.round(est)));
    const ahead = this.bufferAhead();
    const i = this.levelIndex(this.quality);
    const need = this.session.streamKbps || this.qualities[i]?.kbps || 0;
    const sinceSwitch = now - this.lastSwitch;

    const slow = est > 0 && need > 0 && est < need * 1.1 && ahead < 6;
    this.slowChecks = slow ? this.slowChecks + 1 : 0;
    if ((this.stalls.length >= 2 || this.slowChecks >= 3) && sinceSwitch > 15000 && i < this.qualities.length - 1) {
      this.switchTo(this.qualities[i + 1].id, `Slower connection: ${this.qualities[i + 1].label}`);
      return;
    }
    const better = this.qualities[i - 1];
    if (better && better.kbps > 0 && est > better.kbps * 1.6 && ahead > 15 && sinceSwitch > 60000 &&
        this.stalls.length === 0) {
      this.switchTo(better.id, `Faster connection: ${better.label}`);
    }
  }

  bufferAhead() {
    const t = this.video.currentTime, b = this.video.buffered;
    for (let i = 0; i < b.length; i++) if (b.start(i) <= t + 0.5 && b.end(i) >= t) return b.end(i) - t;
    return 0;
  }

  // ── Playback ─────────────────────────────────────────────────────────────

  attach(s) {
    this.detach();
    if (s.mode === 'Direct') {
      this.video.src = s.url;
      if (s.seekTo > 0) this.video.addEventListener('loadedmetadata', () => { this.video.currentTime = s.seekTo; }, { once: true });
    } else if (window.Hls?.isSupported()) {
      this.hls = new window.Hls({ startPosition: 0, maxBufferLength: 60, backBufferLength: 90 });
      this.hls.on(window.Hls.Events.ERROR, (_, data) => {
        if (data.fatal) this.status.textContent = 'Playback error: ' + (data.details ?? data.type);
      });
      this.hls.loadSource(s.url);
      this.hls.attachMedia(this.video);
    } else if (this.video.canPlayType('application/vnd.apple.mpegurl')) {
      this.video.src = s.url;   // Safari plays HLS itself
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

  togglePlay() { this.video.paused ? this.video.play() : this.video.pause(); }

  fullscreen() {
    const el = this.video.parentElement;
    if (document.fullscreenElement) document.exitFullscreen();
    else el.requestFullscreen?.();
  }

  onTime() {
    const pos = this.position();
    if (!this.dragging) { this.seekBar.value = Math.floor(pos); this.showTime(pos); }
    if (!this.video.paused && Date.now() - this.lastSaved > 10000) this.saveProgress();
  }

  showTime(pos) {
    this.time.textContent = `${formatTime(pos)} / ${formatTime(this.details.summary.duration)}`;
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
    if (this.details.next) {
      this.next.replaceChildren(h('a', { href: `#/play/${this.details.next}` }, 'Next episode →'));
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
    removeEventListener('pagehide', this.onPageHide);
    this.offCast?.();
    const id = this.session?.sessionId;
    this.detach();
    if (id && !isCasting()) api(`/api/play/${id}`, { method: 'DELETE', keepalive: true }).catch(() => {});
  }
}
