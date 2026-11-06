# Linked devices (TV and console apps)

An app on a device without a browser (a TV, a games console) signs in with a code, as TV apps usually do: it shows a
short code, someone signed in to MediaRiffle in a browser enters it at **`/link`** (or Profile → Linked devices → Link a
device), and the app gets its own token. This page is for whoever writes such an app.

A linked device can browse the libraries, play, and save where it stopped, as its user. It can't open Admin (even an
admin's device), change the account, or link other devices. Each one is listed under **Profile → Linked devices**,
where it can be unlinked. A device is also unlinked when its user's password changes or is reset, when the account is
removed, and after 180 days unused.

## Connections

Over HTTPS, from anywhere. Plain HTTP only from the local network (private, loopback and link-local addresses), for
devices that can't do TLS themselves and reach MediaRiffle directly on its LAN address (e.g.
`http://media01.example.lan:5080`), not through the proxy. Behind a reverse proxy, `TrustedProxies` must be right
([reverse-proxy.md](reverse-proxy.md)), or every visitor looks local to MediaRiffle.

## Linking

1. Ask for a code. No cookie or antiforgery token; `name` is what the device is called on the page and in the list.

   ```http
   POST /api/devices/code
   Content-Type: application/json

   { "name": "Xbox 360" }
   ```

   ```json
   { "deviceCode": "aKtxsbHY…", "userCode": "VKSZ-TX5K", "link": "/link", "expiresIn": 600, "interval": 5 }
   ```

   Show `userCode` and where to enter it (the server's address + `link`). Keep `deviceCode` secret.

2. Every `interval` seconds, ask whether it was entered:

   ```http
   POST /api/devices/token
   Content-Type: application/json

   { "deviceCode": "aKtxsbHY…" }
   ```

   | Answer | Meaning |
   | --- | --- |
   | `202` `{ "status": "waiting" }` | Not entered yet; ask again after `interval` seconds. |
   | `200` `{ "token": "…", "user": "ann", "device": "Xbox 360" }` | Linked. The token comes only this once: store it. |
   | `410` | The code ran out (`expiresIn`), or was already used: start again at 1. |
   | `429` | Asking too often. |

Codes wait in memory, so a restart of MediaRiffle means starting again on the device.

## Using the token

Send it on every request:

```http
Authorization: Bearer <token>
```

No cookie and no antiforgery token are needed (or used: a request with a token is signed in by the token alone). A
`401` means the token no longer works (unlinked); link again. A `403` means devices can't use that endpoint.

| Endpoint | What for |
| --- | --- |
| `GET /api/device` | Who the device is signed in as: `{ "user", "device", "maxQuality" }` |
| `DELETE /api/device` | Sign out: unlinks this device |
| `GET /api/libraries`, `/api/libraries/{id}/shows`, `/movies`, `/artists` | Browsing |
| `GET /api/shows/{id}`, `/api/movies/{id}`, `/api/files/{id}`, `/api/tags`, `/api/tags/{tag}`, `/api/queue` | Details, categories |
| `GET /api/lists`, `/api/lists/{tag}` | The user's lists (changing them needs a browser) |
| `GET /api/continue`, `DELETE /api/continue/{fileId}` | Continue watching |
| `GET /api/images/show/{id}`, `/movie/{id}`, `/file/{id}`, `/album/{id}`, `/artist/{id}` | Pictures |
| `GET /api/artists/{id}`, `/api/albums/{id}`, `/api/tracks/{id}`, `/api/tracks/{id}/audio` | Music |
| `POST /api/play`, `DELETE /api/play/{sessionId}`, `POST /api/progress` | Playing, and saving where it stopped |

## Playing

`POST /api/play` with the file and what the device can decode:

```json
{ "fileId": 123, "burn": false, "quality": "720", "hevc": false }
```

`quality` is `"auto"` (an adaptive stream), `"original"`, or a level: `"1080"`, `"720"`, `"480"`, `"360"`. The user's
own cap (Admin → Users) still applies. Leave out `start` to resume where they stopped.

The answer's `url` is the stream, relative to the server: an HLS playlist (H.264 in MPEG-TS segments) or, in `Direct`
mode, the file itself (`contentType` says which). Stream URLs carry their own key, so a player can fetch them without
the token. Save progress every so often with `POST /api/progress` `{ "fileId": 123, "position": 754.2 }`, and stop the
stream with `DELETE /api/play/{sessionId}` when leaving it.
