"""Local source-filtered catalogue views; never visit a source to inspect saved data."""
from __future__ import annotations

from .db import db
from .matcher import normalize_title


def source_entry_ids(source_id: str, *, search: str = "", limit: int = 500,
                     offset: int = 0, restrict_ids: list[int] | None = None,
                     min_matches: int = 0, exact_match_count: int | None = None,
                     sort: str = "relevance") -> list[int]:
    """Apply source membership and search before pagination, including variants.

    A None restriction means the full catalogue; an empty subscription snapshot
    means no entries. Source IDs stay strings (some exceed JavaScript's safe ints).
    """
    if restrict_ids is not None and not restrict_ids:
        return []
    terms = ["e.id IN (SELECT entry_id FROM linked)"]
    params: list = [str(source_id), str(source_id)]
    query = str(search or "").strip()
    if query:
        normalized = normalize_title(query)
        if not normalized:
            return []
        raw = "%" + query.casefold() + "%"
        terms.append("""(lower(e.preferred_title) LIKE ? OR
            EXISTS (SELECT 1 FROM titles t WHERE t.entry_id=e.id AND t.normalized LIKE ?) OR
            EXISTS (SELECT 1 FROM source_matches sm WHERE sm.entry_id=e.id AND sm.active=1 AND lower(sm.source_title) LIKE ?) OR
            EXISTS (SELECT 1 FROM source_variants sv WHERE sv.entry_id=e.id AND sv.active=1 AND lower(sv.source_title) LIKE ?))""")
        params.extend((raw, "%" + normalized + "%", raw, raw))
    if restrict_ids is not None:
        wanted = list(dict.fromkeys(int(x) for x in restrict_ids))
        terms.append("e.id IN (" + ",".join("?" for _ in wanted) + ")")
        params.extend(wanted)
    count_needed = exact_match_count is not None or min_matches > 0 or sort == "sources"
    count_sql = """(SELECT COUNT(*) FROM (
        SELECT COALESCE(sc.name,sm.source_id),COALESCE(sc.base_url,'')
        FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
        WHERE sm.entry_id=e.id AND sm.active=1
        GROUP BY lower(COALESCE(sc.name,sm.source_id)),lower(COALESCE(sc.base_url,''))))"""
    if exact_match_count is not None:
        terms.append(count_sql + "=?")
        params.append(int(exact_match_count))
    elif min_matches > 0:
        terms.append(count_sql + ">=?")
        params.append(int(min_matches))
    order = "e.updated_at DESC,e.id DESC"
    if sort == "title":
        order = "e.preferred_title COLLATE NOCASE,e.id"
    elif sort == "sources" and count_needed:
        order = count_sql + " DESC,e.preferred_title COLLATE NOCASE,e.id"
    # Subbed order is significant and is restored after membership filtering.
    fetch_limit = max(1, int(limit))
    if restrict_ids is not None:
        fetch_limit = len(restrict_ids)
    with db.catalog() as c:
        result = [int(row[0]) for row in c.execute("""
            WITH linked AS (
                SELECT entry_id FROM source_matches WHERE source_id=? AND active=1
                UNION SELECT entry_id FROM source_variants WHERE source_id=? AND active=1
            ) SELECT e.id FROM entries e WHERE """ + " AND ".join(terms) +
            " ORDER BY " + order + " LIMIT ? OFFSET ?", (*params, fetch_limit, 0 if restrict_ids is not None else max(0, int(offset))))]
    if restrict_ids is not None:
        present = set(result)
        return [eid for eid in dict.fromkeys(restrict_ids) if eid in present][offset:offset + limit]
    return result


def source_browse(service, source_id: str, *, search: str = "", page: int = 1,
                  page_size: int = 100, min_matches: int = 0,
                  exact_match_count: int | None = None, sort: str = "relevance") -> dict:
    ids = source_entry_ids(source_id, search=search, limit=page_size + 1,
                           offset=(page - 1) * page_size, min_matches=min_matches,
                           exact_match_count=exact_match_count, sort=sort)
    return {"items": service._entry_summaries(ids[:page_size]), "page_size": page_size,
            "has_next_page": len(ids) > page_size, "source_filter": True,
            "source_id": str(source_id), "source_errors": [], "cache_hit": True,
            "catalog_fallback": True, "refreshing": False, "cataloged_entries": 0}
