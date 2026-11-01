#!/usr/bin/env python3
"""Writes the sample backups of every supported Mihon/Tachiyomi backup version (IMP-11, PIPE-9).

The bytes are hand-assembled with the field numbers of src/AutoSourcarr.Core/Imports/Backup/MihonBackup.proto (taken
from Mihon's backup model classes per release). Titles, sources and URLs are invented: no real library data.

    python3 tests/AutoSourcarr.Tests/Fixtures/backups/generate-backups.py

When the backup-format watch reports a new Mihon backup layout, add a writer here, run it, and add the expected values
to expected.json; the C# test "imports: every sample backup imports end to end" reads that file.
"""
from __future__ import annotations

import gzip
import json
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent


def varint(value: int) -> bytes:
    value &= (1 << 64) - 1
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def key(field: int, wire: int) -> bytes:
    return varint((field << 3) | wire)


def v(field: int, value: int) -> bytes:
    return key(field, 0) + varint(int(value))


def b(field: int, value: bytes) -> bytes:
    return key(field, 2) + varint(len(value)) + value


def s(field: int, value: str) -> bytes:
    return b(field, value.encode("utf-8"))


def f32(field: int, value: float) -> bytes:
    return key(field, 5) + struct.pack("<f", value)


def f64(field: int, value: int) -> bytes:
    return key(field, 1) + struct.pack("<q", value)


HARBOR = 9101
QUILL = -4611686018427387904  # a negative (unsigned-overflowing) Tachiyomi source id

WORKS = [
    {
        "source": HARBOR, "url": "/series/lantern-keeper-ninth-harbor", "title": "Lantern Keeper of the Ninth Harbor",
        "author": "Mirela Tov", "artist": "Mirela Tov",
        "description": "A lighthouse apprentice keeps nine harbour lamps burning while the tide brings letters from ships that sank a century ago.",
        "genre": ["Fantasy", "Mystery"], "status": 1, "favorite": True,
    },
    {
        "source": QUILL, "url": "/title/clockwork-orchard-colored", "title": "The Clockwork Orchard (Colored)",
        "author": "Ansel Brightwater", "artist": "Ansel Brightwater",
        "description": "In a valley where apple trees tick like pocket watches, a young tinkerer repairs the orchard before the harvest stops time.",
        "genre": ["Adventure", "Full Color"], "status": 2, "favorite": True,
    },
    {
        "source": HARBOR, "url": "/series/salt-and-ember-cartography", "title": "Salt and Ember Cartography",
        "author": "Odile Marsh", "artist": "Odile Marsh",
        "description": "Two mapmakers chart a coast that rearranges itself every winter.",
        "genre": ["Drama"], "status": 1, "favorite": False,
    },
]
SOURCES = [(HARBOR, "Harborlight (EN)"), (QUILL, "Quillfeed")]


def chapter(index: int, *, sync: bool = False, memo: bool = False, future: bool = False) -> bytes:
    body = s(1, f"/chapter/{index}") + s(2, f"Chapter {index}") + s(3, "Invented Scans") + v(4, 1 if index == 1 else 0)
    body += v(7, 1700000000000 + index) + v(8, 1690000000000 + index) + f32(9, float(index)) + v(10, index)
    if sync:
        body += v(11, 1700000500000) + v(12, 3)
    if memo:
        body += b(13, b'{"origin":"sample"}')
    if future:
        body += s(14, "future chapter field")
    return body


def manga(work: dict, era: str) -> bytes:
    body = v(1, work["source"]) + s(2, work["url"]) + s(3, work["title"]) + s(4, work["artist"]) + s(5, work["author"])
    body += s(6, work["description"])
    for genre in work["genre"]:
        body += s(7, genre)
    body += v(8, work["status"]) + s(9, "https://covers.invalid" + work["url"] + ".webp") + v(13, 1680000000000)
    sync = era in {"mihon-0.16", "mihon-0.17", "mihon-0.19", "mihon-0.20"}
    memo = era in {"mihon-0.20", "mihon-0.21", "next"}
    for index in (1, 2):
        body += b(16, chapter(index, sync=era in {"mihon-0.17", "mihon-0.19", "mihon-0.20"}, memo=memo, future=era == "next"))
    body += v(17, 1)
    body += b(18, v(1, 2) + v(2, 77) + s(4, "https://tracker.invalid/77") + s(5, work["title"]) + f32(6, 1.0) + v(7, 12) + v(9, 1) + v(100, 77))
    if not work["favorite"]:
        body += v(100, 0)
    body += v(101, 0)
    if era == "tachiyomi-legacy":
        # BrokenBackupHistory uses the non-compliant field number 0.
        body += b(102, s(0, "/chapter/1") + v(1, 1700000000000))
    else:
        body += b(104, s(1, "/chapter/1") + v(2, 1700000000000) + v(3, 120000))
    if era != "tachiyomi-legacy":
        body += v(103, 0)
    if sync:
        body += v(106, 1700000600000)
    if era.startswith("mihon") or era in {"next", "fork"}:
        body += v(107, 1700000700000)
        body += s(108, "Blocked Scans")
    if era in {"mihon-0.17", "mihon-0.19", "mihon-0.20"}:
        body += v(109, 4)
    if era in {"mihon-0.19", "mihon-0.20", "mihon-0.21", "next", "fork"}:
        body += s(110, "sample note") + v(111, 1)
    if memo:
        body += b(112, b'{"origin":"sample"}')
    if era in {"mihon-0.21", "next"}:
        body += v(113, 1800000000000)
    if era == "next":
        body += s(114, "unknown future text") + v(115, 7)
    if era == "fork":
        # TachiyomiSY/Komikku-style extras: custom title/author/status, and odd wire types.
        body += s(800, work["title"] + " (custom)") + s(801, "Custom Author") + v(802, 3) + f64(803, 42) + key(804, 5) + struct.pack("<I", 9)
    return body


def backup(era: str) -> bytes:
    body = b""
    for work in WORKS:
        body += b(1, manga(work, era))
    body += b(2, s(1, "Reading") + v(2, 0) + v(3, 1) + v(100, 0))
    if era == "tachiyomi-legacy":
        for source_id, name in SOURCES:
            body += b(100, s(0, name) + v(1, source_id))  # BrokenBackupSource, field 0
    else:
        for source_id, name in SOURCES:
            body += b(101, s(1, name) + v(2, source_id))
    body += b(104, s(1, "pref_sample") + b(2, s(1, "kotlin.String") + b(2, s(1, "x"))))
    body += b(105, s(1, "source_9101") + b(2, s(1, "pref") + b(2, b"")))
    if era in {"mihon-0.17", "mihon-0.19"}:
        body += b(106, s(1, "https://repo.invalid") + s(2, "Invented Repo") + s(3, "IR") + s(4, "https://repo.invalid/site") + s(5, "ab" * 32))
    if era in {"mihon-0.20", "mihon-0.21", "next"}:
        body += b(106, s(1, "https://store.invalid/index.min.json") + s(2, "Invented Store") + s(3, "IS") + s(5, "cd" * 32)
                  + s(4, "https://store.invalid") + s(6, "https://discord.invalid") + v(7, 0) + s(8, "https://store.invalid/list"))
    if era == "next":
        body += s(107, "unknown future backup field")
    if era == "fork":
        body += b(500, s(1, "Saved search") + s(2, "{}")) + v(600, 1)
    return body


FIXTURES = [
    ("tachiyomi-legacy.proto.gz", "tachiyomi-legacy", "tachiyomi-legacy", True),
    ("tachiyomi.proto.gz", "tachiyomi", "tachiyomi", True),
    ("mihon-0.16.tachibk", "mihon-0.16", "mihon-0.16", True),
    ("mihon-0.17.tachibk", "mihon-0.17", "mihon-0.17", True),
    ("mihon-0.19.tachibk", "mihon-0.19", "mihon-0.19", True),
    ("mihon-0.20.tachibk", "mihon-0.20", "mihon-0.20", True),
    ("mihon-0.21.tachibk", "mihon-0.21", "mihon-0.21", True),
    # The layout after v0.21 is not published yet (IMP-11): this probe is v0.21 with unknown future fields, uncompressed.
    ("mihon-next-probe.tachibk", "next", "mihon-0.21", False),
    ("fork-extra-fields.tachibk", "fork", "mihon-0.19", True),
]


def main() -> None:
    expected = []
    for name, era, version, compressed in FIXTURES:
        payload = backup(era)
        data = gzip.compress(payload, mtime=0) if compressed else payload
        (HERE / name).write_bytes(data)
        expected.append({
            "file": name, "version": version, "compression": "gzip" if compressed else "none",
            "manga": len(WORKS), "favorites": sum(1 for work in WORKS if work["favorite"]),
            "titles": [work["title"] for work in WORKS],
            "source_names": {str(source_id): source_name for source_id, source_name in SOURCES},
        })
    (HERE / "expected.json").write_text(json.dumps({"fixtures": expected}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
