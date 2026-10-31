from __future__ import annotations

import os
import re
import tempfile
import threading
import hashlib
from functools import wraps
from pathlib import Path

ENV_WRITE_LOCK = threading.RLock()


def env_write_locked(function):
    @wraps(function)
    def locked(*args, **kwargs):
        with ENV_WRITE_LOCK:
            return function(*args, **kwargs)
    return locked


ENV_KEY_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")

FILES = {
    "autosourcarr": ("AutoSourcarr", "autosourcarr.env"),
    "source_host": ("Source engine", "source-host.env"),
}

DESCRIPTIONS = {
    "AUTOSOURCARR_PUBLIC_URL": "External hostname used by the Tachiyomi/Mihon APK. Keep the normal UI behind Authelia; the APK uses the dedicated /api/reader/v1/ path and AutoSourcarr reader API token.",
    "AUTOSOURCARR_READER_API_TOKEN": "Dedicated secret for the external reader API. The Android APK sends it in X-AutoSourcarr-API-Key. Configure Authelia to bypass only /api/reader/v1/; AutoSourcarr itself rejects missing or wrong tokens on that path.",
    "AUTOSOURCARR_AUTHELIA_BASIC_USERNAME": "Deprecated in 0.2.68. The Android extension now uses AutoSourcarr's own reader API token instead of logging into Authelia as a service account.",
    "AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD": "Deprecated in 0.2.68. Kept only so existing env files remain loadable; it is no longer embedded in newly built APKs.",
    "AUTOSOURCARR_INTERNAL_URL": "Internal Docker/LAN URL used when building the Suwayomi JAR. Usually http://autosourcarr:8282 on the shared Docker network.",
    "MAIN_SUWAYOMI_URL": "Main Suwayomi instance that owns your library and downloads.",
    "MAIN_SUWAYOMI_DOWNLOADS_PATH": "Optional read-only container path to the main Suwayomi AutoSourcarr download folder (or its downloads/mangas root). Used to show the real downloaded-data size in Status.",
    "SOURCE_AUTO_CHALLENGES": "Enable supported extension CAPTCHA preferences and readiness-gated Cloudflare handling through the configured FlareSolverr. Manual WebView remains available. Restart after changing this option.",
    "SOURCE_ENGINE_MODE": "integrated runs the bundled engine in AutoSourcarr; external uses SOURCE_SUWAYOMI_URL.",
    "SOURCE_ENGINE_UID": "0 reuses the source data directory owner; set 568 for a TrueNAS apps dataset when required.",
    "SOURCE_ENGINE_GID": "0 reuses the source data directory group. Existing files are never recursively re-owned.",
    "SOURCE_SUWAYOMI_URL": "Used only in external mode. Integrated mode always connects to its loopback engine.",
    "TRANSLATION_API_URL": "Leave blank to disable translation. Integrated mode manages the internal endpoint automatically; a custom URL is used only in external mode. Translations enrich aliases and never auto-merge works.",
    "SOURCE_LANGUAGES": "Comma-separated source languages AutoSourcarr is allowed to discover.",
    "BROWSE_PAGE_SIZE": "Number of unified works shown on one AutoSourcarr Browse page.",
    "AUTO_MATCH_THRESHOLD": "Automatic whole-title similarity threshold. AutoSourcarr never allows this below 0.94; extra title length lowers the score instead of being ignored.",
    "FUZZY_REVIEW_THRESHOLD": "Lower score where a possible match is retained for review.",
    "AUTO_MISSING_SOURCE_DISCOVERY": "Automatically search enabled sources that are still missing from an entry, using the same expanded title search as the manual matcher but a stricter independent evidence gate before attaching anything.",
    "AUTO_MISSING_SOURCE_ENTRIES_PER_FEED": "Maximum recently discovered entries per Browse feed that are queued for automatic missing-source searches.",
    "AUTO_MISSING_SOURCE_SOURCE_LIMIT": "Maximum missing source variants searched for one entry during an automatic pass.",
    "SOURCE_RECOVERY_TIMEOUT_SECONDS": "Maximum elapsed time for one source recovery attempt, including search and verification. Completed sources are retained; budget-limited attempts remain retryable.",
    "SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS": "Time budget for each item in Repair unmatched or Search all Subbed. An incomplete item does not stall later entries; retry prioritizes sources not yet searched.",
    "DUPLICATE_AUTO_MERGE_MIN_EXACT_TITLES": "Automatic duplicate merging accepts this many exact real-title overlaps as direct proof. A strong summary may corroborate one compatible title but is never sufficient by itself.",
    "DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD": "Automatic duplicate summary corroboration threshold. 0.70 means 70%; summary similarity alone never authorizes a merge.",
    "DUPLICATE_AUTO_MERGE_MIN_SUMMARY_CHARS": "Ignore very short summaries for automatic duplicate merging until each normalized summary has at least this many characters.",
    "AUTOMATIC_DUPLICATE_MATCHING": "Automatically run the conservative duplicate evidence gate in the background. Colored/monochrome and manga/webtoon conflicts remain hard blockers.",
    "AUTOMATIC_DUPLICATE_MATCHING_DELAY_SECONDS": "Debounce before a safe background duplicate-merge pass starts.",
    "EXACT_THUMBNAIL_MATCHING": "Allow exact decoded-pixel thumbnail fingerprints as additional match evidence. This is cryptographic pixel equality, not perceptual image similarity.",
    "THUMBNAIL_FINGERPRINT_TTL_SECONDS": "How long an exact decoded-thumbnail fingerprint remains cached.",
    "THUMBNAIL_FINGERPRINT_TIMEOUT": "Maximum fetch time for one thumbnail used by exact-image matching.",
    "THUMBNAIL_FINGERPRINT_ENTRIES_PER_PASS": "Maximum catalog entries whose cover fingerprints are refreshed in one automatic duplicate pass.",
    "AUTHELIA_USER_HEADER": "Optional exact reverse-proxy header name containing the authenticated Authelia username. Leave blank to auto-detect common Remote-User/X-Forwarded-User variants.",
    "SETTINGS_CONFIG_DIR": "Container path where the persistent AutoSourcarr config directory is mounted for the Settings editor.",
    "PYTHONPATH": "Container Python module/dependency path. Usually leave this unchanged on TrueNAS.",
    "DATA_DIR": "Container path for AutoSourcarr runtime data, imports and cover cache. Databases use this too unless DATABASE_DIR is set.",
    "DATABASE_DIR": "Managed database location. Use the Database storage Move button so AutoSourcarr copies and verifies existing DBs instead of starting empty files.",
    "CONTINUOUS_CATALOG_DISCOVERY": "Continuously browse enabled source feeds in the low-priority background lane so the catalog keeps growing while AutoSourcarr runs.",
    "CONTINUOUS_CATALOG_INTERVAL_SECONDS": "Minimum delay between low-priority source-page crawl steps. The crawler also pauses for foreground traffic, Subbed rebuilds, imports and enrichment backlog.",
    "CONTINUOUS_CATALOG_MODES": "Comma-separated source feed modes for continuous catalog discovery. Supported values: LATEST,POPULAR.",
    "CONTINUOUS_CATALOG_BACKLOG_LIMIT": "Pause continuous crawling when queued metadata/identity/translation work reaches this count.",
    "MAX_SOURCE_CONCURRENCY": "Maximum parallel work inside one AutoSourcarr source operation.",
    "SOURCE_HOST_WORK_CONCURRENCY": "Global cap on network-heavy GraphQL work sent to the private source host.",
    "BACKGROUND_SOURCE_CONCURRENCY": "Private-source-host slots available to all background Tasks combined. Keep below SOURCE_HOST_WORK_CONCURRENCY so foreground work always has reserved capacity.",
    "BACKGROUND_TASK_CONCURRENCY": "Maximum long-running background task bodies allowed to actively use Python/SQLite at once. Extra tasks remain queued without consuming resources.",
    "IMPORT_RESOLVE_CONCURRENCY": "Maximum user-requested Resolve imported entry jobs allowed to run in parallel. AutoSourcarr caps the effective value to preserve source-host capacity for interactive browsing.",
    "METADATA_IDENTITY_ENABLED": "Enable the external metadata/identity layer before source-level matching and metadata fallback.",
    "METADATA_IDENTITY_PROVIDERS": "Comma-separated metadata providers in priority/search order: mangadex, anilist, mangaupdates, kitsu, myanimelist, comicvine.",
    "METADATA_IDENTITY_INCLUDE_ADULT": "Allow adult-rated metadata candidates. Needed for doujin/adult manga, manhwa and manhua coverage.",
    "METADATA_IDENTITY_TIMEOUT": "Per-provider metadata request timeout in seconds.",
    "METADATA_IDENTITY_OVERALL_TIMEOUT": "Overall timeout for one identity-resolution query batch.",
    "METADATA_IDENTITY_CONCURRENCY": "Maximum entries resolving external metadata identities concurrently.",
    "METADATA_IDENTITY_CACHE_TTL_SECONDS": "How long provider search results remain cached.",
    "METADATA_IDENTITY_MIN_SCORE": "Minimum strict title score for automatic external identity attachment; never configured below the built-in 0.94 floor.",
    "METADATA_IDENTITY_DESCRIPTION_THRESHOLD": "Summary corroboration threshold used only with a strong near-exact title.",
    "METADATA_IDENTITY_QUERY_LIMIT": "Maximum real/known title aliases tried per provider.",
    "METADATA_IDENTITY_RESULTS_PER_PROVIDER": "Maximum provider candidates evaluated per query.",
    "METADATA_IDENTITY_ON_INGEST": "Resolve external identity after a new source record enters the catalog.",
    "METADATA_IDENTITY_ENTRIES_PER_FEED": "Maximum Browse/feed entries queued for identity resolution in one pass.",
    "COMICVINE_API_KEY": "Optional Comic Vine API key. Comic Vine remains disabled when this is blank.",
    "COVER_FETCH_TIMEOUT": "Maximum upstream time for one uncached cover fetch.",
    "COVER_INLINE_WAIT": "How long an image request waits for an uncached cover before returning a temporary placeholder while the fetch continues in the background.",
    "COVER_FETCH_CONCURRENCY": "Maximum cover fetches running at once. Four is a responsive default without creating a large burst against the private source host.",
    "COLOR_DETECTION_ENABLED": "Keep colored and monochrome editions separate. Explicit title markers are used immediately; ambiguous matches are verified from sampled chapter pages when possible.",
    "COLOR_DETECTION_SAMPLE_PAGES": "Number of interior chapter pages sampled to classify an unlabeled source manga as colored or monochrome.",
    "COLOR_DETECTION_COLORED_PIXEL_FRACTION": "Minimum fraction of visibly saturated pixels for a sampled page to count as colored.",
    "COLOR_DETECTION_MIN_CONFIDENCE": "Minimum source color-profile confidence used to block or split automatic matches.",
    "COLOR_DETECTION_TIMEOUT": "Timeout in seconds for each color-detection source/page request.",
    "ADMIN_TOKEN": "Optional token protecting the /api/admin endpoints and settings UI.",
    "EXTENSION_STORES": "JSON array of extension repositories available to the private source host.",
    "FLARESOLVERR_URL": "External compatibility endpoint. The integrated image manages its own loopback solver and ignores this saved URL.",
    "MAX_SOURCES_IN_PARALLEL": "Private Suwayomi limit for simultaneously queried extension sources.",
    "WEB_UI_ENABLED": "Enable the private Suwayomi web interface.",
    "KCEF_ENABLED": "Enable Suwayomi's Chromium/KCEF support for sources that need a browser.",
}

SETTING_ORDER = {
    # Common user-facing controls first inside their section.
    "SOURCE_LANGUAGES": 10, "BROWSE_PAGE_SIZE": 20,
    "AUTOSOURCARR_INTERNAL_URL": 5, "AUTOSOURCARR_PUBLIC_URL": 10, "AUTOSOURCARR_READER_API_TOKEN": 11, "AUTOSOURCARR_AUTHELIA_BASIC_USERNAME": 12, "AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD": 13, "MAIN_SUWAYOMI_URL": 20, "SOURCE_SUWAYOMI_URL": 30,
    "AUTO_MATCH_THRESHOLD": 10, "FUZZY_REVIEW_THRESHOLD": 20,
    "AUTO_MISSING_SOURCE_DISCOVERY": 30, "AUTO_MISSING_SOURCE_ENTRIES_PER_FEED": 40, "AUTO_MISSING_SOURCE_SOURCE_LIMIT": 50,
    "DUPLICATE_AUTO_MERGE_MIN_EXACT_TITLES": 10,
    "DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD": 20,
    "DUPLICATE_AUTO_MERGE_MIN_SUMMARY_CHARS": 30,
    "COLOR_DETECTION_ENABLED": 10, "COLOR_DETECTION_SAMPLE_PAGES": 20, "COLOR_DETECTION_MIN_CONFIDENCE": 30,
    "BACKGROUND_DEDUP_ON_STARTUP": 40,
    "SETTINGS_CONFIG_DIR": 10, "DATA_DIR": 20, "DATABASE_DIR": 21, "MAIN_SUWAYOMI_DOWNLOADS_PATH": 30, "PYTHONPATH": 90,
}

SENSITIVE_KEYS = {
    "ADMIN_TOKEN",
    "AUTOSOURCARR_READER_API_TOKEN",
    "AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD",
    "COMICVINE_API_KEY",
    "TRANSLATION_API_KEY",
    "API_KEY",
    "PASSWORD",
    "KEY_STORE_PASSWORD",
    "KEY_PASSWORD",
}


def _label(key: str) -> str:
    return key.replace("_", " ").title().replace("Url", "URL").replace("Api", "API").replace("Sqlite", "SQLite")


def _group_for(file_id: str, key: str) -> str:
    if key in {'SOURCE_SUWAYOMI_URL','TRANSLATION_API_URL','TRANSLATION_API_KEY','REPAIR_WORKER_URL','REPAIR_WORKER_TOKEN','FLARESOLVERR_URL','AUTOSOURCARR_AUTHELIA_BASIC_USERNAME','AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD'}:
        return 'Advanced: external compatibility'
    if file_id == "source_host":
        if key in {"TZ", "AUTH_MODE", "UPDATE_INTERVAL"}:
            return "General"
        if key in {"BIND_IP", "BIND_PORT"}:
            return "Network"
        if key.startswith("EXTENSION_"):
            return "Extensions"
        if key.startswith("FLARESOLVERR_"):
            return "FlareSolverr"
        if key.startswith("WEB_UI_") or key == "KCEF_ENABLED":
            return "Web UI & browser"
        if key.startswith("AUTO_DOWNLOAD") or key.startswith("MAX_SOURCES"):
            return "Source runtime"
        if key.endswith("_DIR") or key.endswith("_PATH"):
            return "Paths"
        return "Advanced"

    if key in {"SETTINGS_CONFIG_DIR", "PYTHONPATH", "DATA_DIR", "MAIN_SUWAYOMI_DOWNLOADS_PATH"} or key.endswith("_DIR") or key.endswith("_PATH"):
        return "Paths"
    if key in {"TZ", "SOURCE_LANGUAGES", "BROWSE_PAGE_SIZE"}:
        return "General"
    if key in {"AUTOSOURCARR_INTERNAL_URL", "AUTOSOURCARR_PUBLIC_URL", "MAIN_SUWAYOMI_URL", "SOURCE_SUWAYOMI_URL", "TRANSLATION_API_URL", "TRANSLATION_API_KEY"}:
        return "Connections"
    if key in {"AUTO_MATCH_THRESHOLD", "FUZZY_REVIEW_THRESHOLD", "SOURCE_RECOVERY_TIMEOUT_SECONDS", "SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS"} or key.startswith("MANUAL_") or key.startswith("AUTO_MISSING_SOURCE_"):
        return "Matching"
    if key.startswith("DUPLICATE_") or key.startswith("BACKGROUND_DEDUP_") or key.startswith("AUTOMATIC_DUPLICATE_") or key.startswith("EXACT_THUMBNAIL_") or key.startswith("THUMBNAIL_FINGERPRINT_"):
        return "Duplicates"
    if key.startswith("COLOR_DETECTION_"):
        return "Color variants"
    if key.startswith("BROWSE_") or key.startswith("FEDERATED_") or key.startswith("INTERACTIVE_"):
        return "Browse"
    if key.startswith("METADATA_") or key.startswith("COVER_") or key == "COMICVINE_API_KEY":
        return "Metadata & covers"
    if key.startswith("AUTO_VARIANT_"):
        return "Additional source records"
    if key.startswith("TRANSLATION_") or key.startswith("BACKGROUND_TRANSLATION_"):
        return "Translation"
    if key.startswith("REFRESH_") or key.startswith("CHAPTER_") or key.startswith("PAGE_INFO_"):
        return "Chapters & refresh"
    if key.startswith("BACKGROUND_REFRESH_") or key.startswith("STARTUP_REFRESH_") or key.startswith("CONTINUOUS_CATALOG_"):
        return "Background jobs"
    if key in {"ADMIN_TOKEN", "AUTHELIA_USER_HEADER", "AUTOSOURCARR_READER_API_TOKEN", "AUTOSOURCARR_AUTHELIA_BASIC_USERNAME", "AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD"}:
        return "Security"
    if key.startswith("QUALITY_"):
        return "Quality analysis"
    if key.startswith("SOURCE_FAILURE_") or key.startswith("SOURCE_COOLDOWN_") or key.startswith("HTTP_") or key.startswith("SQLITE_") or key.startswith("SEARCH_CACHE_") or key.startswith("SUBBED_CACHE_") or key.startswith("BACKGROUND_") or key in {"MAX_SOURCE_CONCURRENCY", "SOURCE_HOST_WORK_CONCURRENCY", "IMPORT_RESOLVE_CONCURRENCY"}:
        return "Advanced performance"
    if key in {"PYTHONUNBUFFERED", "PYTHONDONTWRITEBYTECODE"}:
        return "Advanced runtime"
    return "Advanced"


def _kind(key: str, value: str) -> str:
    lower = value.strip().lower()
    if key in SENSITIVE_KEYS or key.endswith("_PASSWORD") or key.endswith("_TOKEN") or key.endswith("_API_KEY"):
        return "password"
    if lower in {"true", "false"}:
        return "boolean"
    if key.endswith("_URL"):
        return "url"
    if value.strip().startswith("[") or value.strip().startswith("{"):
        return "textarea"
    if re.fullmatch(r"-?\d+", value.strip()):
        return "integer"
    if re.fullmatch(r"-?(?:\d+\.\d*|\d*\.\d+)", value.strip()):
        return "number"
    return "text"


def parse_env(path: Path) -> tuple[list[tuple[str, str]], list[str]]:
    items: list[tuple[str, str]] = []
    errors: list[str] = []
    if not path.is_file():
        return items, [f"File does not exist: {path}"]
    seen: set[str] = set()
    for line_no, raw in enumerate(path.read_text("utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in raw:
            errors.append(f"Line {line_no}: expected KEY=value")
            continue
        key, value = raw.split("=", 1)
        key = key.strip()
        if not ENV_KEY_RE.fullmatch(key):
            errors.append(f"Line {line_no}: invalid key {key!r}")
            continue
        if key in seen:
            errors.append(f"Line {line_no}: duplicate key {key}")
            continue
        seen.add(key)
        items.append((key, value))
    return items, errors


@env_write_locked
def settings_payload(config_dir: Path) -> dict:
    from .config import settings
    internally_owned = {'APP_SERVICES_MODE', 'REPAIR_WORKER_URL', 'REPAIR_WORKER_TOKEN'} if settings.app_services_mode == 'integrated' else set()
    files = []
    for file_id, (title, filename) in FILES.items():
        path = config_dir / filename
        pairs, errors = parse_env(path)
        grouped: dict[str, list[dict]] = {}
        for key, value in pairs:
            if file_id == 'autosourcarr' and key in internally_owned:
                continue  # Retain legacy file values without presenting unused controls.
            group = _group_for(file_id, key)
            grouped.setdefault(group, []).append(
                {
                    "key": key,
                    "label": _label(key),
                    "value": value,
                    "kind": _kind(key, value),
                    "sensitive": _kind(key, value) == "password",
                    "description": DESCRIPTIONS.get(key, ""),
                }
            )
        group_order = {
            "General": 10, "Connections": 20, "Network": 20, "Paths": 30,
            "Matching": 40, "Duplicates": 50, "Browse": 60, "Metadata & covers": 70,
            "Additional source records": 80, "Translation": 90, "Chapters & refresh": 100,
            "Extensions": 40, "Source runtime": 50, "FlareSolverr": 60, "Web UI & browser": 70,
            "Background jobs": 110, "Security": 120, "Quality analysis": 130,
            "Advanced: external compatibility": 990, "Advanced performance": 900, "Advanced runtime": 910, "Advanced": 999,
        }
        for items in grouped.values():
            items.sort(key=lambda item: (SETTING_ORDER.get(item["key"], 500), item["label"]))
        ordered_groups = sorted(grouped.items(), key=lambda pair: (group_order.get(pair[0], 500), pair[0]))
        files.append(
            {
                "id": file_id,
                "title": title,
                "filename": filename,
                "revision": hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None,
                "path": str(path),
                "exists": path.is_file(),
                "writable": path.is_file() and os.access(path, os.W_OK),
                "errors": errors,
                "groups": [{"name": name, "items": items} for name, items in ordered_groups],
            }
        )
    return {
        "config_dir": str(config_dir),
        "files": files,
        "apply_note": "Save here, then restart this AutoSourcarr instance to apply all changes. Routine settings do not require editing Compose.",
    }


@env_write_locked
def update_env(path: Path, values: dict[str, str], *, revision: str | None = None) -> dict:
    if revision is not None and hashlib.sha256(path.read_bytes()).hexdigest() != revision:
        raise ValueError("Settings changed since this form was loaded. Reload before saving")
    pairs, errors = parse_env(path)
    if errors:
        raise ValueError("; ".join(errors))
    existing = {key for key, _ in pairs}
    unknown = sorted(set(values) - existing)
    if unknown:
        raise ValueError("Unknown environment variable(s): " + ", ".join(unknown))
    for key, value in values.items():
        if not ENV_KEY_RE.fullmatch(key):
            raise ValueError(f"Invalid environment key: {key}")
        if not isinstance(value, str):
            raise ValueError(f"Value for {key} must be text")
        if "\n" in value or "\r" in value or "\x00" in value:
            raise ValueError(f"Value for {key} may not contain newlines or NUL bytes")

    original = path.read_text("utf-8")
    rendered: list[str] = []
    for raw in original.splitlines():
        if "=" in raw and not raw.lstrip().startswith("#"):
            key = raw.split("=", 1)[0].strip()
            if key in values:
                rendered.append(f"{key}={values[key]}")
                continue
        rendered.append(raw)
    text = "\n".join(rendered) + ("\n" if original.endswith("\n") else "")

    path.parent.mkdir(parents=True, exist_ok=True)
    mode = path.stat().st_mode & 0o777
    fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(tmp_name, mode)
        os.replace(tmp_name, path)
    finally:
        if os.path.exists(tmp_name):
            os.unlink(tmp_name)

    _, after_errors = parse_env(path)
    if after_errors:
        raise ValueError("Saved env file failed validation: " + "; ".join(after_errors))
    return {"path": str(path), "updated": len(values), "restart_required": True}


def file_path(config_dir: Path, file_id: str) -> Path:
    try:
        return config_dir / FILES[file_id][1]
    except KeyError as exc:
        raise KeyError(file_id) from exc


@env_write_locked
def upsert_env_value(path: Path, key: str, value: str) -> dict:
    """Atomically set one release-managed env key, appending it when newly introduced."""
    if not ENV_KEY_RE.fullmatch(key):
        raise ValueError(f"Invalid environment key: {key}")
    if not isinstance(value, str) or "\n" in value or "\r" in value or "\x00" in value:
        raise ValueError(f"Invalid value for {key}")
    if not path.is_file():
        raise FileNotFoundError(path)
    original = path.read_text("utf-8")
    rendered = []
    found = False
    for raw in original.splitlines():
        if "=" in raw and not raw.lstrip().startswith("#") and raw.split("=", 1)[0].strip() == key:
            rendered.append(f"{key}={value}")
            found = True
        else:
            rendered.append(raw)
    if not found:
        if rendered and rendered[-1] != "":
            rendered.append("")
        rendered.append(f"{key}={value}")
    text = "\n".join(rendered) + "\n"
    mode = path.stat().st_mode & 0o777
    fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(tmp_name, mode)
        os.replace(tmp_name, path)
    finally:
        if os.path.exists(tmp_name):
            os.unlink(tmp_name)
    return {"path": str(path), "key": key, "restart_required": True, "added": not found}
