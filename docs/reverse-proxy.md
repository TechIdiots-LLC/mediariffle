# Behind a reverse proxy

MediaRiffle listens on plain HTTP; the proxy in front handles HTTPS and the public name (`media.example.com` here).
Whatever the proxy, MediaRiffle needs to know:

- the proxy's address, in `TrustedProxies`: only that address may tell it the real client (`X-Forwarded-For`) and that
  the request came over HTTPS (`X-Forwarded-Proto`)
- how many proxies are in the chain, in `ForwardLimit`

If either is wrong, every visitor looks like the proxy. Sign-in rate limiting then throttles everyone at once, and the
app thinks requests are plain HTTP, so secure cookies and redirects misbehave.

Timeouts matter: starting a stream waits until its first segment is ready (up to 45 s), and a segment can take a few
seconds to encode. Allow at least 120 s.

## HAProxy directly to MediaRiffle (recommended)

MediaRiffle on `media01.example.lan:5080`, HAProxy on `10.0.0.5`. MediaRiffle's settings: `"TrustedProxies": [
"10.0.0.5" ]`, `"ForwardLimit": 1`, and Kestrel listening on the LAN address (see [install-ubuntu.md](install-ubuntu.md)).

```haproxy
frontend https
    bind :443 ssl crt /etc/haproxy/certs/
    mode http
    option forwardfor
    http-request set-header X-Forwarded-Proto https
    use_backend mediariffle if { req.hdr(host) -i media.example.com }
    # … your other sites

backend mediariffle
    mode http
    option httpchk GET /api/auth/state
    http-check expect status 200
    timeout server 120s
    server media01 media01.example.lan:5080 check
```

`option forwardfor` sets `X-Forwarded-For`, and the `set-header` line tells MediaRiffle the visitor used HTTPS. If
HAProxy already uses `option forwardfor` globally, don't add it twice.

## HAProxy → Apache → MediaRiffle

If MediaRiffle has to sit behind your existing Apache:
`"TrustedProxies": [ "10.0.0.5", "127.0.0.1" ]` (HAProxy's and Apache's addresses) and `"ForwardLimit": 2`.

```apache
<VirtualHost *:80>
    ServerName media.example.com
    ProxyPreserveHost On
    ProxyTimeout 120
    # HAProxy already set X-Forwarded-Proto; pass it on rather than overwrite it
    ProxyAddHeaders On
    ProxyPass        / http://127.0.0.1:5080/ retry=0
    ProxyPassReverse / http://127.0.0.1:5080/
</VirtualHost>
```

Enable the modules: `sudo a2enmod proxy proxy_http headers`.

## nginx

```nginx
server {
    listen 443 ssl;
    server_name media.example.com;
    # ssl_certificate …;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 120s;
        proxy_buffering off;
    }
}
```

## Chromecast

A Chromecast fetches the stream itself, from the same `https://media.example.com/stream/…` address, so it must be able
to reach that name. On the same LAN as the proxy that works when the name resolves inside the LAN (split DNS) or
hairpin NAT is set up. The Cast button only appears on HTTPS pages in Chrome and Edge.
