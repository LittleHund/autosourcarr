import asyncio
import statistics
from typing import Any

try:
    import imagehash
except ImportError:  # pragma: no cover - Docker image installs it; allows lightweight self-tests
    imagehash = None
from .config import settings
from .db import db, now_iso
from .image_safety import open_raster
from .suwayomi import bounded_map, source_host
from .async_utils import run_blocking
from .runtime_observability import metrics

_decode_loop=None
_decode_slots=None


async def _decode(content):
    global _decode_loop,_decode_slots
    loop=asyncio.get_running_loop()
    if _decode_loop is not loop or _decode_slots is None:
        _decode_loop=loop;_decode_slots=asyncio.Semaphore(max(1,int(settings.quality_analysis_concurrency)))
    async with _decode_slots:
        return await run_blocking(_inspect_image,content)


class BrokenImage(ValueError):
    """An image was received but its decoder could not read it."""


def _inspect_image(content: bytes) -> tuple[int, int, int, str]:
    """Decode/hash one sample off the asyncio event loop."""
    try:
        with metrics.phase('quality_decode',len(content)), open_raster(content) as img:
            width, height = img.width, img.height
            with metrics.decoded_pixels(width*height):
                img.load()
                phash = str(imagehash.phash(img)) if imagehash is not None else ""
    except OSError as exc:
        raise BrokenImage('Received image could not be decoded') from exc
    return width, height, len(content), phash


async def analyze_candidate(candidate_id: int, sample_count: int = 5) -> dict[str, Any]:
    try:
        result=await _analyze_candidate(candidate_id,sample_count)
    except BaseException as exc:
        with db.catalog() as c:
            c.execute('INSERT OR REPLACE INTO quality_attempts VALUES(?,?,?,?,?,?)',
                (candidate_id,now_iso(),'deferred' if getattr(exc,'local_source_deferral',False) else 'incomplete',0,0,type(exc).__name__))
        raise
    with db.catalog() as c:
        c.execute('INSERT OR REPLACE INTO quality_attempts VALUES(?,?,?,?,?,?)',
            (candidate_id,now_iso(),result['outcome'],result['sampled_pages']-result['unobserved_pages'],result['unobserved_pages'],result.get('reason','')))
    return result


async def _analyze_candidate(candidate_id: int, sample_count: int = 5) -> dict[str, Any]:
    with db.origins() as o:
        candidate = o.execute(
            """SELECT cc.*,vc.entry_id FROM chapter_candidates cc
               JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE cc.id=?""",
            (candidate_id,),
        ).fetchone()
        if not candidate:
            raise RuntimeError("Candidate not found")
        candidate = dict(candidate)
        pages = db.rows(o.execute(
            """SELECT cp.page_index,cp.private_url,cc.source_manga_id,cc.source_chapter_index
               FROM candidate_pages cp JOIN chapter_candidates cc ON cc.id=cp.candidate_id
               WHERE cp.candidate_id=? ORDER BY cp.page_index""",
            (candidate_id,),
        ))

    # Quality comparison must work for alternatives too, not only the candidate
    # that the reader happened to select. Resolve/cache its page list on demand.
    if not pages:
        urls = await source_host.fetch_chapter_pages(int(candidate["source_chapter_id"]))
        chapter_index = int(candidate.get("source_chapter_index", -1))
        if chapter_index < 0:
            try:
                chapter_index = await source_host.chapter_index_for_id(
                    int(candidate["source_manga_id"]), int(candidate["source_chapter_id"])
                )
            except Exception:
                chapter_index = -2
        ts = now_iso()
        with db.origins() as o:
            o.execute("DELETE FROM candidate_pages WHERE candidate_id=?", (candidate_id,))
            if urls:
                o.executemany(
                    "INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(?,?,?,?)",
                    [(candidate_id, index, private_url, ts) for index, private_url in enumerate(urls)],
                )
            o.execute(
                "UPDATE chapter_candidates SET page_count=?,source_chapter_index=?,last_seen=? WHERE id=?",
                (len(urls), chapter_index, ts, candidate_id),
            )
        pages = [
            {
                "page_index": index,
                "private_url": url,
                "source_manga_id": candidate["source_manga_id"],
                "source_chapter_index": chapter_index,
            }
            for index, url in enumerate(urls)
        ]
    if not pages:
        raise RuntimeError("This source returned no pages for the candidate")

    count = len(pages)
    sample_count = max(2, min(12, int(sample_count)))
    indexes = sorted(set(round(i * (count - 1) / max(1, sample_count - 1)) for i in range(sample_count)))
    sample = [pages[i] for i in indexes]

    async def inspect_page(page: dict[str, Any]):
        if int(page.get("source_chapter_index", -1)) >= 0:
            content, _ = await source_host.fetch_page_image(
                page["source_manga_id"], page["source_chapter_index"], page["page_index"]
            )
        else:
            content, _ = await source_host.fetch_binary(page["private_url"])
        # Cancellation drains the real decoder thread before the sample lane is
        # released. A timed-out await cannot create unaccounted background decodes.
        return await _decode(content)

    # Quality checks are explicitly user-triggered. Two concurrent samples make
    # them much faster without producing a burst of page traffic against a source.
    sampled = await bounded_map(
        sample,
        inspect_page,
        limit=max(1, int(settings.quality_analysis_concurrency)),
        per_item_timeout=max(1.0, float(settings.quality_sample_timeout)),
        overall_timeout=max(1.0, float(settings.quality_overall_timeout)),
    )

    widths: list[int] = []
    heights: list[int] = []
    sizes: list[int] = []
    hashes_by_page: list[tuple[int, str]] = []
    broken = 0
    unobserved=0
    reasons=set()
    for page, value, error in sampled:
        if error or value is None:
            if isinstance(error,BrokenImage):broken+=1
            else:
                unobserved+=1
                reasons.add('deferred' if getattr(error,'local_source_deferral',False) else type(error).__name__ if error else 'unobserved')
            continue
        width, height, size, phash = value
        widths.append(width)
        heights.append(height)
        sizes.append(size)
        if phash:
            hashes_by_page.append((int(page["page_index"]), phash))
    # bounded_map completes out of order; preserve actual first/last sampled page
    # semantics for the stored perceptual hashes.
    hashes = [value for _, value in sorted(hashes_by_page)]

    if not widths:
        score = 0.0
        mw = mh = avg = 0
    else:
        mw = int(statistics.median(widths))
        mh = int(statistics.median(heights))
        avg = int(statistics.mean(sizes)) if sizes else 0
        megapixels = (mw * mh) / 1_000_000
        resolution = min(70.0, megapixels * 15.0)
        file_component = min(20.0, avg / 150_000)
        integrity = max(0.0, 10.0 - broken * 5.0)
        score = min(100.0, resolution + file_component + integrity)

    ts = now_iso()
    with db.origins() as o:
        if unobserved:
            previous=o.execute('SELECT analyzed_at FROM quality_samples WHERE candidate_id=?',(candidate_id,)).fetchone()
            score=candidate.get('quality_score')
            observed_at=previous[0] if previous else ''
        else:
            observed_at=ts
            o.execute("UPDATE chapter_candidates SET quality_score=? WHERE id=?", (score, candidate_id))
            o.execute(
            """INSERT INTO quality_samples(candidate_id,sampled_pages,broken_pages,median_width,median_height,avg_bytes,phash_first,phash_last,analyzed_at)
               VALUES(?,?,?,?,?,?,?,?,?) ON CONFLICT(candidate_id) DO UPDATE SET sampled_pages=excluded.sampled_pages,broken_pages=excluded.broken_pages,
               median_width=excluded.median_width,median_height=excluded.median_height,avg_bytes=excluded.avg_bytes,phash_first=excluded.phash_first,
               phash_last=excluded.phash_last,analyzed_at=excluded.analyzed_at""",
            (candidate_id, len(sample), broken, mw, mh, avg, hashes[0] if hashes else "", hashes[-1] if hashes else "", ts),
        )
    return {
        "candidate_id": candidate_id,
        "quality_score": round(score, 2) if score is not None else None,
        "complete":not unobserved,
        "outcome":'incomplete' if unobserved else 'complete',
        "unobserved_pages":unobserved,
        "reason":', '.join(sorted(reasons)),
        "analyzed_at":observed_at,
        "sampled_pages": len(sample),
        "broken_pages": broken,
        "median_width": mw,
        "median_height": mh,
        "avg_bytes": avg,
        "phash_first": hashes[0] if hashes else "",
        "phash_last": hashes[-1] if hashes else "",
    }
