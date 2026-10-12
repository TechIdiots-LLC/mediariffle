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
