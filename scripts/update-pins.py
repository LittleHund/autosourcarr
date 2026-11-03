#!/usr/bin/env python3
"""PIPE-3: refresh the pinned fingerprints of the 0.5.0 image build and show the diff for review.

  python3 scripts/update-pins.py             rewrite stale pins in place, then print `git diff` of the pinned files
  python3 scripts/update-pins.py --check     report stale pins only (exit 1 when one is stale, 0 when all are current)
  python3 scripts/update-pins.py --list      print every pin that was found, nothing is fetched

What is pinned:
  * every container image written as  <registry>/<repo>:<tag>@sha256:<digest>  in the files below: the tag is
    kept and the digest is set to what the registry serves for that tag today (the multi-arch index digest). A
    digest is the content address itself, so refreshing it is safe to automate; the diff is still reviewed.
  * every downloaded tool written as a pair of Dockerfile build arguments  ARG <NAME>_URL=<url>  and
    ARG <NAME>_SHA256=<hex> / ARG <NAME>_SHA512=<hex>. These hashes are pinned IN THE REPOSITORY and never rewritten
    here: the upstream value (the vendor's <url>.checksum line for exactly that file name, or a download with
    --download) is only REPORTED as "UPSTREAM-CHANGED" for a person to verify independently and edit by hand. A
    checksum served by the same host as the file is not an independent proof.

Not refreshed here, on purpose: git commit pins (Suwayomi v2.4.2366 in scripts/build-source-host.sh, Keiyoushi in
extension/keiyoushi-default.lock) and package versions (package-lock.json, Directory.Packages.props + lock files).
Those change behaviour and are updated by hand with their own tests. OS packages are not pinned: the image installs
the current, signed Ubuntu packages at build time (PIPE-1/PIPE-2 "fresh OS packages"); the build records the list in
/opt/autosourcarr-image-packages.txt.

Owner note: the release workflow's scheduled run of this script stays disabled (the owner does not want extra GitHub
runs or notification mail); run it by hand before a release. Only the Python standard library is used.

Registry tokens are fetched only from the realm a registry names when that realm is https and belongs to the
registry itself (or Docker Hub's auth.docker.io); anything else is refused.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PINNED_FILES = [
    ROOT / "docker" / "Dockerfile",
    ROOT / ".github" / "workflows" / "release.yml",
    ROOT / "docker-compose.0.5.0.yml",
]
IMAGE = re.compile(r"(?P<ref>(?P<registry>[a-z0-9.-]+\.[a-z]{2,}(?::\d+)?/)?(?P<repo>[a-z0-9._/-]+)):(?P<tag>[A-Za-z0-9._-]+)@sha256:(?P<digest>[0-9a-f]{64})")
TOOL_URL = re.compile(r"^ARG (?P<name>[A-Z0-9_]+)_URL=(?P<url>\S+)\s*$", re.M)
TOOL_HASH = re.compile(r"^ARG (?P<name>[A-Z0-9_]+)_(?P<algo>SHA256|SHA512)=(?P<hex>[0-9a-f]+)\s*$", re.M)
ACCEPT = ", ".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])
USER_AGENT = "autosourcarr-update-pins/1"


def registry_of(match: re.Match) -> tuple[str, str]:
    registry = (match.group("registry") or "").rstrip("/")
    repo = match.group("repo")
    if not registry or registry == "docker.io":
        registry = "registry-1.docker.io"
        if "/" not in repo:
            repo = "library/" + repo
    return registry, repo


def _request(url: str, method: str = "GET", headers: dict | None = None):
    request = urllib.request.Request(url, method=method, headers={"User-Agent": USER_AGENT, **(headers or {})})
    return urllib.request.urlopen(request, timeout=60)


# Token services a registry may send us to (its own host, or Docker Hub's separate auth host).
TOKEN_HOSTS = {"registry-1.docker.io": {"auth.docker.io"}}


def _bearer(challenge: str, repo: str, registry: str) -> str:
    fields = dict(re.findall(r'(\w+)="([^"]*)"', challenge))
    realm = urllib.parse.urlsplit(fields.get("realm", ""))
    allowed = {registry} | TOKEN_HOSTS.get(registry, set())
    if realm.scheme != "https" or realm.hostname not in allowed or realm.username or realm.password:
        raise RuntimeError(f"{registry}: refused token realm {fields.get('realm', '')!r} (expected https on {sorted(allowed)})")
    query = {"service": fields.get("service", ""), "scope": fields.get("scope") or f"repository:{repo}:pull"}
    with _request(realm.geturl() + ("&" if realm.query else "?") + urllib.parse.urlencode(query)) as response:
        body = json.load(response)
    return body.get("token") or body.get("access_token") or ""


def image_digest(registry: str, repo: str, tag: str) -> str:
    url = f"https://{registry}/v2/{repo}/manifests/{tag}"
    headers = {"Accept": ACCEPT}
    for _ in range(2):
        try:
            with _request(url, "HEAD", headers) as response:
                digest = response.headers.get("Docker-Content-Digest", "")
                if not digest:  # a few registries answer HEAD without it; GET and hash the body then
                    with _request(url, "GET", headers) as full:
                        digest = "sha256:" + hashlib.sha256(full.read()).hexdigest()
                if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
                    raise RuntimeError(f"{registry}/{repo}:{tag}: unexpected digest {digest!r}")
                return digest.split(":", 1)[1]
        except urllib.error.HTTPError as error:
            challenge = error.headers.get("WWW-Authenticate", "")
            if error.code == 401 and challenge.lower().startswith("bearer") and "Authorization" not in headers:
                headers["Authorization"] = "Bearer " + _bearer(challenge, repo, registry)
                continue
            raise RuntimeError(f"{registry}/{repo}:{tag}: HTTP {error.code}") from error
    raise RuntimeError(f"{registry}/{repo}:{tag}: not authorised")


def tool_hash(url: str, algo: str, downloads: bool) -> str | None:
    """The upstream hash of one file: the <url>.checksum line naming exactly that file, else (with downloads) the hash
    of the downloaded file. Only reported, never written (see the module notes)."""
    size = 64 if algo == "SHA256" else 128
    name = urllib.parse.urlsplit(url).path.rsplit("/", 1)[-1]
    if urllib.parse.urlsplit(url).scheme != "https":
        raise RuntimeError(f"{url}: only https downloads are checked")
    try:
        with _request(url + ".checksum") as response:
            text = response.read(4096).decode("ascii", "replace")
        for line in text.splitlines():
            parts = line.split()
            # "<hex>  <file>" or "<hex> *<file>" (sha*sum format), for this file name only.
            if len(parts) == 2 and re.fullmatch(r"[0-9a-f]{%d}" % size, parts[0]) and parts[1].lstrip("*") == name:
                return parts[0]
    except (urllib.error.URLError, TimeoutError):
        pass
    if not downloads:
        return None
    digest = hashlib.new(algo.lower())
    with _request(url) as response:
        for chunk in iter(lambda: response.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--check", action="store_true", help="report stale pins, change nothing")
    parser.add_argument("--list", action="store_true", help="list the pins, fetch nothing")
    parser.add_argument("--download", action="store_true", help="download a tool to hash it when no checksum file names it")
    args = parser.parse_args()

    stale: list[str] = []
    upstream: list[str] = []
    errors: list[str] = []
    for path in PINNED_FILES:
        if not path.exists():
            continue
        text = original = path.read_text(encoding="utf-8")
        rel = path.relative_to(ROOT).as_posix()
        seen: dict[str, str] = {}
        for match in IMAGE.finditer(original):
            ref, tag, digest = match.group("ref"), match.group("tag"), match.group("digest")
            key = f"{ref}:{tag}"
            if args.list:
                print(f"{rel}: image {key}@sha256:{digest}")
                continue
            if key not in seen:
                try:
                    seen[key] = image_digest(*registry_of(match), tag)
                except (RuntimeError, urllib.error.URLError, TimeoutError) as error:
                    errors.append(f"{rel}: {key}: {error}")
                    continue
            current = seen[key]
            if current != digest:
                stale.append(f"{rel}: {key} {digest[:12]} -> {current[:12]}")
                text = text.replace(f"{key}@sha256:{digest}", f"{key}@sha256:{current}")
        urls = {m.group("name"): m.group("url") for m in TOOL_URL.finditer(original)}
        for match in TOOL_HASH.finditer(original):
            name, algo, old = match.group("name"), match.group("algo"), match.group("hex")
            url = urls.get(name)
            if args.list:
                print(f"{rel}: tool {name} {algo} {old[:16]}... {url or '(no URL argument)'}")
                continue
            if not url or "$" in url:
                errors.append(f"{rel}: {name}_{algo}: no literal {name}_URL argument to check it against")
                continue
            try:
                current = tool_hash(url, algo, args.download)
            except (RuntimeError, urllib.error.URLError, TimeoutError) as error:
                errors.append(f"{rel}: {name}: {error}")
                continue
            if current is None:
                errors.append(f"{rel}: {name}: no checksum file names {url.rsplit('/', 1)[-1]} (use --download to hash it)")
            elif current != old:
                # Never rewritten: a person verifies the new file independently and edits the pin by hand.
                upstream.append(f"{rel}: {name}_{algo} pinned {old[:16]}..., upstream now {current[:16]}... ({url})")
        if text != original and not args.check:
            path.write_text(text, encoding="utf-8", newline="\n")

    if args.list:
        return 0
    for line in errors:
        print("ERROR " + line, file=sys.stderr)
    for line in stale:
        print(("STALE " if args.check else "UPDATED ") + line)
    for line in upstream:
        print("UPSTREAM-CHANGED " + line + " -- verify independently, then edit the pin by hand")
    if not stale and not upstream:
        print("All pins are current." if not errors else "No stale pins found (see errors).")
    if stale and not args.check:
        # The diff is the review: nothing is committed or pushed by this script.
        subprocess.run(["git", "-C", str(ROOT), "--no-pager", "diff", "--", *[str(p) for p in PINNED_FILES if p.exists()]], check=False)
    if errors:
        return 2
    return 1 if ((stale and args.check) or upstream) else 0


if __name__ == "__main__":
    sys.exit(main())
