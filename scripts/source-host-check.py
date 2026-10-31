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
        step = "search"
        page = call(host, "GET", "/v1/sources/%s/mangas?type=SEARCH&page=1&query=%s" % (sid, urllib.parse.quote(query)))
        if not page["mangas"]:
            step = "popular"; page = call(host, "GET", "/v1/sources/%s/mangas?type=POPULAR&page=1" % sid)
        if not page["mangas"]: raise RuntimeError("no titles found")
        result["titles"] = len(page["mangas"])
        step = "details"
        chapters = []; manga = None
        for candidate in page["mangas"][:3]:
            detail = call(host, "POST", "/v1/mangas/%d/fetch" % candidate["id"], {"fetchManga": True, "fetchChapters": True})
            chapters = detail["chapters"]
            if chapters: manga = detail["manga"]; result["title"] = manga["title"]; break
        if not chapters: raise RuntimeError("no chapters in the first titles")
        result["chapters"] = len(chapters)
        step = "pages"
        pages = call(host, "POST", "/v1/chapters/%d/pages" % chapters[-1]["id"])["pages"]
        if not pages: raise RuntimeError("chapter has no pages")
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

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="http://127.0.0.1:4567")
    parser.add_argument("--index", default="https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.pb")
    parser.add_argument("--pkgs", required=True, help="comma-separated package names")
    parser.add_argument("--apk", action="append", default=[], help="extra pkg=url entries (e.g. an old 1.4 APK)")
    parser.add_argument("--query", default="love")
    parser.add_argument("--lang", default="en")
    parser.add_argument("--require", type=int, default=1)
    args = parser.parse_args()
    index = repository(args.index)
    items = []
    for pkg in [p for p in args.pkgs.split(",") if p]:
        if pkg in index: items.append(index[pkg])
        else: print(json.dumps({"pkg": pkg, "ok": False, "failed_at": "index", "error": "not in the repository index"}))
    for extra in args.apk:
        pkg, url = extra.split("=", 1); items.append({"pkg": pkg, "apk": url, "sources": []})
    passed = 0
    for item in items:
        result = check(args.host, item, args.query, args.lang)
        passed += 1 if result["ok"] else 0
        print(json.dumps(result), flush=True)
    print(json.dumps({"summary": True, "passed": passed, "total": len(items), "required": args.require}))
    sys.exit(0 if passed >= args.require else 1)

if __name__ == "__main__":
    main()
