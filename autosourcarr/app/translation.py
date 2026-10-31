import asyncio
import re
import threading
import unicodedata
from collections import OrderedDict
from typing import Iterable

import httpx
from unidecode import unidecode

from .config import settings
from .db import db, now_iso
from .async_utils import run_blocking
from .matcher import normalize_title


_HANGUL_RE = re.compile(r"[\uac00-\ud7af\u1100-\u11ff]")
_KANA_RE = re.compile(r"[\u3040-\u30ff\u31f0-\u31ff]")
_HAN_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")


def script_language(title: str) -> str:
    """Best-effort language hint from title script.

    Japanese kana and Korean Hangul are unambiguous. Han-only strings are kept as
    Chinese-ish (zh) unless other metadata later indicates Japanese. This is only
    used to decide which *search aliases* to generate, never as canonical metadata.
    """
    text = title or ""
    if _HANGUL_RE.search(text):
        return "ko"
    if _KANA_RE.search(text):
        return "ja"
    if _HAN_RE.search(text):
        return "zh"
    return "en" if any("LATIN" in unicodedata.name(c, "") for c in text if c.isalpha()) else ""


def transliteration(title: str) -> str:
    raw = (title or "").strip()
    if not raw:
        return ""
    value = " ".join(unidecode(raw).split()).strip()
    if not value or value.casefold() == raw.casefold():
        return ""
    return value


def infer_origin_languages(titles: Iterable[str], genre: str = "") -> list[str]:
    hints: list[str] = []
    for title in titles:
        lang = script_language(title)
        if lang in {"ja", "ko", "zh"} and lang not in hints:
            hints.append(lang)
    g = (genre or "").casefold()
    genre_hints = [
        ("manhwa", "ko"), ("korean", "ko"),
        ("manhua", "zh"), ("chinese", "zh"),
        ("manga", "ja"), ("japanese", "ja"),
    ]
    for needle, lang in genre_hints:
        if needle in g and lang not in hints:
            hints.append(lang)
    if hints:
        return hints
    return [x for x in settings.translation_target_languages_list if x in {"ja", "ko", "zh"}]


class TranslationClient:
    def __init__(self) -> None:
        self.builtin = settings.translation_mode == 'builtin' or (settings.translation_mode == 'auto' and settings.app_services_mode == 'integrated' and bool(settings.translation_api_url))
        self.base_url = '' if settings.translation_mode == 'off' else 'http://127.0.0.1:5000' if self.builtin else settings.translation_api_url.rstrip('/')
        self._sem = asyncio.Semaphore(max(1, int(settings.translation_concurrency)))
        # Generated aliases are reused heavily by manual matching and duplicate
        # scans. Keep a modest process-local hot set so repeated ranking does not
        # reopen SQLite for the same translation on every pass. SQLite remains the
        # durable source of truth across restarts.
        self._memory_cache: OrderedDict[tuple[str, str], str] = OrderedDict()
        self._memory_cache_limit = 4096
        self._memory_cache_lock = threading.RLock()
        limits = httpx.Limits(
            max_connections=max(2, int(settings.translation_concurrency) + 2),
            max_keepalive_connections=max(2, int(settings.translation_concurrency)),
            keepalive_expiry=max(5.0, float(settings.http_keepalive_expiry_seconds)),
        )
        transport = httpx.AsyncHTTPTransport(limits=limits, retries=max(0, int(settings.http_transport_retries)))
        self.client = (
            httpx.AsyncClient(
                timeout=settings.translation_timeout,
                limits=limits,
                transport=transport,
                follow_redirects=False,
            )
            if self.base_url else None
        )

    async def close(self) -> None:
        if self.client is not None:
            await self.client.aclose()

    @property
    def enabled(self) -> bool:
        return bool(self.base_url)

    def _remember(self, text: str, target: str, translated: str) -> None:
        key = (text, target)
        with self._memory_cache_lock:
            self._memory_cache[key] = translated
            self._memory_cache.move_to_end(key)
            while len(self._memory_cache) > self._memory_cache_limit:
                self._memory_cache.popitem(last=False)

    def _memory_value(self, text: str, target: str) -> str | None:
        key = (text, target)
        with self._memory_cache_lock:
            value = self._memory_cache.get(key)
            if value is not None:
                self._memory_cache.move_to_end(key)
            return value

    def cached(self, text: str, target: str) -> str:
        text = (text or "").strip()
        target = (target or "").strip().lower()
        if not text or not target:
            return ""
        key = (text, target)
        cached = self._memory_value(text, target)
        if cached is not None:
            return cached
        with db.catalog() as c:
            row = c.execute(
                "SELECT translated_text FROM translation_cache WHERE source_text=? AND target_lang=?",
                key,
            ).fetchone()
        value = str(row["translated_text"]) if row else ""
        if value:
            self._remember(text, target, value)
        return value

    def _persist_translation(self, text: str, target: str, source: str, translated: str) -> None:
        with db.catalog() as c:
            c.execute(
                """INSERT INTO translation_cache(source_text,source_lang,target_lang,translated_text,provider,updated_at)
                   VALUES(?,?,?,?,?,?)
                   ON CONFLICT(source_text,target_lang) DO UPDATE SET translated_text=excluded.translated_text,
                     source_lang=excluded.source_lang,provider=excluded.provider,updated_at=excluded.updated_at""",
                (text, source or "auto", target, translated, "libretranslate", now_iso()),
            )

    async def health(self) -> dict:
        if not self.enabled:
            return {"ok": False, "configured": False, "url": "", "error": "Translation service not configured"}
        try:
            if self.client is None:
                raise RuntimeError("Translation client is not configured")
            response = await self.client.get(f"{self.base_url}/languages", timeout=min(3.0, settings.translation_timeout))
            response.raise_for_status()
            langs = response.json()
            return {"ok": True, "configured": True, "url": self.base_url, "languages": len(langs) if isinstance(langs, list) else 0}
        except Exception as exc:
            return {"ok": False, "configured": True, "url": self.base_url, "error": str(exc)}

    async def translate(self, text: str, target: str, source: str = "auto") -> str:
        text = (text or "").strip()
        target = (target or "").strip().lower()
        if not text or not target:
            return ""
        cached = self._memory_value(text, target)
        if cached is None:
            cached = await run_blocking(self.cached, text, target)
        if cached:
            return cached
        if not self.enabled:
            return ""
        if self.builtin and settings.app_services_mode == "integrated":
            from .internal_services import internal_services
            if not internal_services.ensure("translation"):
                return ""  # model preparation never holds title matching open
        payload = {"q": text, "source": source or "auto", "target": target, "format": "text"}
        if settings.translation_api_key and not self.builtin:
            payload["api_key"] = settings.translation_api_key
        try:
            if self.client is None:
                return ""
            async with self._sem:
                response = await self.client.post(f"{self.base_url}/translate", json=payload)
                response.raise_for_status()
                body = response.json()
            translated = str(body.get("translatedText") or "").strip()
            if not translated or normalize_title(translated) == normalize_title(text):
                return ""
            await run_blocking(self._persist_translation, text, target, source, translated)
            self._remember(text, target, translated)
            return translated
        except Exception:
            return ""


translator = TranslationClient()
