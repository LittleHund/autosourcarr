"""Source-native gallery metadata and conservative work identity.

Only known gallery sources (or an explicit gallery profile) use decorated-title
parsing. Raw titles/fields are retained; an empty provider search is not evidence
that a gallery lacks metadata, nor permission to invent a synopsis.
"""
from __future__ import annotations

import json
import re
import unicodedata
from typing import Any, Callable
from urllib.parse import urlsplit

from .matcher import best_automatic_title_score, normalize_title, title_color_variant, title_media_variant

_GALLERY_NAMES = {"ehentai", "exhentai", "imhentai", "luscious"}
_GALLERY_HOSTS = {"e-hentai.org", "exhentai.org", "imhentai.xxx", "luscious.net"}
_NAMESPACES = {"artist", "author", "circle", "group", "parody", "character", "language", "category", "type", "genre", "tag", "male", "female", "mixed", "other", "cosplayer", "uploader"}
_LANGUAGES = {"english": "English", "eng": "English", "en": "English", "japanese": "Japanese", "jpn": "Japanese", "ja": "Japanese", "chinese": "Chinese", "zh": "Chinese", "korean": "Korean", "ko": "Korean", "spanish": "Spanish", "es": "Spanish", "french": "French", "fr": "French", "german": "German", "de": "German", "dutch": "Dutch", "nl": "Dutch", "portuguese": "Portuguese", "pt": "Portuguese", "russian": "Russian", "ru": "Russian", "thai": "Thai", "th": "Thai"}
_CATEGORIES = {"doujinshi", "manga", "artist cg", "game cg", "western", "imageset", "image set", "cosplay", "asian porn", "non-h", "comic", "comics"}
_GENERIC = {"english", "japanese", "translated", "translation", "manga", "comic", "comics", "doujinshi", "color", "colored", "colour", "full color", "full colour", "uncensored", "censored", "digital", "scan", "scanned", "adult", "18+", "non-h"}


def _text(value: Any) -> str:
    return " ".join(unicodedata.normalize("NFKC", str(value or "")).split()).strip()


def is_gallery_source(source: dict[str, Any] | None, manga: dict[str, Any] | None = None) -> bool:
    source = source or {}; manga = manga or {}
    if str(source.get("metadata_profile") or "").lower() == "gallery":
        return True
    for value in (source.get("name"), source.get("display_name"), manga.get("sourceName")):
        name = re.sub(r"\([^)]*\)|\[[^]]*\]", "", _text(value))
        if re.sub(r"[^a-z0-9]", "", name.lower()) in _GALLERY_NAMES:
            return True
    for value in (source.get("base_url"), source.get("baseUrl"), manga.get("realUrl"), manga.get("url")):
        try:
            host = (urlsplit(str(value or "")).hostname or "").lower().removeprefix("www.")
        except ValueError:
            continue
        if host in _GALLERY_HOSTS:
            return True
    return False


def source_tags(manga: dict[str, Any]) -> list[dict[str, str]]:
    """Read namespace-bearing source tags without dropping their original spelling."""
    result: list[dict[str, str]] = []
    seen: set[tuple[str, str]] = set()

    def add(namespace: str, value: Any) -> None:
        name = _text(value)
        namespace = _text(namespace).lower() or "tag"
        namespace = {"artists": "artist", "authors": "author", "groups": "circle", "group": "circle", "languages": "language", "characters": "character", "parodies": "parody", "genres": "tag", "genre": "tag", "tags": "tag", "general": "tag", "type": "category"}.get(namespace, namespace)
        if not name:
            return
        key = (namespace, name.casefold())
        if key not in seen:
            seen.add(key)
            result.append({"namespace": namespace, "name": name})

    def parse(value: Any, namespace: str = "tag") -> None:
        # Older catalogue rows persisted genre arrays/objects as JSON strings.
        # Decode only valid containers; bracketed literal tags are still text.
        if isinstance(value, str) and value.lstrip().startswith(("[", "{")):
            try:
                decoded = json.loads(value)
            except (ValueError, TypeError):
                decoded = None
            if isinstance(decoded, (list, dict)):
                parse(decoded, namespace)
                return
        if isinstance(value, dict):
            if "name" in value or ("tag" in value and isinstance(value["tag"], str)):
                add(str(value.get("namespace") or value.get("type") or namespace), value.get("name") or value.get("tag"))
            else:
                for ns, names in value.items():
                    parse(names, str(ns))
        elif isinstance(value, (list, tuple, set)):
            for item in value:
                parse(item, namespace)
        else:
            for name in re.split(r"[,;\n]", str(value or "")):
                match = re.match(r"^\s*([\w -]+)\s*:\s*(.+)$", name)
                if match and match[1].strip().lower() in _NAMESPACES:
                    add(match[1], match[2])
                else:
                    add(namespace, name)

    for field in ("tags", "genre", "genres"):
        parse(manga.get(field))
    for namespace in ("artist", "author", "circle", "parody", "character", "language", "category"):
        if manga.get(namespace):
            parse(manga[namespace], namespace)
    return result


def enrich_gallery_payload(manga: dict[str, Any], source: dict[str, Any] | None = None) -> dict[str, Any]:
    out = dict(manga or {})
    if not is_gallery_source(source, out):
        return out
    previous = out.get("_gallery_metadata") if isinstance(out.get("_gallery_metadata"), dict) else {}
    raw = str(previous.get("raw_title") or out.get("_autosourcarr_raw_title") or out.get("title") or "")
    title = _text(raw)
    tags = source_tags(out)
    provenance: dict[str, str] = {"title": "source title", "tags": "source fields"}
    data: dict[str, Any] = {"animated": False, "chapter_number": None, "work_title": "", "raw_title": raw, "title": title, "artist": "", "circle": "", "language": "", "category": "", "parody": "", "kind": "work", "tags": tags, "provenance": provenance, "annotations": []}
    if source:
        data["source_name"] = str(source.get("name") or source.get("display_name") or "")
    data["source_id"] = str(out.get("sourceId") or (source or {}).get("source_id") or "")
    data["source_manga_id"] = out.get("id") or 0

    def record(namespace: str, value: str, origin: str) -> None:
        value = _text(value)
        if value and not any(t["namespace"] == namespace and t["name"].casefold() == value.casefold() for t in tags):
            tags.append({"namespace": namespace, "name": value})
        if namespace in data and value and not data.get(namespace):
            data[namespace] = value
            provenance[namespace] = origin

    for tag in list(tags):
        ns = {"group": "circle", "type": "category"}.get(tag["namespace"], tag["namespace"])
        if ns in {"artist", "circle", "language", "category", "parody"}:
            record(ns, tag["name"], "source field")
    # Animation is a content identity, not a franchise or a broad genre match.
    # Only explicit tags/decorations count; prose such as "an animated discussion"
    # is not an animation marker.
    data["animated"] = any(t["namespace"] in {"tag", "other", "category", "mixed"} and _text(t["name"]).casefold() in {"animated", "animation", "animated gallery"} for t in tags)
    animation_marker = re.compile(r"\s*[\[(]\s*(?:animated|animation)\s*[\])]", re.I)
    if animation_marker.search(title):
        data["animated"] = True
        title = animation_marker.sub("", title).strip()
    def strip_chapter_suffix(value: str) -> str:
        serial = re.search(r"(?:\s+|^)[\[(]?(?:chapter|chap\.?|ch\.?|part|pt\.?|episode|ep\.?)\s*[:#.-]?\s*(\d+(?:\.\d+)?)[\])]?(?:\s*)$", value, re.I)
        if serial and value[:serial.start()].strip(" -:–—"):
            data["chapter_number"] = float(serial[1])
            provenance["chapter_number"] = "source title"
            return value[:serial.start()].strip(" -:–—")
        return value
    # A publisher's gallery/artist archive is not a single work. A literal
    # [Artist] or [Circle] header is a metadata label, not the creator's name.
    # Recognized update dates annotate the collection, never the creator.
    bracket_label = re.match(r"^\[(artist|circle|collection)\]\s*(.+)$", title, re.I)
    explicit_collection = re.match(r"^(artist|circle|collection|image\s*set)(?:\s*[:–—-]+\s*|\s+)(.+)$", title, re.I)
    collection_header = bracket_label or explicit_collection
    if collection_header:
        data["kind"] = "collection"
        label = collection_header[1].lower()
        name = collection_header[2].strip(" ♥❤♡💕💖💗💓💞💘💝❣️:–—-")
        update = re.search(r"(?:\s+|^)[\"'“”‘’]?updates?(?:d)?[\"'“”‘’]?\s*[:–—-]?\s*\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\s*$", name, re.I)
        if update:
            data["annotations"].append(name[update.start():].strip())
            name = name[:update.start()].strip()
        # In artist-heading galleries a part suffix belongs to the upload,
        # never to the creator. Language labels likewise remain metadata.
        while (label_suffix := re.search(r"\s*\[([^\[\]]+)\]\s*$", name)):
            key = label_suffix[1].strip().casefold()
            if key in _LANGUAGES:
                record("language", _LANGUAGES[key], "title suffix")
            elif key in _CATEGORIES:
                record("category", label_suffix[1], "title suffix")
            else:
                break
            name = name[:label_suffix.start()].strip()
        name = strip_chapter_suffix(name)
        data["collection_name"] = name
        title = name
        if label in {"artist", "circle"}:
            record(label, name, "title label")
    if re.search(r"\b(?:artist\s+(?:collection|pack)|complete\s+(?:works|collection)|image\s*set|collection|updates?(?:d)?\b.*\d|updates?\s*$)", title, re.I):
        data["kind"] = "collection"
    # Event labels such as (C105) are provenance, never part of work identity.
    event = re.match(r"^\((C\d{2,3}|COMIC\d+|COMITIA\d+)\)\s*", title, re.I)
    if event:
        data["annotations"].append(event[1]); title = title[event.end():]
    creator = re.match(r"^\[([^\[\]]+)\]\s*(.+)$", title)
    if creator and not collection_header and creator[1].casefold() not in _LANGUAGES and creator[1].casefold() not in _CATEGORIES and ":" not in creator[1]:
        value = creator[1]
        combined = re.fullmatch(r"(.+?)\s*\(([^()]+)\)", value)
        if combined:
            record("circle", combined[1], "title prefix")
            record("artist", combined[2], "title prefix")
        else:
            record("artist", value, "title prefix")
        # Preserve edition markers and collection names; only strip creator prefix.
        title = creator[2]
    # Only remove understood language/category decorations. Unknown blocks stay
    # visible (e.g. translator names) instead of being silently misclassified.
    suffixes: list[str] = []
    while (suffix := re.search(r"\s*\[([^\[\]]+)\]\s*$", title)):
        label = suffix[1].strip(); key = label.casefold()
        if key in _LANGUAGES:
            record("language", _LANGUAGES[key], "title suffix")
        elif key in _CATEGORIES:
            record("category", label, "title suffix")
        elif key in {"digital", "translated", "translation"}:
            record("tag", label, "title suffix")
        else:
            break
        suffixes.append(label); title = title[:suffix.start()].strip()
    # A gallery uploaded per chapter carries that chapter in its work title.
    # Strip only an explicit terminal chapter/part/episode token, never a bare
    # number, volume, edition, season or chapter range.
    title = strip_chapter_suffix(title)
    # Parenthetical franchise is conventional on galleries, but chapter/edition
    # markers must retain their identity. Explicit metadata wins on ambiguity.
    parody = re.search(r"\s+\(([^()]+)\)\s*$", title)
    if parody and not re.search(r"\b(?:colou?r|mono|manga|webtoon|chapter|volume|part|episode|ver(?:sion)?|edition|revised|uncensored|censored|digital|special|extra|omake|bonus|prologue|epilogue|rewrite|textless|decensored|mosaic|anthology)\b|\d", parody[1], re.I):
        record("parody", parody[1], "title suffix")
        title = strip_chapter_suffix(title[:parody.start()].strip())
    data["work_title"] = title or _text(raw)
    if data["animated"]:
        title = title + " (animated)"
    if title and title != _text(raw):
        out.setdefault("_autosourcarr_raw_title", raw)
        out["title"] = title
        provenance["title"] = "parsed source title"
    data["title"] = title or _text(raw)
    if data["parody"] and normalize_title(data["title"]) == normalize_title(data["parody"]):
        data["kind"] = "collection"
    if data["category"].casefold() in {"artist cg", "imageset", "image set"}:
        data["kind"] = "collection"
    if not out.get("artist") and data["artist"]:
        out["artist"] = data["artist"]
    # Re-enriching a cached payload must not relabel a parsed creator as an
    # upstream structured field simply because we filled artist above last time.
    for field in ("artist", "circle", "language", "category", "parody"):
        if data.get(field) and data.get(field) == previous.get(field):
            origin = (previous.get("provenance") or {}).get(field)
            if origin:
                provenance[field] = origin
    out["_gallery_metadata"] = data
    return out


def gallery_pair_evidence(left: dict[str, Any] | None, right: dict[str, Any] | None, canonicalize: Callable[[list[dict]], set[str]], *, threshold: float = 0.94) -> dict[str, Any]:
    """Gallery identity needs a work title plus tags, never a generic tag bag."""
    left = left or {}; right = right or {}
    result: dict[str, Any] = {"applies": bool(left or right), "eligible": False, "score": 0.0, "title_score": 0.0, "tag_overlap": [], "reason": "gallery metadata needs source detail hydration"}
    if not result["applies"] or not left or not right:
        return result
    if bool(left.get("animated")) != bool(right.get("animated")):
        result["reason"] = "animated and non-animated galleries are separate entries"
        return result
    if "collection" in {left.get("kind"), right.get("kind")}:
        result["reason"] = "artist collections need manual work-level review"
        return result
    lt = str(left.get("title") or ""); rt = str(right.get("title") or "")
    score, _, _ = best_automatic_title_score([lt], [rt])
    result["title_score"] = score
    for name, fn in (("color edition", title_color_variant), ("media edition", title_media_variant)):
        a = fn(str(left.get("raw_title") or lt)); b = fn(str(right.get("raw_title") or rt))
        if a != "unknown" and b != "unknown" and a != b:
            result["reason"] = f"gallery {name} conflict"; return result
    # Volume/part identifiers may be omitted by generic title normalization. An
    # explicit mismatch is a hard conflict, not a near-title candidate.
    serial = lambda t: re.findall(r"\b(?:part|vol(?:ume)?\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)", t, re.I)
    if serial(str(left.get("raw_title") or lt)) != serial(str(right.get("raw_title") or rt)) and (serial(str(left.get("raw_title") or lt)) or serial(str(right.get("raw_title") or rt))):
        result["reason"] = "gallery part/volume mismatch"; return result
    ltags = list(left.get("tags") or []); rtags = list(right.get("tags") or [])
    for ns in ("artist", "circle", "parody"):
        a = canonicalize([t for t in ltags if t.get("namespace") == ns])
        b = canonicalize([t for t in rtags if t.get("namespace") == ns])
        if a and b and not a.intersection(b):
            result["reason"] = f"gallery {ns} conflict"; return result
    def informative(tags: list[dict]) -> list[dict]:
        return [t for t in tags if t.get("namespace") not in {"language", "category", "type", "uploader"} and _text(t.get("name")).casefold() not in _GENERIC]
    lkeys = canonicalize(informative(ltags)); rkeys = canonicalize(informative(rtags))
    overlap = lkeys & rkeys
    result["tag_overlap"] = sorted(overlap)
    ratio = len(overlap) / max(1, min(len(lkeys), len(rkeys)))
    result["tag_similarity"] = ratio
    creator_overlap = bool(canonicalize([t for t in ltags if t.get("namespace") in {"artist", "circle"}]) & canonicalize([t for t in rtags if t.get("namespace") in {"artist", "circle"}]))
    distinctive = len(normalize_title(lt).split()) >= 3 or len(normalize_title(lt)) >= 18
    enough = bool((len(overlap) >= 2 and ratio >= 0.5 and creator_overlap) or (len(overlap) >= 3 and ratio >= 0.75 and distinctive))
    result["eligible"] = bool(score >= max(0.94, float(threshold)) and enough)
    result["score"] = score if result["eligible"] else 0.0
    result["reason"] = f"gallery title {score:.0%} + {len(overlap)} shared source tags" if result["eligible"] else "gallery match needs a strong work title and informative shared tags"
    return result


def gallery_family_evidence(left: dict[str, Any], right: dict[str, Any], canonicalize: Callable[[list[dict]], set[str]], *, threshold: float = .94) -> dict[str, Any]:
    """Authorize chapter membership without claiming two galleries are one release.

    Artist archives are only grouped within one source and the same explicit
    creator. Numbered works can share a family across sources with the existing
    title/tag/creator evidence. Animation and manual reviews remain separate gates.
    """
    result = {"applies": bool(left or right), "eligible": False, "score": 0.0, "title_score": 0.0,
              "reason": "gallery family needs a source-native creator and work title"}
    if not left or not right or bool(left.get("animated")) != bool(right.get("animated")):
        return result
    lt = str(left.get("work_title") or left.get("title") or "")
    rt = str(right.get("work_title") or right.get("title") or "")
    score, _, _ = best_automatic_title_score([lt], [rt])
    result["title_score"] = score
    if score < max(.94, float(threshold)):
        return result
    a = dict(left, title=lt, raw_title=lt, kind="work")
    b = dict(right, title=rt, raw_title=rt, kind="work")
    # Do not erase color/media edition evidence when removing chapter suffixes.
    for fn in (title_color_variant, title_media_variant):
        x, y = fn(str(left.get("raw_title") or lt)), fn(str(right.get("raw_title") or rt))
        if x != "unknown" and y != "unknown" and x != y:
            return result
    creator = False
    for ns in ("artist", "circle", "parody"):
        l = canonicalize([t for t in left.get("tags", []) if t.get("namespace") == ns])
        r = canonicalize([t for t in right.get("tags", []) if t.get("namespace") == ns])
        if l and r and not l.intersection(r):
            return result
        if ns in {"artist", "circle"} and l.intersection(r):
            creator = True
    same_source = bool(left.get("source_id") and left.get("source_id") == right.get("source_id"))
    collection = "collection" in {left.get("kind"), right.get("kind")}
    if collection and (not same_source or left.get("kind") != right.get("kind")):
        return result
    if same_source and creator and normalize_title(lt) == normalize_title(rt):
        return {**result, "eligible": True, "score": score, "reason": "same-source gallery family: exact work title and creator", "chapter_family": True}
    # Cross-source collection headings can encompass unrelated subsets. A
    # numbered work is narrower and uses the existing informative-tag gate.
    if collection or (left.get("chapter_number") is None and right.get("chapter_number") is None):
        return result
    evidence = gallery_pair_evidence(a, b, canonicalize, threshold=threshold)
    return {**evidence, "chapter_family": bool(evidence.get("eligible"))}
