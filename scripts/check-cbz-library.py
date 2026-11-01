#!/usr/bin/env python3
"""Check a CBZ library folder against what Mihon's local source, Kavita and Komga read (CHAP-7).

Layout: <Library>/<Series>/<Chapter>.cbz, <Series>/cover.jpg, optional <Series>/details.json, ComicInfo.xml inside
every CBZ. The rules follow the readers' own code:

* Mihon source-local (LocalSource.kt): series are non-hidden folders; chapters are non-hidden .zip/.cbz/... files,
  named after the file; the chapter number comes from ComicInfo <Number> or else ChapterRecognition on the name;
  the cover is a file named "cover.*" that is an image; series details come from a top-level ComicInfo.xml, else a
  *.json details file (title, author, artist, description, genre[], status "0".."6"), else the first archive.
* Kavita scanner (Parser.cs, Manga library): every series in its own folder, no files at the library root, volume and
  chapter from the file name markers (Vol.N, Ch.N), overridden by ComicInfo <Volume>/<Number> for cb* files; a file
  without a chapter marker is a loose leaf or (with <Format>Special</Format>) a special.
* Komga: folders of CBZ files, books sorted by name / ComicInfo number.

Only the subset of Kavita's manga parser that applies to the names AutoSourcarr writes is reproduced; a series
prefix with digits is reported because Kavita would read those digits as volume or chapter markers.

Usage: check-cbz-library.py LIBRARY [--json] [--expect-series N] [--expect-chapters N]
Exit status 0 when the library is valid, 1 on errors, 2 on usage errors.
"""
import argparse
import json
import re
import sys
import unicodedata
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

ELEMENT_ORDER = [
    "Title", "Series", "Number", "Count", "Volume", "AlternateSeries", "AlternateNumber", "AlternateCount", "Summary", "Notes",
    "Year", "Month", "Day", "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator",
    "Publisher", "Imprint", "Genre", "Tags", "Web", "PageCount", "LanguageISO", "Format", "BlackAndWhite", "Manga",
    "Characters", "Teams", "Locations", "ScanInformation", "StoryArc", "StoryArcNumber", "SeriesGroup", "AgeRating",
    "Pages", "CommunityRating", "MainCharacterOrTeam", "Review", "GTIN",
]
REQUIRED = ["Title", "Series", "Writer", "Summary", "Genre", "PageCount"]
MANGA_VALUES = {"Unknown", "No", "Yes", "YesAndRightToLeft"}
ARCHIVES = {".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7", ".tar", ".cbt"}
IMAGES = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp", ".jxl", ".heif"}
WINDOWS_RESERVED = set('<>:"/\\|?*')
DEVICE_NAMES = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(10)), *(f"LPT{i}" for i in range(10))}
COMPONENT_BYTES = 255

# Kavita Parser.cs: Number = \d+(\.\d)? (one decimal digit); the volume and chapter regexes that match "Vol.NN" / "Ch.NNN".
KAVITA_VOLUME = re.compile(r"(vol\.? ?)(?P<v>\d+(\.\d)?)", re.IGNORECASE)
KAVITA_CHAPTER = re.compile(r"(\b|_)(c|ch)(\.?\s?)(?P<c>(\d+(\.\d)?)(-c?\d+(\.\d)?)?)", re.IGNORECASE)
# Mihon ChapterRecognition: the volume is removed first, then the number after "ch." or the first number.
MIHON_VOLUME = re.compile(r"\b(?:(?:vol(?:ume)?|season)[^a-z0-9]? *|[vs]\.?)([0-9]+(?:\.[0-9]+)?)\b")
MIHON_BASIC = re.compile(r"(?<=ch\.) *([0-9]+)(\.[0-9]+)?(\.?[a-z]+)?")
MIHON_NUMBER = re.compile(r"([0-9]+)(\.[0-9]+)?(\.?[a-z]+)?")


def image_kind(data):
    if data[:3] == b"\xff\xd8\xff":
        return "jpeg"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return "gif"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "webp"
    if data[4:8] == b"ftyp":
        return "avif"
    if data[:2] == b"BM":
        return "bmp"
    return None


def natural_key(name):
    return [int(part) if part.isdigit() else part.lower() for part in re.split(r"(\d+)", name)]


def kavita_number(text):
    """Kavita keeps one decimal digit of a parsed marker ("12.25" -> 12.2)."""
    match = re.match(r"\d+(\.\d)?", text)
    return float(match.group(0)) if match else None


def mihon_number(series, name):
    clean = name.lower().replace(series.lower(), "")
    clean = MIHON_VOLUME.sub(" ", clean).strip().replace(",", ".").replace("-", ".")
    numbers = list(MIHON_NUMBER.finditer(clean))
    match = MIHON_BASIC.search(clean) if len(numbers) > 1 else None
    match = match or (numbers[0] if numbers else None)
    if not match:
        return -1.0
    return float(match.group(1)) + (float(match.group(2)) if match.group(2) else 0.0)


def check_component(name, where, errors):
    if not name or name in (".", ".."):
        errors.append(f"{where}: empty or traversal name")
        return
    if any(ch in WINDOWS_RESERVED or ord(ch) < 32 for ch in name):
        errors.append(f"{where}: name has a character Windows cannot store: {name!r}")
    if name != name.rstrip(" .") or name != name.lstrip(" "):
        errors.append(f"{where}: name starts with a space or ends with a space or dot: {name!r}")
    if name.split(".")[0].upper() in DEVICE_NAMES:
        errors.append(f"{where}: Windows device name: {name!r}")
    if len(name.encode("utf-8")) > COMPONENT_BYTES:
        errors.append(f"{where}: name is longer than {COMPONENT_BYTES} UTF-8 bytes")
    if unicodedata.normalize("NFC", name) != name:
        errors.append(f"{where}: name is not NFC-normalised")


def check_comicinfo(data, where, errors, page_count):
    try:
        root = ElementTree.fromstring(data)
    except ElementTree.ParseError as error:
        errors.append(f"{where}: ComicInfo.xml is not valid XML: {error}")
        return None
    if root.tag != "ComicInfo":
        errors.append(f"{where}: ComicInfo.xml root is <{root.tag}>, expected <ComicInfo>")
        return None
    names = [child.tag for child in root]
    unknown = [name for name in names if name not in ELEMENT_ORDER]
    if unknown:
        errors.append(f"{where}: unknown ComicInfo elements {unknown}")
    known = [ELEMENT_ORDER.index(name) for name in names if name in ELEMENT_ORDER]
    if known != sorted(known) or len(set(names)) != len(names):
        errors.append(f"{where}: ComicInfo elements are out of schema order or repeated: {names}")
    values = {child.tag: (child.text or "").strip() for child in root}
    special = values.get("Format") == "Special"
    for name in REQUIRED + ([] if special else ["Number"]):
        if not values.get(name):
            errors.append(f"{where}: ComicInfo <{name}> is missing or empty")
    for name in ("Number", "AlternateNumber"):
        if values.get(name) and not re.fullmatch(r"-?\d+(\.\d+)?", values[name]):
            errors.append(f"{where}: ComicInfo <{name}> is not a number: {values[name]!r}")
    for name in ("Count", "Volume", "Year", "Month", "Day", "PageCount"):
        if values.get(name) and not re.fullmatch(r"-?\d+", values[name]):
            errors.append(f"{where}: ComicInfo <{name}> is not an integer: {values[name]!r}")
    if values.get("Month") and not 1 <= int(values["Month"]) <= 12:
        errors.append(f"{where}: ComicInfo <Month> out of range")
    if values.get("Day") and not 1 <= int(values["Day"]) <= 31:
        errors.append(f"{where}: ComicInfo <Day> out of range")
    if values.get("PageCount") and re.fullmatch(r"\d+", values["PageCount"]) and int(values["PageCount"]) != page_count:
        errors.append(f"{where}: ComicInfo <PageCount> {values['PageCount']} but the archive has {page_count} pages")
    if values.get("Manga") and values["Manga"] not in MANGA_VALUES:
        errors.append(f"{where}: ComicInfo <Manga> must be one of {sorted(MANGA_VALUES)}")
    if values.get("Genre") and any(not part.strip() for part in values["Genre"].split(",")):
        errors.append(f"{where}: ComicInfo <Genre> has an empty item")
    return values


def check_archive(path, series_folder, errors):
    where = f"{series_folder}/{path.name}"
    try:
        archive = zipfile.ZipFile(path)
    except zipfile.BadZipFile as error:
        errors.append(f"{where}: not a ZIP archive ({error})")
        return None
    with archive:
        bad = archive.testzip()
        if bad:
            errors.append(f"{where}: corrupt archive member {bad}")
        names = archive.namelist()
        nested = [name for name in names if "/" in name.rstrip("/") or name.endswith("/")]
        if nested:
            errors.append(f"{where}: folders inside the archive (Mihon ignores them): {nested[:3]}")
        if "ComicInfo.xml" not in names:
            errors.append(f"{where}: no ComicInfo.xml at the archive root")
            return None
        pages = sorted((name for name in names if Path(name).suffix.lower() in IMAGES and "/" not in name), key=natural_key)
        if not pages:
            errors.append(f"{where}: no page images")
        if pages != sorted(pages):
            errors.append(f"{where}: natural and plain page order differ (pad page numbers)")
        for page in pages:
            head = archive.open(page).read(16)
            if image_kind(head) is None:
                errors.append(f"{where}: {page} is not an image")
        others = [name for name in names if name not in pages and name != "ComicInfo.xml"]
        if others:
            errors.append(f"{where}: unexpected archive members {others[:3]}")
        return check_comicinfo(archive.read("ComicInfo.xml"), where, errors, len(pages))


def check_series(folder, errors, warnings):
    name = folder.name
    check_component(name, name, errors)
    entries = [child for child in folder.iterdir() if not child.name.startswith(".")]
    archives = sorted((child for child in entries if child.is_file() and child.suffix.lower() in ARCHIVES), key=lambda p: natural_key(p.name))
    if not archives:
        errors.append(f"{name}: no chapter archives")
    covers = [child for child in entries if child.is_file() and child.stem.lower() == "cover"]
    cover = folder / "cover.jpg"
    if not cover.is_file():
        errors.append(f"{name}: cover.jpg is missing")
    elif image_kind(cover.read_bytes()[:16]) != "jpeg":
        errors.append(f"{name}: cover.jpg is not a JPEG image")
    if len(covers) > 1:
        warnings.append(f"{name}: several cover files; Mihon uses the first image named cover.*")
    details_title = None
    jsons = [child for child in entries if child.is_file() and child.suffix.lower() == ".json"]
    if (folder / "ComicInfo.xml").is_file():
        warnings.append(f"{name}: series ComicInfo.xml present; Mihon uses it instead of details.json")
    elif jsons:
        try:
            details = json.loads(jsons[0].read_text(encoding="utf-8"))
            for key in ("title", "author", "artist", "description"):
                if key in details and not isinstance(details[key], str):
                    errors.append(f"{name}/{jsons[0].name}: {key} must be a string")
            if not isinstance(details.get("genre", []), list) or not all(isinstance(g, str) for g in details.get("genre", [])):
                errors.append(f"{name}/{jsons[0].name}: genre must be a list of strings")
            if "status" in details and str(details["status"]) not in {str(code) for code in range(7)}:
                errors.append(f"{name}/{jsons[0].name}: status must be \"0\"..\"6\"")
            details_title = details.get("title")
            if not details_title:
                errors.append(f"{name}/{jsons[0].name}: title is empty")
        except (ValueError, UnicodeDecodeError) as error:
            errors.append(f"{name}/{jsons[0].name}: not valid UTF-8 JSON ({error})")
    else:
        warnings.append(f"{name}: no details.json; Mihon reads series details from the first archive")
    seen = {}
    series_values = set()
    chapters = 0
    for archive in archives:
        where = f"{name}/{archive.name}"
        check_component(archive.name, where, errors)
        values = check_archive(archive, name, errors)
        if values is None:
            continue
        chapters += 1
        series_values.add(values.get("Series", ""))
        stem = archive.stem
        special = values.get("Format") == "Special"
        if not special:
            number = float(values["Number"]) if re.fullmatch(r"-?\d+(\.\d+)?", values.get("Number", "")) else None
            chapter = KAVITA_CHAPTER.search(stem)
            if chapter is None:
                errors.append(f"{where}: Kavita finds no chapter marker in the file name")
            elif number is not None and kavita_number(chapter.group("c")) != kavita_number(values["Number"]):
                errors.append(f"{where}: Kavita reads chapter {chapter.group('c')} from the name but ComicInfo says {values['Number']}")
            volume = KAVITA_VOLUME.search(stem)
            if volume and values.get("Volume") and kavita_number(volume.group("v")) != float(values["Volume"]):
                errors.append(f"{where}: Kavita reads volume {volume.group('v')} from the name but ComicInfo says {values['Volume']}")
            if volume and not values.get("Volume"):
                errors.append(f"{where}: the name has a volume marker but ComicInfo has no <Volume>")
            if number is not None and abs(mihon_number(name, stem) - number) > 1e-6:
                errors.append(f"{where}: Mihon's chapter recognition reads {mihon_number(name, stem)} from the name, ComicInfo says {number}")
            if number is not None:
                if number in seen:
                    warnings.append(f"{where}: chapter {values['Number']} also in {seen[number]}")
                seen[number] = archive.name
        marker = re.search(r"(?:^|\s)(?:Vol\.\d|Ch\.\d)", stem)
        prefix = stem[:marker.start()].strip() if marker else ""
        if prefix and any(ch.isdigit() for ch in prefix):
            errors.append(f"{where}: digits before the volume/chapter marker; Kavita may read them as a number")
    if len(series_values) > 1:
        errors.append(f"{name}: archives disagree on ComicInfo <Series>: {sorted(series_values)}")
    if details_title and series_values and details_title not in series_values:
        errors.append(f"{name}: details.json title {details_title!r} differs from ComicInfo <Series> {sorted(series_values)}")
    return chapters


def check_library(root):
    errors, warnings = [], []
    root = Path(root)
    if not root.is_dir():
        return {"ok": False, "errors": [f"{root}: not a folder"], "warnings": [], "series": 0, "chapters": 0, "series_names": []}
    loose = [child.name for child in root.iterdir() if child.is_file() and not child.name.startswith(".")]
    if loose:
        errors.append(f"files at the library root (Kavita requires every series in its own folder): {loose[:5]}")
    series = sorted((child for child in root.iterdir() if child.is_dir() and not child.name.startswith(".")), key=lambda p: natural_key(p.name))
    keys = {}
    chapters = 0
    for folder in series:
        key = unicodedata.normalize("NFC", folder.name).casefold()
        if key in keys:
            errors.append(f"{folder.name}: same folder as {keys[key]} on a case-insensitive file system")
        keys[key] = folder.name
        chapters += check_series(folder, errors, warnings)
    return {"ok": not errors, "errors": errors, "warnings": warnings, "series": len(series), "chapters": chapters,
            "series_names": [folder.name for folder in series]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("library")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    parser.add_argument("--expect-series", type=int)
    parser.add_argument("--expect-chapters", type=int)
    args = parser.parse_args(argv)
    report = check_library(args.library)
    if args.expect_series is not None and report["series"] != args.expect_series:
        report["errors"].append(f"expected {args.expect_series} series, found {report['series']}")
    if args.expect_chapters is not None and report["chapters"] != args.expect_chapters:
        report["errors"].append(f"expected {args.expect_chapters} chapters, found {report['chapters']}")
    report["ok"] = not report["errors"]
    if args.json:
        print(json.dumps(report, indent=2, ensure_ascii=False))
    else:
        for line in report["errors"]:
            print("ERROR", line)
        for line in report["warnings"]:
            print("warning", line)
        print(f"{'OK' if report['ok'] else 'FAILED'}: {report['series']} series, {report['chapters']} chapters")
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
