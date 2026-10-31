from __future__ import annotations

import gzip
import io
import zlib
import json
import os
from pathlib import Path
from typing import Any


MAX_BACKUP_BYTES = 128 * 1024 * 1024
MAX_DECODED_BYTES = 512 * 1024 * 1024
MAX_BACKUP_RECORDS = 100_000
MAX_DECODED_BACKUP_BYTES = MAX_DECODED_BYTES
MAX_BACKUP_MANGA = MAX_BACKUP_RECORDS


class BackupDecodeError(ValueError):
    pass


def _read_varint(data: bytes, pos: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while pos < len(data) and shift < 70:
        b = data[pos]
        pos += 1
        if shift == 63 and b > 1:
            raise BackupDecodeError("Protobuf varint exceeds 64 bits")
        value |= (b & 0x7F) << shift
        if not (b & 0x80):
            return value, pos
        shift += 7
    raise BackupDecodeError("Invalid or truncated protobuf varint")


def _skip_field(data: bytes, pos: int, wire: int) -> int:
    if wire == 0:
        _, pos = _read_varint(data, pos)
        return pos
    if wire == 1:
        pos += 8
    elif wire == 2:
        size, pos = _read_varint(data, pos)
        pos += size
    elif wire == 5:
        pos += 4
    else:
        raise BackupDecodeError(f"Unsupported protobuf wire type {wire}")
    if pos > len(data):
        raise BackupDecodeError("Truncated protobuf field")
    return pos


def _decode_text(raw: bytes, *, identity: bool = False) -> str:
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        if identity:
            raise BackupDecodeError('Backup URL contains invalid UTF-8') from None
        return raw.decode("utf-8", errors="replace")


def _signed_int64(value: int) -> int:
    return value - (1 << 64) if value >= (1 << 63) else value


def _parse_backup_manga(data: bytes) -> dict[str, Any]:
    item: dict[str, Any] = {
        "source": 0,
        "url": "",
        "title": "",
        "artist": None,
        "author": None,
        "description": None,
        "genre": [],
        "status": 0,
        "thumbnailUrl": None,
        "dateAdded": 0,
        # Mihon's model defaults this to true when the field is absent.
        "favorite": True,
        "version": 0,
        "notes": "",
        "initialized": False,
    }
    text_fields = {2: "url", 3: "title", 4: "artist", 5: "author", 6: "description", 9: "thumbnailUrl", 110: "notes"}
    varint_fields = {1: "source", 8: "status", 13: "dateAdded", 100: "favorite", 109: "version", 111: "initialized"}
    pos = 0
    while pos < len(data):
        key, pos = _read_varint(data, pos)
        field, wire = key >> 3, key & 7
        if field == 0 or field > 536870911:
            raise BackupDecodeError("Invalid protobuf field number")
        if field in text_fields and wire == 2:
            size, pos = _read_varint(data, pos)
            end = pos + size
            if end > len(data):
                raise BackupDecodeError("Truncated manga text field")
            item[text_fields[field]] = _decode_text(data[pos:end], identity=field == 2)
            pos = end
        elif field == 7 and wire == 2:
            size, pos = _read_varint(data, pos)
            end = pos + size
            if end > len(data):
                raise BackupDecodeError("Truncated manga genre field")
            item["genre"].append(_decode_text(data[pos:end]))
            pos = end
        elif field in varint_fields and wire == 0:
            value, pos = _read_varint(data, pos)
            name = varint_fields[field]
            if name == "source":
                item[name] = _signed_int64(value)
            elif name in {"favorite", "initialized"}:
                item[name] = bool(value)
            else:
                item[name] = int(value)
        else:
            pos = _skip_field(data, pos, wire)
    return item



def _parse_backup_source(data: bytes) -> dict[str, Any]:
    item: dict[str, Any] = {"name": "", "sourceId": 0}
    pos = 0
    while pos < len(data):
        key, pos = _read_varint(data, pos)
        field, wire = key >> 3, key & 7
        if field == 0 or field > 536870911:
            raise BackupDecodeError("Invalid protobuf field number")
        if field == 1 and wire == 2:
            size, pos = _read_varint(data, pos)
            end = pos + size
            if end > len(data):
                raise BackupDecodeError("Truncated backup source name")
            item["name"] = _decode_text(data[pos:end])
            pos = end
        elif field == 2 and wire == 0:
            value, pos = _read_varint(data, pos)
            item["sourceId"] = _signed_int64(value)
        else:
            pos = _skip_field(data, pos, wire)
    return item

def decode_backup_bytes(payload: bytes) -> dict[str, Any]:
    if not payload:
        raise BackupDecodeError("Backup file is empty")
    if len(payload) > MAX_BACKUP_BYTES:
        raise BackupDecodeError("Backup file is larger than 128 MiB")
    raw = payload
    if raw[:2] == b"\x1f\x8b":
        try:
            with gzip.GzipFile(fileobj=io.BytesIO(raw)) as stream:
                raw = stream.read(min(MAX_DECODED_BYTES, MAX_DECODED_BACKUP_BYTES) + 1)
        except (OSError, EOFError, zlib.error) as exc:
            raise BackupDecodeError(f"Invalid gzip backup: {exc}") from exc
    if len(raw) > min(MAX_DECODED_BYTES, MAX_DECODED_BACKUP_BYTES):
        raise BackupDecodeError("Decoded backup exceeds the 512 MiB safety limit")
    if raw.lstrip().startswith((b"{", b"[")):
        raise BackupDecodeError("JSON backups are not supported by current Mihon backup format")

    mangas: list[dict[str, Any]] = []
    backup_sources: list[dict[str, Any]] = []
    pos = 0
    while pos < len(raw):
        key, pos = _read_varint(raw, pos)
        field, wire = key >> 3, key & 7
        if field == 0 or field > 536870911:
            raise BackupDecodeError("Invalid protobuf field number")
        if field == 1 and wire == 2:  # Backup.backupManga
            size, pos = _read_varint(raw, pos)
            end = pos + size
            if end > len(raw):
                raise BackupDecodeError("Truncated BackupManga message")
            if len(mangas) >= min(MAX_BACKUP_RECORDS, MAX_BACKUP_MANGA):
                raise BackupDecodeError("Backup has too many manga records")
            mangas.append(_parse_backup_manga(raw[pos:end]))
            pos = end
        elif field == 101 and wire == 2:  # Backup.backupSources
            size, pos = _read_varint(raw, pos)
            end = pos + size
            if end > len(raw):
                raise BackupDecodeError("Truncated BackupSource message")
            backup_sources.append(_parse_backup_source(raw[pos:end]))
            pos = end
        else:
            pos = _skip_field(raw, pos, wire)
    if not mangas:
        raise BackupDecodeError("No manga entries were found in the backup")
    source_names = {int(x.get("sourceId") or 0): str(x.get("name") or "").strip() for x in backup_sources if str(x.get("name") or "").strip()}
    for manga in mangas:
        manga["sourceName"] = source_names.get(int(manga.get("source") or 0), "")
    favorites = sum(1 for m in mangas if bool(m.get("favorite", True)))
    return {
        "format": "mihon-tachiyomi-protobuf",
        "manga_count": len(mangas),
        "favorite_count": favorites,
        "backup_sources": backup_sources,
        "manga": mangas,
    }


def decode_backup_file(input_path: Path, readable_path: Path) -> dict[str, Any]:
    with input_path.open("rb") as stream:
        payload = stream.read(MAX_BACKUP_BYTES + 1)
    decoded = decode_backup_bytes(payload)
    readable_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = readable_path.with_suffix(readable_path.suffix + ".tmp")
    try:
        with temporary.open("w", encoding="utf-8") as destination:
            json.dump(decoded, destination, ensure_ascii=False, indent=2)
        os.replace(temporary, readable_path)
    finally:
        temporary.unlink(missing_ok=True)
    return decoded
