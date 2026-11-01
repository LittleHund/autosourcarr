# Authelia in front of AutoSourcarr

Everything stays behind Authelia except two paths that phones use without a browser
sign-in:

| Path | Who calls it | Protection |
|---|---|---|
| `/api/reader/v1/...` | the AutoSourcarr reader extension (Mihon, Tachiyomi forks, Suwayomi) | AutoSourcarr's reader key (`X-AutoSourcarr-API-Key`), read-only GET/HEAD |
| `/api/reader/repo/...` | the reader app adding and updating the AutoSourcarr extension repository | none needed: the prebuilt extension, its icon and index only (no address, key or catalogue data) |

Put these rules **before** your normal rule for the AutoSourcarr host. The first rule
is the exact reader rule; do not widen it.

```yaml
access_control:
  default_policy: deny
  rules:
    # 1. Reader API: AutoSourcarr checks the reader key itself.
    - domain: autosourcarr.example.com
      resources:
        - '^/api/reader/v1([/?].*)?$'
      policy: bypass
    # 2. Reader extension repository (public files, GET/HEAD only).
    - domain: autosourcarr.example.com
      resources:
        - '^/api/reader/repo/(index\.min\.json|repo\.json|apk/[A-Za-z0-9._-]+\.apk|icon/[A-Za-z0-9._-]+\.png|jar/[A-Za-z0-9._-]+\.jar)$'
      methods:
        - GET
        - HEAD
      policy: bypass
    # 3. Everything else (the web UI, /api/v1, /api/admin): signed in.
    - domain: autosourcarr.example.com
      policy: two_factor
```

Never bypass `/api/v1/`, `/api/admin/`, `/`, `/api/reader/` as a whole or the whole
hostname. The reader API accepts only GET and HEAD and answers 401 without a valid key.

## Checking it

```sh
# Reader API without a key: AutoSourcarr's own 401 (JSON), not an Authelia sign-in page.
curl -si https://autosourcarr.example.com/api/reader/v1/sources | head -n 1
# Reader API with a device key from System -> Keys: 200.
curl -s -H 'X-AutoSourcarr-API-Key: <device key>' https://autosourcarr.example.com/api/reader/v1/sources | head -c 200
# Repository: 200 JSON array.
curl -s https://autosourcarr.example.com/api/reader/repo/index.min.json | head -c 200
# Anything else: redirected to Authelia.
curl -si https://autosourcarr.example.com/api/v1/settings | head -n 1
```

If the reader shows "intercepted by Authelia", rule 1 is missing or comes after the
two_factor rule.

## Several AutoSourcarr servers

Each server has its own hostname, its own two bypass rules and its own device keys.
In the reader, server 1 goes into "AutoSourcarr" settings, server 2 into
"AutoSourcarr (2)" and so on (System -> Reader setup shows a QR code per slot).
