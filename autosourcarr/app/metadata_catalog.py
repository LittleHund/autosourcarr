from __future__ import annotations

import asyncio
import html
import re
import time
import copy
import hashlib
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from dataclasses import dataclass
from typing import Any, Iterable

import httpx

from .config import settings
from .async_utils import gather_partial


_TAG_RE = re.compile(r"<[^>]+>")


def _plain(value: Any) -> str:
    text = html.unescape(str(value or ""))
    text = _TAG_RE.sub(" ", text)
    return re.sub(r"\s+", " ", text).strip()


def _first_text(values: Iterable[Any]) -> str:
    for value in values:
        text = _plain(value)
        if text:
            return text
    return ""


def _uniq(values: Iterable[Any]) -> list[str]:
    out: list[str] = []
    seen: set[str] = set()
    for value in values:
        text = _plain(value)
        key = text.casefold()
        if text and key not in seen:
            seen.add(key)
            out.append(text)
    return out


def _title_rows(primary: str, values: Iterable[tuple[Any, str, str]]) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    seen: set[str] = set()

    def add(value: Any, language: str = "", kind: str = "alias") -> None:
        text = _plain(value)
        key = text.casefold()
        if not text or key in seen:
            return
        seen.add(key)
        rows.append({"title": text, "language": str(language or ""), "type": kind})

    add(primary, "", "primary")
    for value, language, kind in values:
        add(value, language, kind)
    return rows


def _status(value: Any) -> str:
    raw = str(value or "").strip().casefold()
    if raw in {"releasing", "publishing", "current", "ongoing"}:
        return "ongoing"
    if raw in {"finished", "completed", "complete"}:
        return "completed"
    if raw in {"hiatus", "on_hiatus", "on hiatus"}:
        return "hiatus"
    if raw in {"cancelled", "canceled", "discontinued"}:
        return "cancelled"
    return "unknown"


@dataclass(frozen=True)
class ProviderSpec:
    key: str
    label: str
    identity_scope: str
    priority: int
    requires_key: bool = False


PROVIDER_SPECS: dict[str, ProviderSpec] = {
    "mangadex": ProviderSpec("mangadex", "MangaDex", "edition", 10),
    "anilist": ProviderSpec("anilist", "AniList", "work", 20),
    "mangaupdates": ProviderSpec("mangaupdates", "MangaUpdates", "edition", 30),
    "kitsu": ProviderSpec("kitsu", "Kitsu", "work", 40),
    "myanimelist": ProviderSpec("myanimelist", "MyAnimeList (Jikan)", "work", 50),
    "comicvine": ProviderSpec("comicvine", "Comic Vine", "edition", 60, True),
}


class MetadataCatalogClient:
    """Small normalized search layer over public comic/manga metadata catalogs.

    Providers are intentionally read-only. AutoSourcarr stores their IDs/titles as
    identity evidence; chapter acquisition still comes exclusively from configured
    Suwayomi/Mihon sources.
    """

    def __init__(self) -> None:
        self._sem = asyncio.Semaphore(max(1, int(settings.metadata_identity_concurrency)))
        self._client: httpx.AsyncClient | None = None
        self._cooldowns: dict[str, tuple[float, str]] = {}
        self._inflight = {}
        self._closing = False

    def _http_client(self) -> httpx.AsyncClient:
        if self._client is None or self._client.is_closed:
            self._client = httpx.AsyncClient(
                timeout=httpx.Timeout(max(1.0, float(settings.metadata_identity_timeout))),
                follow_redirects=True,
                limits=httpx.Limits(max_connections=12, max_keepalive_connections=6),
                headers={"Accept": "application/json", "User-Agent": "AutoSourcarr/0.2.85 (metadata identity; self-hosted)"},
            )
        return self._client

    async def close(self) -> None:
        self._closing=True
        owned=[row[0] for row in self._inflight.values()]
        for task in owned:task.cancel()
        if owned:await asyncio.gather(*owned,return_exceptions=True)
        self._inflight.clear()
        if self._client is not None:
            await self._client.aclose()
            self._client = None
        self._closing=False

    @staticmethod
    def _json(response: httpx.Response) -> dict[str, Any]:
        response.raise_for_status()
        payload = response.json()
        if not isinstance(payload, dict):
            raise ValueError("Metadata provider returned a non-object response")
        if payload.get("errors"):
            raise ValueError(f"Metadata provider reported an error: {str(payload['errors'])[:400]}")
        # Comic Vine signals application-level errors in a successful HTTP response.
        if "status_code" in payload and int(payload["status_code"]) != 1:
            raise ValueError(f"Metadata provider reported status {payload['status_code']}: {str(payload.get('error') or '')[:200]}")
        return payload

    def _check_cooldown(self, provider: str) -> None:
        expiry, reason = self._cooldowns.get(provider, (0.0, ""))
        remaining = expiry - time.monotonic()
        if remaining > 0:
            raise RuntimeError(f"{provider}: {reason}; retry in {max(1, int(remaining))}s")
        self._cooldowns.pop(provider, None)

    @staticmethod
    def _retry_delay(response: httpx.Response) -> float:
        value = str(response.headers.get("retry-after") or "").strip()
        try:
            delay = float(value)
        except ValueError:
            try:
                stamp = parsedate_to_datetime(value)
                if stamp.tzinfo is None:
                    stamp = stamp.replace(tzinfo=timezone.utc)
                delay = (stamp - datetime.now(timezone.utc)).total_seconds()
            except (ValueError, TypeError, OverflowError):
                delay = 60.0
        return max(5.0, min(3600.0, delay))

    def enabled_provider_keys(self) -> list[str]:
        requested = [
            x.strip().lower()
            for x in str(settings.metadata_identity_providers or "").split(",")
            if x.strip()
        ]
        out: list[str] = []
        for key in requested:
            if key not in PROVIDER_SPECS or key in out:
                continue
            if key == "comicvine" and not str(settings.comicvine_api_key or "").strip():
                continue
            out.append(key)
        return out

    def provider_status(self) -> list[dict[str, Any]]:
        requested = {
            x.strip().lower()
            for x in str(settings.metadata_identity_providers or "").split(",")
            if x.strip()
        }
        out: list[dict[str, Any]] = []
        for key, spec in PROVIDER_SPECS.items():
            configured = key in requested
            available = configured and (not spec.requires_key or bool(str(settings.comicvine_api_key or "").strip()))
            out.append(
                {
                    "key": key,
                    "name": spec.label,
                    "identity_scope": spec.identity_scope,
                    "priority": spec.priority,
                    "configured": configured,
                    "available": available,
                    "reason": "" if available else ("API key required" if configured and spec.requires_key else "disabled"),
                }
            )
        return out

    async def search_provider(self, provider: str, query: str, limit: int = 6, *, force_revision=False) -> list[dict[str, Any]]:
        provider = str(provider or "").strip().lower()
        query = " ".join(str(query or "").split()).strip()
        if not query or provider not in self.enabled_provider_keys():
            return []
        if self._closing:raise RuntimeError('Metadata owner is shutting down')
        policy=(bool(settings.metadata_identity_include_adult),str(settings.metadata_identity_providers),
                hashlib.sha256(str(settings.comicvine_api_key or '').encode()).digest())
        key=(provider,query,max(1,int(limit)),policy,force_revision)
        row=self._inflight.get(key)
        if row is None:
            if len(self._inflight)>=128:raise RuntimeError('Metadata request capacity deferred')
            task=asyncio.create_task(self._search_provider_owned(provider,query,limit))
            row=[task,0];self._inflight[key]=row
        row[1]+=1
        try:
            return copy.deepcopy(await asyncio.shield(row[0]))
        finally:
            row[1]-=1
            if row[1]==0:
                if not row[0].done():row[0].cancel()
                drain=asyncio.gather(row[0],return_exceptions=True)
                while not drain.done():
                    try:await asyncio.shield(drain)
                    except asyncio.CancelledError:continue
                drain.result()
                if self._inflight.get(key) is row:self._inflight.pop(key,None)

    async def _search_provider_owned(self,provider,query,limit):
        from .runtime_observability import metrics
        self._check_cooldown(provider)
        async with self._sem:
            # Requests queued behind an earlier 429 must respect its new cooldown.
            self._check_cooldown(provider)
            method = {
                "mangadex": self._search_mangadex, "anilist": self._search_anilist,
                "mangaupdates": self._search_mangaupdates, "kitsu": self._search_kitsu,
                "myanimelist": self._search_jikan, "comicvine": self._search_comicvine,
            }.get(provider)
            if method is None:
                return []
            try:
                with metrics.phase('metadata'):
                    return await method(self._http_client(), query, limit)
            except httpx.HTTPStatusError as exc:
                if exc.response.status_code in {429, 503}:
                    delay = self._retry_delay(exc.response)
                    self._cooldowns[provider] = (time.monotonic() + delay, f"HTTP {exc.response.status_code}")
                raise

    async def search(self, query: str, limit_per_provider: int = 6) -> dict[str, list[dict[str, Any]]]:
        keys = self.enabled_provider_keys()
        values = await gather_partial(
            (self.search_provider(key, query, limit_per_provider) for key in keys),
            timeout=max(float(settings.metadata_identity_timeout), float(settings.metadata_identity_overall_timeout)),
        )
        return {key: [] if isinstance(value, Exception) else list(value or []) for key, value in zip(keys, values)}

    @staticmethod
    def _result(provider: str, external_id: Any, **fields: Any) -> dict[str, Any]:
        spec = PROVIDER_SPECS[provider]
        result = {
            "provider": provider,
            "provider_name": spec.label,
            "identity_scope": spec.identity_scope,
            "provider_priority": spec.priority,
            "external_id": str(external_id or "").strip(),
            "url": "",
            "title": "",
            "titles": [],
            "description": "",
            "cover_url": "",
            "status": "unknown",
            "author": "",
            "artist": "",
            "genres": [],
            "media_type": "",
            "country": "",
            "year": None,
            "adult": False,
            "links": {},
            "raw": {},
        }
        result.update(fields)
        result["external_id"] = str(result.get("external_id") or "").strip()
        result["title"] = _plain(result.get("title"))
        result["description"] = _plain(result.get("description"))
        result["author"] = _plain(result.get("author"))
        result["artist"] = _plain(result.get("artist"))
        result["genres"] = _uniq(result.get("genres") or [])
        return result

    async def _search_mangadex(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        params: list[tuple[str, str]] = [
            ("title", query),
            ("limit", str(max(1, min(20, int(limit))))),
            ("includes[]", "author"),
            ("includes[]", "artist"),
            ("includes[]", "cover_art"),
            ("order[relevance]", "desc"),
        ]
        ratings = ["safe", "suggestive", "erotica"]
        if bool(settings.metadata_identity_include_adult):
            ratings.append("pornographic")
        params.extend(("contentRating[]", value) for value in ratings)
        response = await client.get("https://api.mangadex.org/manga", params=params)
        response.raise_for_status()
        payload = self._json(response)
        out: list[dict[str, Any]] = []
        for row in payload.get("data") or []:
            attrs = row.get("attributes") or {}
            title_map = attrs.get("title") or {}
            primary = _first_text([title_map.get("en"), *title_map.values()])
            title_values: list[tuple[Any, str, str]] = []
            for lang, value in title_map.items():
                title_values.append((value, lang, "primary"))
            for block in attrs.get("altTitles") or []:
                if isinstance(block, dict):
                    title_values.extend((value, lang, "alias") for lang, value in block.items())
            desc_map = attrs.get("description") or {}
            description = _first_text([desc_map.get("en"), *desc_map.values()])
            authors: list[str] = []
            artists: list[str] = []
            cover_file = ""
            for rel in row.get("relationships") or []:
                rtype = str(rel.get("type") or "")
                rattrs = rel.get("attributes") or {}
                if rtype == "author":
                    authors.append(rattrs.get("name") or "")
                elif rtype == "artist":
                    artists.append(rattrs.get("name") or "")
                elif rtype == "cover_art":
                    cover_file = str(rattrs.get("fileName") or cover_file)
            tags: list[str] = []
            for tag in attrs.get("tags") or []:
                names = (tag.get("attributes") or {}).get("name") or {}
                tags.append(_first_text([names.get("en"), *names.values()]))
            links = dict(attrs.get("links") or {})
            cross: dict[str, str] = {}
            if links.get("al"):
                cross["anilist"] = str(links["al"])
            if links.get("mal"):
                cross["myanimelist"] = str(links["mal"])
            if links.get("mu"):
                cross["mangaupdates"] = str(links["mu"])
            if links.get("kt"):
                cross["kitsu"] = str(links["kt"])
            manga_id = str(row.get("id") or "")
            cover = f"https://uploads.mangadex.org/covers/{manga_id}/{cover_file}.512.jpg" if manga_id and cover_file else ""
            out.append(
                self._result(
                    "mangadex",
                    manga_id,
                    url=f"https://mangadex.org/title/{manga_id}" if manga_id else "",
                    title=primary,
                    titles=_title_rows(primary, title_values),
                    description=description,
                    cover_url=cover,
                    status=_status(attrs.get("status")),
                    author=", ".join(_uniq(authors)),
                    artist=", ".join(_uniq(artists)),
                    genres=tags,
                    media_type=str(attrs.get("publicationDemographic") or ""),
                    country=str(attrs.get("originalLanguage") or ""),
                    year=attrs.get("year"),
                    adult=str(attrs.get("contentRating") or "") == "pornographic",
                    links=cross,
                    raw=row,
                )
            )
        return out

    async def _search_anilist(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        gql = """
        query ($search: String!, $perPage: Int!) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, sort: [SEARCH_MATCH, POPULARITY_DESC]) {
              id idMal siteUrl format status countryOfOrigin isAdult genres synonyms chapters volumes
              startDate { year }
              title { romaji english native userPreferred }
              coverImage { extraLarge large medium }
              description(asHtml: false)
              tags { name isAdult rank }
              staff(perPage: 12) { edges { role node { name { full } } } }
            }
          }
        }
        """
        response = await client.post(
            "https://graphql.anilist.co",
            json={"query": gql, "variables": {"search": query, "perPage": max(1, min(20, int(limit)))}},
        )
        response.raise_for_status()
        media = (((self._json(response) or {}).get("data") or {}).get("Page") or {}).get("media") or []
        out: list[dict[str, Any]] = []
        for row in media:
            if row.get("isAdult") and not bool(settings.metadata_identity_include_adult):
                continue
            title = row.get("title") or {}
            primary = _first_text([title.get("english"), title.get("userPreferred"), title.get("romaji"), title.get("native")])
            title_values: list[tuple[Any, str, str]] = [
                (title.get("english"), "en", "primary"),
                (title.get("romaji"), "", "romanized"),
                (title.get("native"), "", "native"),
                (title.get("userPreferred"), "", "primary"),
            ]
            title_values.extend((x, "", "alias") for x in row.get("synonyms") or [])
            authors: list[str] = []
            artists: list[str] = []
            for edge in ((row.get("staff") or {}).get("edges") or []):
                role = str(edge.get("role") or "").casefold()
                name = (((edge.get("node") or {}).get("name") or {}).get("full") or "")
                if any(x in role for x in ("story", "original", "creator", "author")):
                    authors.append(name)
                if any(x in role for x in ("art", "illustration", "artist")):
                    artists.append(name)
            genres = list(row.get("genres") or [])
            genres.extend(
                str(t.get("name") or "")
                for t in row.get("tags") or []
                if (not t.get("isAdult") or bool(settings.metadata_identity_include_adult)) and int(t.get("rank") or 0) >= 60
            )
            mal_id = row.get("idMal")
            links = {"myanimelist": str(mal_id)} if mal_id else {}
            cover = row.get("coverImage") or {}
            out.append(
                self._result(
                    "anilist",
                    row.get("id"),
                    url=str(row.get("siteUrl") or ""),
                    title=primary,
                    titles=_title_rows(primary, title_values),
                    description=row.get("description") or "",
                    cover_url=_first_text([cover.get("extraLarge"), cover.get("large"), cover.get("medium")]),
                    status=_status(row.get("status")),
                    author=", ".join(_uniq(authors)),
                    artist=", ".join(_uniq(artists)),
                    genres=genres,
                    media_type=str(row.get("format") or "").lower(),
                    country=str(row.get("countryOfOrigin") or ""),
                    year=((row.get("startDate") or {}).get("year")),
                    adult=bool(row.get("isAdult")),
                    links=links,
                    raw=row,
                )
            )
        return out

    async def _search_mangaupdates(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        response = await client.post(
            "https://api.mangaupdates.com/v1/series/search",
            json={"search": query, "perpage": max(1, min(20, int(limit)))},
        )
        response.raise_for_status()
        payload = self._json(response) or {}
        rows = payload.get("results") or payload.get("data") or []
        out: list[dict[str, Any]] = []
        for wrapper in rows:
            row = wrapper.get("record") if isinstance(wrapper, dict) and isinstance(wrapper.get("record"), dict) else wrapper
            if not isinstance(row, dict):
                continue
            sid = row.get("series_id") or row.get("id")
            primary = _first_text([row.get("title"), row.get("name")])
            aliases = row.get("associated") or row.get("associated_names") or row.get("alt_titles") or []
            if isinstance(aliases, dict):
                aliases = list(aliases.values())
            if isinstance(aliases, str):
                aliases = [aliases]
            image = row.get("image") or {}
            if isinstance(image, dict):
                cover = _first_text([image.get("url"), image.get("original"), image.get("thumb_url"), image.get("thumbnail")])
            else:
                cover = _plain(image)
            genres = row.get("genres") or []
            if genres and isinstance(genres[0] if isinstance(genres, list) else None, dict):
                genres = [x.get("genre") or x.get("name") or "" for x in genres]
            adult = any("adult" in str(x).casefold() or "hentai" in str(x).casefold() for x in (genres if isinstance(genres, list) else []))
            if adult and not bool(settings.metadata_identity_include_adult):
                continue
            out.append(
                self._result(
                    "mangaupdates",
                    sid,
                    url=str(row.get("url") or (f"https://www.mangaupdates.com/series/{sid}" if sid else "")),
                    title=primary,
                    titles=_title_rows(primary, [(x, "", "alias") for x in aliases]),
                    description=row.get("description") or row.get("summary") or "",
                    cover_url=cover,
                    status=_status(row.get("status")),
                    author=_first_text([row.get("author"), row.get("authors")]),
                    artist=_first_text([row.get("artist"), row.get("artists")]),
                    genres=genres if isinstance(genres, list) else [],
                    media_type=str(row.get("type") or "").lower(),
                    year=row.get("year"),
                    adult=adult,
                    raw=row,
                )
            )
        return out

    async def _search_kitsu(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        response = await client.get(
            "https://kitsu.io/api/edge/manga",
            params={"filter[text]": query, "page[limit]": str(max(1, min(20, int(limit))))},
            headers={"Accept": "application/vnd.api+json"},
        )
        response.raise_for_status()
        out: list[dict[str, Any]] = []
        for row in (self._json(response) or {}).get("data") or []:
            attrs = row.get("attributes") or {}
            nsfw = bool(attrs.get("nsfw"))
            if nsfw and not bool(settings.metadata_identity_include_adult):
                continue
            titles = attrs.get("titles") or {}
            primary = _first_text([attrs.get("canonicalTitle"), titles.get("en"), titles.get("en_jp"), *titles.values()])
            values: list[tuple[Any, str, str]] = [(v, k, "alias") for k, v in titles.items()]
            values.extend((x, "", "alias") for x in attrs.get("abbreviatedTitles") or [])
            poster = attrs.get("posterImage") or {}
            start_date = str(attrs.get("startDate") or "")
            year = int(start_date[:4]) if len(start_date) >= 4 and start_date[:4].isdigit() else None
            out.append(
                self._result(
                    "kitsu",
                    row.get("id"),
                    url=str((row.get("links") or {}).get("self") or ""),
                    title=primary,
                    titles=_title_rows(primary, values),
                    description=attrs.get("description") or attrs.get("synopsis") or "",
                    cover_url=_first_text([poster.get("original"), poster.get("large"), poster.get("medium"), poster.get("small")]),
                    status=_status(attrs.get("status")),
                    genres=[],
                    media_type=str(attrs.get("subtype") or "").lower(),
                    year=year,
                    adult=nsfw,
                    raw=row,
                )
            )
        return out

    async def _search_jikan(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        response = await client.get(
            "https://api.jikan.moe/v4/manga",
            params={"q": query, "limit": str(max(1, min(20, int(limit))))},
        )
        response.raise_for_status()
        out: list[dict[str, Any]] = []
        for row in (self._json(response) or {}).get("data") or []:
            genres = [x.get("name") or "" for x in (row.get("genres") or []) + (row.get("themes") or []) + (row.get("demographics") or []) + (row.get("explicit_genres") or [])]
            adult = bool(row.get("explicit_genres")) or any(str(x).casefold() in {"hentai", "erotica"} for x in genres)
            if adult and not bool(settings.metadata_identity_include_adult):
                continue
            primary = _first_text([row.get("title_english"), row.get("title"), row.get("title_japanese")])
            values: list[tuple[Any, str, str]] = []
            for item in row.get("titles") or []:
                values.append((item.get("title"), "", str(item.get("type") or "alias").lower()))
            values += [
                (row.get("title_english"), "en", "primary"),
                (row.get("title_japanese"), "ja", "native"),
            ]
            images = row.get("images") or {}
            jpg = images.get("jpg") or {}
            webp = images.get("webp") or {}
            authors = [x.get("name") or "" for x in row.get("authors") or []]
            published_from = str(((row.get("published") or {}).get("from") or ""))
            year = int(published_from[:4]) if len(published_from) >= 4 and published_from[:4].isdigit() else None
            out.append(
                self._result(
                    "myanimelist",
                    row.get("mal_id"),
                    url=str(row.get("url") or ""),
                    title=primary,
                    titles=_title_rows(primary, values),
                    description=row.get("synopsis") or row.get("background") or "",
                    cover_url=_first_text([webp.get("large_image_url"), jpg.get("large_image_url"), jpg.get("image_url")]),
                    status=_status(row.get("status")),
                    author=", ".join(_uniq(authors)),
                    genres=genres,
                    media_type=str(row.get("type") or "").lower(),
                    year=year,
                    adult=adult,
                    raw=row,
                )
            )
        return out

    async def _search_comicvine(self, client: httpx.AsyncClient, query: str, limit: int) -> list[dict[str, Any]]:
        key = str(settings.comicvine_api_key or "").strip()
        if not key:
            return []
        response = await client.get(
            "https://comicvine.gamespot.com/api/search/",
            params={
                "api_key": key,
                "format": "json",
                "query": query,
                "resources": "volume",
                "limit": str(max(1, min(20, int(limit)))),
                "field_list": "id,name,deck,description,image,site_detail_url,start_year,publisher",
            },
        )
        response.raise_for_status()
        out: list[dict[str, Any]] = []
        for row in (self._json(response) or {}).get("results") or []:
            image = row.get("image") or {}
            publisher = row.get("publisher") or {}
            out.append(
                self._result(
                    "comicvine",
                    row.get("id"),
                    url=str(row.get("site_detail_url") or ""),
                    title=row.get("name") or "",
                    titles=_title_rows(row.get("name") or "", []),
                    description=row.get("deck") or row.get("description") or "",
                    cover_url=_first_text([image.get("super_url"), image.get("original_url"), image.get("medium_url")]),
                    status="unknown",
                    author="",
                    artist="",
                    genres=[publisher.get("name") or ""] if isinstance(publisher, dict) else [],
                    media_type="comic",
                    year=row.get("start_year"),
                    adult=False,
                    raw=row,
                )
            )
        return out


metadata_catalog = MetadataCatalogClient()
