# Nginx Proxy Manager (with Authelia) in front of AutoSourcarr

Nginx Proxy Manager (NPM) forwards to AutoSourcarr; Authelia protects everything
except the reader API and the reader extension repository:

- `^/api/reader/v1([/?].*)?$`: the reader extension's API. AutoSourcarr requires its
  reader key (`X-AutoSourcarr-API-Key`) and allows only GET/HEAD.
- `^/api/reader/repo/...`: the public extension repository (index, APK, JAR, icon).

Use the matching Authelia rules from [authelia.md](authelia.md) as well.

## Proxy host

1. **Details**: domain `autosourcarr.example.com`, scheme `http`, forward host
   `autosourcarr` (the container name) and port `8282`. Turn on *Websockets support*
   (live updates and the source WebView use them) and *Block common exploits*.
2. **SSL**: a certificate for the domain, *Force SSL* and *HTTP/2* on.
3. **Advanced**: paste the block below (replace `authelia:9091` and the portal
   address with yours).

```nginx
# Authelia's verification endpoint (internal only).
location = /internal/authelia/authz {
    internal;
    proxy_pass http://authelia:9091/api/authz/auth-request;
    proxy_set_header X-Original-Method $request_method;
    proxy_set_header X-Original-URL $scheme://$http_host$request_uri;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header Content-Length "";
    proxy_set_header Connection "";
    proxy_pass_request_body off;
}

# 1. Reader API: no Authelia; AutoSourcarr checks the reader key.
location ~ ^/api/reader/v1([/?].*)?$ {
    limit_except GET HEAD { deny all; }
    proxy_pass http://autosourcarr:8282;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
}

# 2. Reader extension repository: public files only.
location ~ ^/api/reader/repo/(index\.min\.json|repo\.json|apk/[A-Za-z0-9._-]+\.apk|icon/[A-Za-z0-9._-]+\.png|jar/[A-Za-z0-9._-]+\.jar)$ {
    limit_except GET HEAD { deny all; }
    proxy_pass http://autosourcarr:8282;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}

# 3. Everything else: signed in through Authelia.
location / {
    auth_request /internal/authelia/authz;
    auth_request_set $redirection_url $upstream_http_location;
    error_page 401 =302 $redirection_url;
    auth_request_set $user $upstream_http_remote_user;
    auth_request_set $groups $upstream_http_remote_groups;
    proxy_set_header Remote-User $user;
    proxy_set_header Remote-Groups $groups;

    proxy_pass http://autosourcarr:8282;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $http_connection;
    proxy_buffering off;   # live updates (server-sent events)
}
```

With *external* sign-in in AutoSourcarr (AUTH_METHOD=external), AutoSourcarr reads
`Remote-User`/`Remote-Groups` only from the trusted proxy (AUTH_TRUSTED_PROXIES).

Do not add a custom location for `/api/v1`, `/api/admin` or `/api/reader/` as a whole
without Authelia. Check the result with the `curl` lines in [authelia.md](authelia.md).
