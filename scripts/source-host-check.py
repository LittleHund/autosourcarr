#!/usr/bin/env python3
"""End-to-end check of a running source host against real Keiyoushi extensions (phase 4 "Done when").

For every package: install the APK named by the repository index (index.pb), then search (or list popular),
open the first title (details + chapters), list the pages of its first chapter and download the first image.
Prints one JSON line per package and a summary; exits 1 when fewer than --require packages pass.
Standard library only.
"""
import argparse, gzip, json, sys, time, urllib.error, urllib.parse, urllib.request

def varint(b, i):
    result = shift = 0
    while True:
        c = b[i]; i += 1; result |= (c & 0x7F) << shift; shift += 7
        if c < 0x80: return result, i

def fields(b):
    i = 0
    while i < len(b):
        key, i = varint(b, i); field, wire = key >> 3, key & 7
        if wire == 0: value, i = varint(b, i)
        elif wire == 2:
            length, i = varint(b, i); value = b[i:i + length]; i += length
        elif wire == 1: value = b[i:i + 8]; i += 8
        elif wire == 5: value = b[i:i + 4]; i += 4
        else: raise ValueError("unknown wire type %d" % wire)
        yield field, value

def repository(url):
    data = urllib.request.urlopen(url, timeout=60).read()
    if data[:2] == b"\x1f\x8b": data = gzip.decompress(data)
    out = {}
    for field, value in fields(data):
        if field != 101: continue
        for row_field, row in fields(value):
            if row_field != 1: continue
            item = {"sources": []}
            for f, v in fields(row):
                if f == 1: item["name"] = v.decode()
                elif f == 2: item["pkg"] = v.decode()
                elif f == 3:
                    for uf, uv in fields(v):
                        if uf == 1: item["apk"] = uv.decode()
                elif f == 6: item["version"] = v.decode()
                elif f == 8:
                    source = {}
                    for sf, sv in fields(v):
                        if sf == 1: source["id"] = str(sv)
                        elif sf == 2: source["name"] = sv.decode()
                        elif sf == 3: source["lang"] = sv.decode()
                    item["sources"].append(source)
            out[item.get("pkg")] = item
    return out

def call(host, method, path, body=None, raw=False, timeout=180):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(host + path, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read()
            return (payload, response.headers.get("Content-Type", "")) if raw else json.loads(payload)
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:500]
        raise RuntimeError("HTTP %d %s %s: %s" % (error.code, method, path, detail))

def check(host, item, query, lang):
    started = time.time(); step = "install"; result = {"pkg": item["pkg"], "version": item.get("version")}
    try:
        installed = call(host, "POST", "/v1/extensions/install-url", {"apk_url": item["apk"]}, timeout=300)
        result["lib"] = installed.get("libVersion")
        if installed.get("loadError"): raise RuntimeError("load error: " + installed["loadError"])
        mine = installed["source"]["nodes"]
        source = next((s for s in mine if s["lang"] == lang), None) or (mine[0] if mine else None)
        if source is None: raise RuntimeError("no source loaded")
        result["source"] = "%s (%s)" % (source["name"], source["lang"])
        sid = source["id"]
        if item.get("preferences"):
            step = "preferences"
            result["preferences_set"] = configure(host, sid, item["preferences"])
            if len(result["preferences_set"]) < len(item["preferences"]): raise RuntimeError("preferences not found: %s" % result["preferences_set"])
        step = "search"
        page = {"mangas": []} if item.get("browse") else call(host, "GET", "/v1/sources/%s/mangas?type=SEARCH&page=1&query=%s" % (sid, urllib.parse.quote(query)))
        if not page["mangas"]:
            step = "popular"; page = call(host, "GET", "/v1/sources/%s/mangas?type=POPULAR&page=1" % sid)
        if not page["mangas"]: raise RuntimeError("no titles found")
        result["titles"] = len(page["mangas"])
        step = "details"
        pages = []; seen_chapters = False
        # First titles in order; per title the oldest, newest and middle chapter. A chapter without pages (e.g. a
        # Komga book in a format without page images) moves on to the next.
        for candidate in page["mangas"][:item.get("titles", 5)]:
            step = "details"
            detail = call(host, "POST", "/v1/mangas/%d/fetch" % candidate["id"], {"fetchManga": True, "fetchChapters": True})
            chapters = detail["chapters"]
            if not chapters: continue
            seen_chapters = True; result["title"] = detail["manga"]["title"]; result["chapters"] = len(chapters)
            step = "pages"
            for chapter in [chapters[-1], chapters[0], chapters[len(chapters) // 2]][:min(3, len(chapters))]:
                pages = call(host, "POST", "/v1/chapters/%d/pages" % chapter["id"])["pages"]
                if pages: break
            if pages: break
        if not seen_chapters: raise RuntimeError("no chapters in the first titles")
        if not pages: raise RuntimeError("no chapter with pages in the first titles")
        result["pages"] = len(pages)
        step = "image"
        image, kind = call(host, "GET", pages[0], raw=True)
        if len(image) < 512: raise RuntimeError("first image is only %d bytes (%s)" % (len(image), kind))
        result["image_bytes"] = len(image); result["image_type"] = kind
        result["ok"] = True
    except Exception as error:  # report and continue with the next package
        result["ok"] = False; result["failed_at"] = step; result["error"] = str(error)[:3000]
    result["seconds"] = round(time.time() - started, 1)
    return result

LIB14_REPO = "https://raw.githubusercontent.com/tachiyomiorg/extensions/repo/"

def lib14_item(address, username, password):
    """Komga from the archived Tachiyomi repository: built with extensions-lib 1.4 (version 1.4.x), checked against a
    Komga server (in CI: scripts/komga-fixture.sh, one CBZ comic)."""
    rows = json.loads(urllib.request.urlopen(LIB14_REPO + "index.min.json", timeout=60).read())
    row = next(r for r in rows if r["pkg"] == "eu.kanade.tachiyomi.extension.all.komga")
    if not str(row["version"]).startswith("1.4."): raise RuntimeError("Komga in the archive is %s, not lib 1.4" % row["version"])
    return {"pkg": row["pkg"], "apk": LIB14_REPO + "apk/" + row["apk"], "version": row["version"], "sources": [], "lib14": True,
            "preferences": {"address": address, "username": username, "password": password}, "browse": True, "titles": 30}

def configure(host, sid, values):
    prefs = call(host, "GET", "/v1/sources/%s/preferences" % sid)
    done = []
    for pref in prefs:
        label = ((pref.get("title") or "") + " " + (pref.get("key") or "")).lower()
        for name, value in values.items():
            if name in label and pref.get("valueType") == "String":
                call(host, "PUT", "/v1/sources/%s/preferences" % sid, {"position": pref["position"], "value": value}); done.append(name)
                break
    return done

def cloudflare(host, url):
    started = time.time()
    try:
        result = call(host, "POST", "/v1/webview/fetch", {"url": url}, timeout=240)
    except Exception as error:
        result = {"status": 0, "error": str(error)[:1000]}
    result["url"] = url; result["seconds"] = round(time.time() - started, 1)
    result["ok"] = bool(result.get("clearance")) and result.get("status") == 200 and not result.get("challenge_page")
    return result

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="http://127.0.0.1:4567")
    parser.add_argument("--index", default="https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.pb")
    parser.add_argument("--pkgs", required=True, help="comma-separated package names")
    parser.add_argument("--apk", action="append", default=[], help="extra pkg=url entries (e.g. an old 1.4 APK)")
    parser.add_argument("--query", default="love")
    parser.add_argument("--lang", default="en")
    parser.add_argument("--require", type=int, default=1)
    parser.add_argument("--lib14", action="store_true", help="also install and check a lib 1.4 APK from the repository history; it must pass")
    parser.add_argument("--cloudflare-url", action="append", default=[], help="a real page behind Cloudflare; reported, required only with --require-cloudflare")
    parser.add_argument("--require-cloudflare", action="store_true")
    parser.add_argument("--komga-url", default="http://127.0.0.1:25600")
    parser.add_argument("--komga-user", default="ci@example.org")
    parser.add_argument("--komga-password", default="ci-komga-pass")
    parser.add_argument("--challenge-url", default="", help="a local JavaScript challenge (scripts/fake-cloudflare.py); it must pass")
    args = parser.parse_args()
    index = repository(args.index)
    items = []
    for pkg in [p for p in args.pkgs.split(",") if p]:
        if pkg in index: items.append(index[pkg])
        else: print(json.dumps({"pkg": pkg, "ok": False, "failed_at": "index", "error": "not in the repository index"}))
    for extra in args.apk:
        pkg, url = extra.split("=", 1); items.append({"pkg": pkg, "apk": url, "sources": []})
    lib14_ok = None
    if args.lib14:
        try:
            items.append(lib14_item(args.komga_url, args.komga_user, args.komga_password))
        except Exception as error:
            lib14_ok = False
            print(json.dumps({"pkg": "lib 1.4", "ok": False, "failed_at": "find", "error": str(error)[:1000]}))
    passed = 0
    for item in items:
        result = check(args.host, item, args.query, args.lang)
        if item.get("lib14"):
            result["lib14"] = True; lib14_ok = result["ok"]
        else:
            passed += 1 if result["ok"] else 0
        print(json.dumps(result), flush=True)
    cloudflare_ok = None; challenge_ok = None
    if args.cloudflare_url or args.challenge_url:
        # The WebView downloads its Chromium build on first start: wait for it (up to 6 minutes).
        deadline = time.time() + 360; state = ""
        while time.time() < deadline:
            state = str(call(args.host, "GET", "/v1/health").get("webview", ""))
            if state == "ready" or state.startswith("failed") or state == "disabled": break
            time.sleep(5)
        print(json.dumps({"webview": state}), flush=True)
    for url in args.cloudflare_url:
        result = cloudflare(args.host, url)
        print(json.dumps({"cloudflare": True, **result}), flush=True)
        cloudflare_ok = bool(cloudflare_ok) or result["ok"]
    if args.challenge_url:
        result = cloudflare(args.host, args.challenge_url)
        try:
            body = urllib.request.urlopen(args.challenge_url.rstrip("/") + "/stats", timeout=10).read().decode()
            result["server"] = json.loads(body)
        except Exception as error:
            result["server"] = str(error)
        print(json.dumps({"challenge": True, **result}), flush=True)
        challenge_ok = result["ok"]
    print(json.dumps({"summary": True, "passed": passed, "total": len([i for i in items if not i.get("lib14")]),
                      "required": args.require, "lib14": lib14_ok, "cloudflare": cloudflare_ok, "challenge": challenge_ok}))
    ok = passed >= args.require and lib14_ok is not False and challenge_ok is not False
    if args.require_cloudflare: ok = ok and bool(cloudflare_ok)
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
