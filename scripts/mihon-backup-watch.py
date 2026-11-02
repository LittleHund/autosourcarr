#!/usr/bin/env python3
"""Mihon backup-format release watch (PIPE-9).

Fetches the latest mihonapp/mihon release tag and the backup model files
(app/src/main/java/eu/kanade/tachiyomi/data/backup/models/*.kt) at that tag through the
GitHub API, extracts every @ProtoNumber field and compares them with
src/AutoSourcarr.Core/Imports/Backup/MihonBackup.proto.

Numbers the .proto declares but the latest release no longer writes are expected (older
backups still carry them) and are only listed for information.

Release-to-release diff (phase 13): the last release whose backup model was reviewed is kept in
src/AutoSourcarr.Core/Imports/Backup/mihon-backup-watch.json. When the latest release differs, the model of both
releases is fetched and every added/removed class, added/removed field number and renamed/retyped field is
printed. A schema change that has not been reviewed yet exits 1 even when the .proto already covers it, so a
person checks MihonBackup.proto, adds a sample backup if needed and then records the release with --accept.

Sample coverage (--fixtures): every backup family MihonBackupReader.DetectVersion can report must have a sample
backup in tests/AutoSourcarr.Tests/Fixtures/backups/expected.json (the C# test "imports: every sample backup imports
end to end" imports each one). Missing samples, missing files and a latest release newer than the newest family
with a changed schema are reported as gaps. --offline checks only the samples (no network).

    python3 scripts/mihon-backup-watch.py [--fixtures] [--offline] [--accept]

Exit codes: 0 ok; 1 the model changed (new field numbers, or an unreviewed schema change; a readable diff is
printed); 2 the release or the model files could not be fetched/parsed; 3 sample gap (--fixtures/--offline, only
when nothing worse happened).

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
ROOT = Path(__file__).resolve().parent.parent
BACKUP = ROOT / "src" / "AutoSourcarr.Core" / "Imports" / "Backup"
PROTO = BACKUP / "MihonBackup.proto"
STATE = BACKUP / "mihon-backup-watch.json"
READER = BACKUP / "MihonBackupReader.cs"
FIXTURES = ROOT / "tests" / "AutoSourcarr.Tests" / "Fixtures" / "backups"

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


def release_model(repo: str, ref: str) -> dict[str, dict[int, str]]:
    """{Kotlin class: {proto number: 'name: Type'}} of the backup model files at one release."""
    listing = fetch_json(f"{API}/repos/{repo}/contents/{MODELS}?ref={ref}")
    if not isinstance(listing, list):
        raise ValueError("the backup model path is not a directory at " + ref)
    files = [item for item in listing if str(item.get("name", "")).endswith(".kt") and item.get("download_url")]
    if not files:
        raise ValueError("no backup model files at " + ref)
    model: dict[str, dict[int, str]] = {}
    for item in files:
        for name, fields in kotlin_fields(fetch(item["download_url"], accept="text/plain").decode("utf-8")).items():
            model.setdefault(name, {}).update(fields)
    return model


def schema_diff(old: dict[str, dict[int, str]], new: dict[str, dict[int, str]]) -> list[str]:
    """Readable differences of two release models (classes and field numbers)."""
    lines: list[str] = []
    for name in sorted(set(old) | set(new)):
        if name.endswith(IGNORED_SUFFIXES):
            continue
        if name not in old:
            lines.append(f"+ class {name}: " + ", ".join(f"{n} = {f}" for n, f in sorted(new[name].items())))
            continue
        if name not in new:
            lines.append(f"- class {name} (no longer written)")
            continue
        for number in sorted(set(old[name]) | set(new[name])):
            before, after = old[name].get(number), new[name].get(number)
            if before == after:
                continue
            if before is None:
                lines.append(f"+ {name}.{number} = {after}")
            elif after is None:
                lines.append(f"- {name}.{number} = {before}")
            else:
                lines.append(f"~ {name}.{number}: {before} -> {after}")
    return lines


def load_state() -> dict:
    try:
        return json.loads(STATE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def families() -> list[str]:
    """Every backup family MihonBackupReader.DetectVersion can report."""
    source = READER.read_text(encoding="utf-8")
    body = source[source.index("string DetectVersion("):]
    body = body[: body.index("\n    }\n")]
    return re.findall(r'return "([\w.-]+)"', body)


def fixture_gaps(latest: str | None, latest_changed: bool) -> tuple[list[str], list[str]]:
    """(report lines, gaps) for the sample backups against the reader's families."""
    expected = json.loads((FIXTURES / "expected.json").read_text(encoding="utf-8"))["fixtures"]
    by_family: dict[str, list[str]] = {}
    gaps: list[str] = []
    for fixture in expected:
        by_family.setdefault(fixture["version"], []).append(fixture["file"])
        if not (FIXTURES / fixture["file"]).is_file():
            gaps.append(f"sample {fixture['file']} is listed in expected.json but missing")
    known = families()
    report = [f"  {family}: " + (", ".join(by_family[family]) if family in by_family else "NO SAMPLE") for family in known]
    gaps.extend(f"no sample backup for family {family}" for family in known if family not in by_family)
    gaps.extend(f"sample family {family} ({', '.join(files)}) is not a family the reader reports"
                for family, files in sorted(by_family.items()) if family not in known)
    match = re.match(r"v?(\d+)\.(\d+)", latest or "")
    if match:
        family = f"mihon-{match.group(1)}.{match.group(2)}"
        if family not in known:
            note = f"latest release {latest} reads as an older family (no '{family}' in DetectVersion)"
            if latest_changed:
                gaps.append(note + " although its backup schema changed: add a sample and a family")
            else:
                report.append("  " + note + "; its schema is unchanged, so that is expected")
    return report, gaps


def main() -> int:
    args = set(sys.argv[1:])
    if args - {"--fixtures", "--offline", "--accept"}:
        print("Usage: mihon-backup-watch.py [--fixtures] [--offline] [--accept]", file=sys.stderr)
        return 2
    if "--offline" in args:
        report, gaps = fixture_gaps(None, False)
        print("Sample backups per family (MihonBackupReader.DetectVersion):")
        print("\n".join(report))
        for gap in gaps:
            print("GAP " + gap)
        return 3 if gaps else 0

    repo = os.environ.get("MIHON_REPO", "mihonapp/mihon").strip()
    previous = str(load_state().get("last_known_release", "")).strip()
    try:
        ref = os.environ.get("MIHON_REF", "").strip() or fetch_json(f"{API}/repos/{repo}/releases/latest")["tag_name"]
        kotlin = release_model(repo, ref)
        old = release_model(repo, previous) if previous and previous != ref else None
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
    changes = schema_diff(old, kotlin) if old is not None else []
    if not previous:
        print("No reviewed release is recorded yet (mihon-backup-watch.json); run with --accept after reviewing this one.")
    elif old is None:
        print(f"Same release as the last reviewed one ({previous}).")
    elif changes:
        print(f"Backup schema changes from {previous} to {ref} (not reviewed yet):")
        print("\n".join(changes))
    else:
        print(f"Backup schema unchanged from {previous} to {ref}.")
    if retired:
        print("Declared in MihonBackup.proto but not written by this release (kept for older backups):")
        print("\n".join(retired))
    code = 0
    if problems:
        print("The Mihon backup model changed; update MihonBackup.proto, add a sample backup and extend MihonBackupReader.DetectVersion:")
        print("\n".join(problems))
        code = 1
    else:
        print("MihonBackup.proto covers every field of this release.")
        if changes and "--accept" not in args:
            print("Review the schema change, add a sample if a new family is needed, then record it: mihon-backup-watch.py --accept")
            code = 1
    gaps: list[str] = []
    if "--fixtures" in args:
        report, gaps = fixture_gaps(ref, bool(changes))
        print("Sample backups per family (MihonBackupReader.DetectVersion):")
        print("\n".join(report))
        for gap in gaps:
            print("GAP " + gap)
    if "--accept" in args:
        if problems:
            print("Not recording the release: MihonBackup.proto does not cover it yet.", file=sys.stderr)
        else:
            STATE.write_text(json.dumps({
                "last_known_release": ref, "repo": repo,
                "note": "Last Mihon release whose backup model was reviewed against MihonBackup.proto (PIPE-9); "
                        "written by scripts/mihon-backup-watch.py --accept."}, indent=2) + "\n", encoding="utf-8", newline="\n")
            print(f"Recorded {ref} as the last reviewed release.")
    if code == 0 and gaps:
        code = 3
    return code


if __name__ == "__main__":
    sys.exit(main())
