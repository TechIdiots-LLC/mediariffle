// Chromecast, through Google's Cast sender and the Default Media Receiver. Loaded only when the browser can
// cast (Chrome, Edge), over HTTPS. The Chromecast fetches the stream URL itself, so it has to be able to reach
// the server at that address.

let ready = null;

export function castAvailable() {
  if (ready) return ready;
  ready = new Promise(resolve => {
    if (!window.chrome || !window.isSecureContext) return resolve(false);
    window.__onGCastApiAvailable = ok => {
      // "Available" with the framework missing happens (e.g. a headless or locked-down browser): no Cast then
      if (!ok || !window.cast?.framework || !window.chrome?.cast) return resolve(false);
      try {
        const ctx = cast.framework.CastContext.getInstance();
        ctx.setOptions({
          receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
          autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED,
        });
        resolve(true);
      } catch { resolve(false); }
    };
    const s = document.createElement('script');
    s.src = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';
    s.onerror = () => resolve(false);
    document.head.append(s);
  });
  return ready;
}

// The Cast button, once castAvailable() says yes. Ours, not the SDK's <google-cast-launcher>: that one hides itself
// until Chrome has found a Chromecast on the network, so it often isn't there at all and nobody knows why. This one
// always is, and opens Chrome's Cast dialog (which says when it finds none); while casting it reads "Casting".
export function castButton(className = 'secondary') {
  const ctx = cast.framework.CastContext.getInstance();
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = className + ' cast-button';
  btn.title = 'Cast to a Chromecast or Google TV';
  const type = cast.framework.CastContextEventType.CAST_STATE_CHANGED;
  const show = () => {
    // Taken off the page (a new player, the music bar redrawn): stop listening
    if (btn.dataset.shown && !btn.isConnected) { ctx.removeEventListener(type, show); return; }
    if (btn.isConnected) btn.dataset.shown = '1';
    const on = ctx.getCastState() === cast.framework.CastState.CONNECTED;
    btn.textContent = on ? 'Casting' : 'Cast';
    btn.classList.toggle('casting', on);
  };
  btn.addEventListener('click', () => ctx.requestSession().catch(() => { /* closed the dialog */ }));
  ctx.addEventListener(type, show);
  show();
  return btn;
}

export function isCasting() {
  try { return !!cast.framework.CastContext.getInstance().getCurrentSession(); }
  catch { return false; }
}

export function onCastChange(callback) {
  const ctx = cast.framework.CastContext.getInstance();
  const handler = e => {
    const S = cast.framework.SessionState;
    callback(e.sessionState === S.SESSION_STARTED || e.sessionState === S.SESSION_RESUMED);
  };
  ctx.addEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, handler);
  return () => ctx.removeEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, handler);
}

export function castMedia({ url, contentType, hls, fmp4 = false, title, currentTime, subtitleUrl }) {
  const session = cast.framework.CastContext.getInstance().getCurrentSession();
  if (!session) return;
  const info = new chrome.cast.media.MediaInfo(url, contentType);
  info.streamType = chrome.cast.media.StreamType.BUFFERED;
  if (hls) {
    info.hlsSegmentFormat = fmp4 ? chrome.cast.media.HlsSegmentFormat.FMP4 : chrome.cast.media.HlsSegmentFormat.TS;
    info.hlsVideoSegmentFormat = fmp4 ? chrome.cast.media.HlsVideoSegmentFormat.FMP4 : chrome.cast.media.HlsVideoSegmentFormat.MPEG2_TS;
  }
  info.metadata = new chrome.cast.media.GenericMediaMetadata();
  info.metadata.title = title;
  const request = new chrome.cast.media.LoadRequest(info);
  if (subtitleUrl) {
    const track = new chrome.cast.media.Track(1, chrome.cast.media.TrackType.TEXT);
    track.trackContentId = subtitleUrl;
    track.trackContentType = 'text/vtt';
    track.subtype = chrome.cast.media.TextTrackType.SUBTITLES;
    track.name = 'Subtitles';
    info.tracks = [track];
    request.activeTrackIds = [1];
  }
  request.currentTime = currentTime;
  request.autoplay = true;
  session.loadMedia(request).catch(e => console.warn('Cast failed', e));
}

// ── Music ────────────────────────────────────────────────────────────────────

export function castSession() {
  try { return cast.framework.CastContext.getInstance().getCurrentSession(); }
  catch { return null; }
}

// The Chromecast's player as the page sees it (time, paused, what's playing), and its controls; made once
let remote = null;
export function remotePlayer() {
  if (!remote) {
    const player = new cast.framework.RemotePlayer();
    remote = { player, controller: new cast.framework.RemotePlayerController(player) };
  }
  return remote;
}

/**
 * Hands the Chromecast a queue of tracks (from /api/cast/tracks), playing from `startIndex` at `startTime`. It moves
 * through them itself, so the music goes on with the phone asleep. Each item carries its track id (customData.id).
 */
export function castTracks(tracks, startIndex, startTime = 0) {
  const session = castSession();
  if (!session) return Promise.reject(new Error('Not casting'));
  const items = tracks.map((t, i) => {
    const info = new chrome.cast.media.MediaInfo(new URL(t.url, location.origin).href, t.contentType);
    info.streamType = chrome.cast.media.StreamType.BUFFERED;
    info.duration = t.duration || null;
    info.customData = { id: t.id };
    const meta = new chrome.cast.media.MusicTrackMediaMetadata();
    meta.title = t.title;
    meta.artist = t.artist ?? '';
    meta.albumName = t.album ?? '';
    if (t.track) meta.trackNumber = t.track;
    meta.images = [new chrome.cast.Image(new URL(t.cover, location.origin).href)];
    info.metadata = meta;
    const item = new chrome.cast.media.QueueItem(info);
    item.autoplay = true;
    item.preloadTime = 20;
    if (i === startIndex && startTime > 0) item.startTime = startTime;
    return item;
  });
  const request = new chrome.cast.media.QueueLoadRequest(items);
  request.startIndex = startIndex;
  request.repeatMode = chrome.cast.media.RepeatMode.OFF;
  return new Promise((resolve, reject) => session.getSessionObj().queueLoad(request, resolve, reject));
}

/** The track id the Chromecast is playing, or null. */
export function castTrackId() {
  return remote?.player.mediaInfo?.customData?.id ?? null;
}

/** Moves the Chromecast's queue to the item for this track id; false when it isn't in what it was given. */
export function castJumpTo(trackId) {
  const media = castSession()?.getMediaSession();
  const item = media?.items?.find(x => x.media?.customData?.id === trackId);
  if (!item) return false;
  media.queueJumpToItem(item.itemId, () => { }, e => console.warn('Cast jump failed', e));
  return true;
}

/** The Chromecast's next or previous item; false at the end of what it was given. */
export function castStep(forward) {
  const media = castSession()?.getMediaSession();
  if (!media?.items?.length) return false;
  const i = media.items.findIndex(x => x.itemId === media.currentItemId);
  if (i < 0 || (forward ? i + 1 >= media.items.length : i === 0)) return false;
  (forward ? media.queueNext : media.queuePrev).call(media, () => { }, e => console.warn('Cast step failed', e));
  return true;
}
