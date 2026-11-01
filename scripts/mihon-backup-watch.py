#!/usr/bin/env python3
"""Mihon backup-format release watch (PIPE-9).

Fetches the latest mihonapp/mihon release tag and the backup model files
(app/src/main/java/eu/kanade/tachiyomi/data/backup/models/*.kt) at that tag through the
GitHub API, extracts every @ProtoNumber field and compares them with
src/AutoSourcarr.Core/Imports/Backup/MihonBackup.proto.

Exit codes: 0 = every Kotlin field number is declared in the .proto; 1 = the model changed
(a readable diff is printed); 2 = the release or the model files could not be fetched/parsed.

Numbers the .proto declares but the latest release no longer writes are expected (older
backups still carry them) and are only listed for information.

Environment: GITHUB_TOKEN (optional, raises the API rate limit), MIHON_REPO (default
mihonapp/mihon), MIHON_REF (a tag/branch to compare instead of the latest release).
"""
from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

API = "https://api.github.com"
MODELS = "app/src/main/java/eu/kanade/tachiyomi/data/backup/models"
PROTO = Path(__file__).resolve().parent.parent / "src" / "AutoSourcarr.Core" / "Imports" / "Backup" / "MihonBackup.proto"

# Kotlin class -> .proto message where the names differ (the 0.17-0.19 repos and 0.20+ store share numbers).
ALIASES = {"BackupExtensionRepos": "BackupExtensionRepo", "BackupExtensionStore": "BackupExtensionRepo"}
# kotlinx polymorphic preference values: the .proto keeps BackupPreference.value as raw bytes on purpose.
IGNORED_SUFFIXES = ("PreferenceValue",)


def fetch(url: str, accept: str = "application/vnd.github+json") -> bytes:
    request = urllib.request.Request(url, headers={"Accept": accept, "User-Agent": "autosourcarr-mihon-backup-watch"})
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if token and url.startswith(API):
        request.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(request, timeout=30) as response:  # noqa: S310 - fixed https hosts
        return response.read()


def fetch_json(url: str):
    return json.loads(fetch(url).decode("utf-8"))


def matching_paren(text: str, start: int) -> int:
    depth = 0
    for index in range(start, len(text)):
        if text[index] == "(":
            depth += 1
        elif text[index] == ")":
            depth -= 1
            if depth == 0:
                return index
    return -1


CLASS = re.compile(r"\bclass\s+(\w+)\s*(?:@[\w.]+(?:\([^)]*\))?\s*)*(?:(?:private|internal|public)\s+)?(?:constructor\s*)?\(")
FIELD = re.compile(r"@ProtoNumber\(\s*(\d+)\s*\)\s*(?:@[\w.]+(?:\([^)]*\))?\s*)*(?:override\s+)?(?:val|var)\s+(\w+)\s*:\s*([^=,\n]+)")


def kotlin_fields(source: str) -> dict[str, dict[int, str]]:
    """{class name: {proto number: 'name: Type'}} for every class whose constructor carries @ProtoNumber."""
    classes: dict[str, dict[int, str]] = {}
    for match in CLASS.finditer(source):
        open_index = match.end() - 1
        close_index = matching_paren(source, open_index)
        if close_index < 0:
            continue
        body = source[open_index:close_index]
        fields = {int(number): f"{name}: {kind.strip()}" for number, name, kind in FIELD.findall(body)}
        if fields:
            classes.setdefault(match.group(1), {}).update(fields)
    return classes


def proto_fields(source: str) -> dict[str, dict[int, str]]:
    messages: dict[str, dict[int, str]] = {}
    for match in re.finditer(r"message\s+(\w+)\s*\{(.*?)\n\}", source, re.S):
        fields = {}
        for line in match.group(2).splitlines():
            line = line.split("//", 1)[0].strip()
            field = re.match(r"(?:optional\s+|repeated\s+)?([\w.]+)\s+(\w+)\s*=\s*(\d+)\s*;", line)
            if field:
                fields[int(field.group(3))] = f"{field.group(2)}: {field.group(1)}"
        messages[match.group(1)] = fields
    return messages


def main() -> int:
    repo = os.environ.get("MIHON_REPO", "mihonapp/mihon").strip()
    try:
        ref = os.environ.get("MIHON_REF", "").strip() or fetch_json(f"{API}/repos/{repo}/releases/latest")["tag_name"]
        listing = fetch_json(f"{API}/repos/{repo}/contents/{MODELS}?ref={ref}")
        if not isinstance(listing, list):
            raise ValueError("the backup model path is not a directory at " + ref)
        kotlin: dict[str, dict[int, str]] = {}
        files = [item for item in listing if str(item.get("name", "")).endswith(".kt") and item.get("download_url")]
        if not files:
            raise ValueError("no backup model files at " + ref)
        for item in files:
            for name, fields in kotlin_fields(fetch(item["download_url"], accept="text/plain").decode("utf-8")).items():
                kotlin.setdefault(name, {}).update(fields)
        proto = proto_fields(PROTO.read_text(encoding="utf-8"))
    except (urllib.error.URLError, OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"mihon-backup-watch: could not read the backup model ({error})", file=sys.stderr)
        return 2
    if not kotlin or not proto:
        print("mihon-backup-watch: no @ProtoNumber fields or no .proto messages were found; the parser needs attention", file=sys.stderr)
        return 2

    problems: list[str] = []
    retired: list[str] = []
    for name in sorted(kotlin):
        if name.endswith(IGNORED_SUFFIXES):
            continue
        message = ALIASES.get(name, name)
        declared = proto.get(message)
        if declared is None:
            problems.append(f"+ message {message} (Kotlin {name}) is not in MihonBackup.proto:")
            problems.extend(f"+     {number} = {field}" for number, field in sorted(kotlin[name].items()))
            continue
        missing = {number: field for number, field in kotlin[name].items() if number not in declared}
        if missing:
            problems.append(f"~ message {message} (Kotlin {name}) has new field numbers:")
            problems.extend(f"+     {number} = {field}" for number, field in sorted(missing.items()))
    used = {ALIASES.get(name, name) for name in kotlin}
    for message, fields in sorted(proto.items()):
        if message not in used:
            continue
        written = set().union(*(kotlin[name].keys() for name in kotlin if ALIASES.get(name, name) == message))
        gone = [f"{number} = {field}" for number, field in sorted(fields.items()) if number not in written]
        if gone:
            retired.append(f"  {message}: " + ", ".join(gone))

    print(f"mihon-backup-watch: {repo} {ref}: {sum(len(fields) for fields in kotlin.values())} @ProtoNumber fields in {len(kotlin)} classes")
    if retired:
        print("Declared in MihonBackup.proto but not written by this release (kept for older backups):")
        print("\n".join(retired))
    if problems:
        print("The Mihon backup model changed; update MihonBackup.proto, add a sample backup and extend MihonBackupReader.DetectVersion:")
        print("\n".join(problems))
        return 1
    print("MihonBackup.proto covers every field of this release.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
