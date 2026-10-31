from __future__ import annotations

import asyncio
from typing import Any

from .config import settings
from .db import db, now_iso
from .image_safety import open_raster
from .matcher import title_color_variant
from .suwayomi import bounded_map, source_host


def _inspect_color(content: bytes) -> dict[str, float]:
    """Estimate how much real chroma is present in one comic page.

    JPEG ringing and scanner tint can make grayscale pages technically RGB, so the
    detector counts only pixels with meaningful HSV saturation.  Pages are resized
    before inspection to keep CPU/memory bounded.
    """
    with open_raster(content) as img:
        rgb = img.convert("RGB")
        rgb.thumbnail((256, 256))
        hsv = rgb.convert("HSV")
        pixels = list(hsv.get_flattened_data()) if hasattr(hsv, "get_flattened_data") else list(hsv.getdata())
    if not pixels:
        return {"colored_fraction": 0.0, "mean_saturation": 0.0}
    # Ignore nearly black/white pixels; text/background should not dominate the
    # colorfulness decision. Saturation is 0..255 in Pillow HSV.
    eligible = 0
    colored = 0
    sat_total = 0
    hue_bins = [0] * 12
    for hue, sat, val in pixels:
        if 18 <= val <= 242:
            eligible += 1
            sat_total += sat
            if sat >= 30:
                colored += 1
                hue_bins[min(11, int(hue) * 12 // 256)] += 1
    denom = max(1, eligible)
    meaningful_hues = sum(1 for count in hue_bins if count / denom >= 0.01)
    return {
        "colored_fraction": colored / denom,
        "mean_saturation": (sat_total / denom) / 255.0,
        "hue_bins": float(meaningful_hues),
    }


def _classify_samples(samples: list[dict[str, float]]) -> tuple[str, float, float]:
    if not samples:
        return "unknown", 0.0, 0.0
    threshold = max(0.01, min(0.50, float(settings.color_detection_colored_pixel_fraction)))
    page_votes = []
    for sample in samples:
        fraction = float(sample.get("colored_fraction") or 0.0)
        mean_sat = float(sample.get("mean_saturation") or 0.0)
        hue_bins = int(sample.get("hue_bins") or 0)
        # A uniform sepia/scan tint may be saturated but is still effectively a
        # monochrome edition. True colored pages normally contain several hue
        # families across the interior art.
        colored_page = (fraction >= threshold and hue_bins >= 3) or (mean_sat >= 0.18 and hue_bins >= 2)
        page_votes.append(1 if colored_page else 0)
    colored_pages = sum(page_votes)
    ratio = colored_pages / max(1, len(page_votes))
    avg_fraction = sum(float(x.get("colored_fraction") or 0.0) for x in samples) / len(samples)
    if ratio >= 0.60:
        # Confidence rises with both page agreement and chroma amount.
        confidence = min(0.99, 0.60 + 0.30 * ratio + min(0.09, avg_fraction))
        return "colored", confidence, avg_fraction
    if ratio <= 0.20:
        confidence = min(0.99, 0.70 + 0.25 * (1.0 - ratio))
        return "monochrome", confidence, avg_fraction
    return "unknown", 0.45, avg_fraction


def get_source_color_profile(source_id: str, source_manga_id: int) -> dict[str, Any] | None:
    with db.catalog() as c:
        row = c.execute(
            "SELECT * FROM source_color_profiles WHERE source_id=? AND source_manga_id=?",
            (str(source_id), int(source_manga_id)),
        ).fetchone()
    return dict(row) if row else None


def save_source_color_profile(
    source_id: str,
    source_manga_id: int,
    mode: str,
    confidence: float,
    sampled_pages: int,
    evidence: str,
    colored_fraction: float = 0.0,
) -> dict[str, Any]:
    mode = mode if mode in {"colored", "monochrome", "unknown"} else "unknown"
    stamp = now_iso()
    with db.catalog() as c:
        c.execute(
            """INSERT INTO source_color_profiles(source_id,source_manga_id,color_mode,confidence,sampled_pages,evidence,colored_fraction,analyzed_at)
               VALUES(?,?,?,?,?,?,?,?)
               ON CONFLICT(source_id,source_manga_id) DO UPDATE SET
                 color_mode=excluded.color_mode,confidence=excluded.confidence,sampled_pages=excluded.sampled_pages,
                 evidence=excluded.evidence,colored_fraction=excluded.colored_fraction,analyzed_at=excluded.analyzed_at""",
            (str(source_id), int(source_manga_id), mode, float(confidence), int(sampled_pages), str(evidence), float(colored_fraction), stamp),
        )
    return {
        "source_id": str(source_id),
        "source_manga_id": int(source_manga_id),
        "color_mode": mode,
        "confidence": float(confidence),
        "sampled_pages": int(sampled_pages),
        "evidence": str(evidence),
        "colored_fraction": float(colored_fraction),
        "analyzed_at": stamp,
    }


async def analyze_source_color(
    source_id: str,
    source_manga_id: int,
    *,
    title: str = "",
    chapters: list[dict[str, Any]] | None = None,
    force: bool = False,
) -> dict[str, Any]:
    """Classify a source manga as colored/monochrome from title + chapter pages."""
    if not bool(settings.color_detection_enabled):
        return save_source_color_profile(source_id, source_manga_id, "unknown", 0.0, 0, "disabled")

    explicit = title_color_variant(title)
    if explicit != "unknown":
        return save_source_color_profile(source_id, source_manga_id, explicit, 0.995, 0, "explicit-title-marker")

    cached = get_source_color_profile(source_id, source_manga_id)
    if cached and not force and float(cached.get("confidence") or 0.0) >= max(0.0, float(settings.color_detection_min_confidence)):
        return cached

    # Prefer already-ingested chapter candidates. They avoid another manga-details
    # request and preserve the source's chapter ids/order.
    candidate_chapters: list[dict[str, Any]] = []
    with db.origins() as o:
        rows = o.execute(
            """SELECT source_chapter_id id,source_chapter_index,source_order,page_count,chapter_number,name
               FROM chapter_candidates WHERE source_id=? AND source_manga_id=? AND active=1
               ORDER BY CASE WHEN chapter_number>=0 THEN 0 ELSE 1 END, chapter_number DESC, source_order DESC LIMIT 12""",
            (str(source_id), int(source_manga_id)),
        ).fetchall()
        candidate_chapters = [dict(r) for r in rows]

    if not candidate_chapters:
        candidate_chapters = [dict(x) for x in (chapters or []) if x.get("id") is not None][:12]
    if not candidate_chapters and chapters is None:
        try:
            payload = await asyncio.wait_for(
                source_host.fetch_manga_and_chapters(int(source_manga_id), True, True),
                timeout=max(4.0, float(settings.color_detection_timeout) + 4.0),
            )
            fetched_manga = payload.get("manga") or {}
            explicit = title_color_variant(str(fetched_manga.get("title") or title or ""))
            if explicit != "unknown":
                return save_source_color_profile(source_id, source_manga_id, explicit, 0.995, 0, "explicit-title-marker")
            candidate_chapters = [dict(x) for x in (payload.get("chapters") or []) if x.get("id") is not None][:12]
        except Exception:
            candidate_chapters = []
    if not candidate_chapters:
        return save_source_color_profile(source_id, source_manga_id, "unknown", 0.0, 0, "no-chapters")

    # Pick a chapter with actual pages if known; otherwise try newest chapters first.
    candidate_chapters.sort(key=lambda x: (int(x.get("page_count") or x.get("pageCount") or 0) <= 0, -float(x.get("chapter_number") or x.get("chapterNumber") or -1)))
    chapter = candidate_chapters[0]
    chapter_id = int(chapter.get("id") or 0)
    if chapter_id <= 0:
        return save_source_color_profile(source_id, source_manga_id, "unknown", 0.0, 0, "no-chapter-id")

    try:
        urls = await asyncio.wait_for(
            source_host.fetch_chapter_pages(chapter_id),
            timeout=max(2.0, float(settings.color_detection_timeout)),
        )
    except Exception as exc:
        return save_source_color_profile(source_id, source_manga_id, "unknown", 0.0, 0, f"page-list-error: {type(exc).__name__}")
    if not urls:
        return save_source_color_profile(source_id, source_manga_id, "unknown", 0.0, 0, "no-pages")

    chapter_index = int(chapter["source_chapter_index"]) if chapter.get("source_chapter_index") is not None else -1
    if chapter_index < 0:
        try:
            chapter_index = await source_host.chapter_index_for_id(int(source_manga_id), chapter_id)
        except Exception:
            chapter_index = -2

    sample_count = max(2, min(6, int(settings.color_detection_sample_pages)))
    count = len(urls)
    # Avoid cover/title/end cards where a monochrome series may still use a colored
    # splash. Interior pages are much more representative of the edition.
    if count <= sample_count:
        indexes = list(range(count))
    else:
        indexes = sorted(set(max(0, min(count - 1, round((i + 1) * (count - 1) / (sample_count + 1)))) for i in range(sample_count)))

    async def inspect(index: int):
        if chapter_index >= 0:
            content, _ = await source_host.fetch_page_image(int(source_manga_id), chapter_index, int(index))
        else:
            content, _ = await source_host.fetch_binary(str(urls[index]), timeout=max(2.0, float(settings.color_detection_timeout)))
        return await asyncio.to_thread(_inspect_color, content)

    sampled = await bounded_map(
        indexes,
        inspect,
        limit=max(1, min(2, int(settings.quality_analysis_concurrency))),
        per_item_timeout=max(2.0, float(settings.color_detection_timeout)),
        overall_timeout=max(5.0, float(settings.color_detection_timeout) * max(1, min(3, len(indexes)))),
    )
    values = [value for _, value, error in sampled if not error and value]
    mode, confidence, colored_fraction = _classify_samples(values)
    return save_source_color_profile(
        source_id,
        source_manga_id,
        mode,
        confidence,
        len(values),
        f"chapter-pages:{chapter_id}",
        colored_fraction,
    )
