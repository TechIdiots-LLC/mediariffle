import { api } from './api.js';
import { h, formatTime } from './dom.js';
import { castAvailable, castMedia, onCastChange, isCasting } from './cast.js';

// Plays one file. The server picks how (Direct: the file as it is; Remux/Transcode: an HLS stream that starts
// at `startSeconds` into the file), and the controls here run on the file's clock either way:
// file time = session.startSeconds + video.currentTime.
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
    this.castSlot = h('span', { class: 'cast-btn', hidden: true });
    this.next = h('span');

    this.video.addEventListener('timeupdate', () => this.onTime());
    this.video.addEventListener('play', () => { this.playBtn.textContent = 'Pause'; });
    this.video.addEventListener('pause', () => { this.playBtn.textContent = 'Play'; this.saveProgress(); });
    this.video.addEventListener('waiting', () => { this.status.textContent = 'Loading…'; });
    this.video.addEventListener('playing', () => { this.status.textContent = ''; });
    this.video.addEventListener('ended', () => this.onEnded());
    this.video.addEventListener('error', () => { this.status.textContent = 'This video can\'t be played here.'; });

    this.onPageHide = () => this.saveProgress(true);
    addEventListener('pagehide', this.onPageHide);

    castAvailable().then(ok => {
      if (!ok) return;
      this.castSlot.hidden = false;
      this.castSlot.append(document.createElement('google-cast-launcher'));
      this.offCast = onCastChange(casting => { if (casting) this.castHere(); });
    });
  }

  element() {
    return h('div', {},
      h('div', { class: 'player' }, this.video, this.status),
      h('div', { class: 'controls' },
        this.seekBar,
        h('div', { class: 'row' },
          this.playBtn, this.time,
          this.details.audio.length > 1 ? this.audioSelect : null,
          this.details.subtitles.length > 0 ? this.subSelect : null,
          this.castSlot,
          h('button', { class: 'secondary', onclick: () => this.fullscreen() }, 'Full screen'),
          this.next)));
  }

  // Where in the file we are
  position() {
    return (this.session?.startSeconds ?? 0) + (this.video.currentTime || 0);
  }

  async start(startAt = null) {
    this.status.textContent = 'Starting…';
    const burn = this.subtitle && !this.details.subtitles.find(s => s.id === this.subtitle)?.text;
    const s = await api('/api/play', { method: 'POST', body: {
      fileId: this.fileId, audio: this.audio, subtitle: this.subtitle, burn: !!burn, start: startAt } });
    this.session = s;
    this.attach(s);
    this.setTextTrack();
    try { await this.video.play(); } catch { /* autoplay blocked: the Play button still works */ }
    if (isCasting()) this.castHere();
  }

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
    if (wasBurned || isBurned) this.restart();   // image subtitles are part of the video
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

  async restart(at = this.position()) {
    const old = this.session?.sessionId;
    this.session = null;
    if (old) api(`/api/play/${old}`, { method: 'DELETE' }).catch(() => {});
    try { await this.start(at); }
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

  // Hands the stream to the Chromecast: it fetches the same session URL itself
  castHere() {
    if (!this.session) return;
    const url = new URL(this.session.url, location.origin).href;
    const subtitle = this.session.subtitles?.find(x => x.id === this.subtitle);
    castMedia({
      url, contentType: this.session.contentType, hls: this.session.mode !== 'Direct',
      title: this.details.summary.title,
      currentTime: this.session.mode === 'Direct' ? this.position() : this.video.currentTime,
      subtitleUrl: subtitle ? new URL(subtitle.url, location.origin).href : null,
    });
    this.video.pause();
  }

  destroy() {
    this.saveProgress(true);
    removeEventListener('pagehide', this.onPageHide);
    this.offCast?.();
    const id = this.session?.sessionId;
    this.detach();
    if (id && !isCasting()) api(`/api/play/${id}`, { method: 'DELETE', keepalive: true }).catch(() => {});
  }
}
