"""Refuse cross-site browser writes before any admin or catalogue route runs.

An empty ADMIN_TOKEN leaves /api/admin open on the local network. Without this
guard, any web page the user visits could POST to body-less maintenance routes
(reset-database, full-reset, ...). Browsers always send Origin on writes and
Sec-Fetch-Site on modern engines; scripts, the Suwayomi JAR and internal tools
send neither and are unaffected. The Host/X-Forwarded-Host comparison is the
same rule the setup and repair routes already use behind reverse proxies.
"""
from __future__ import annotations

from urllib.parse import urlsplit

from starlette.responses import JSONResponse

SAFE_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})
PROTECTED_PREFIXES = ("/api/admin/", "/api/v1/")
DETAIL = "Open this action from AutoSourcarr itself"


def _header(headers, name: bytes) -> str:
    for key, value in headers:
        if key.lower() == name:
            return value.decode("latin-1", errors="replace").strip()
    return ""


def cross_site_write(method: str, path: str, headers) -> bool:
    """True when a browser write to a protected path comes from another origin."""
    if method.upper() in SAFE_METHODS or not path.startswith(PROTECTED_PREFIXES):
        return False
    if _header(headers, b"sec-fetch-site").lower() == "cross-site":
        return True
    origin = _header(headers, b"origin")
    if not origin:
        return False
    if origin.lower() == "null":
        return True
    try:
        netloc = urlsplit(origin).netloc.lower()
    except ValueError:
        return True
    forwarded = _header(headers, b"x-forwarded-host").split(",")[0].strip().lower()
    hosts = {_header(headers, b"host").lower(), forwarded} - {""}
    return not netloc or netloc not in hosts


class CrossSiteWriteMiddleware:
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope.get("type") == "http" and cross_site_write(
                str(scope.get("method") or "GET"), str(scope.get("path") or ""), scope.get("headers", [])):
            return await JSONResponse({"detail": DETAIL}, status_code=403)(scope, receive, send)
        return await self.app(scope, receive, send)
