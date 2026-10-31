from __future__ import annotations

import hashlib
from typing import Any

from .image_safety import open_raster


def exact_thumbnail_fingerprint(content: bytes) -> dict[str, Any]:
    """Return an exact decoded-pixel fingerprint for a real thumbnail.

    This is intentionally *not* perceptual matching. Two thumbnails only match when
    they decode to the same dimensions and the same RGBA pixels. Container metadata,
    JPEG/PNG encoding details and EXIF chunks do not matter, but resizing/cropping/
    recompression that changes any decoded pixel does. This makes the fingerprint
    suitable as strong identity evidence without broad image-similarity false hits.
    """
    if not content:
        raise ValueError("empty thumbnail")
    with open_raster(content) as image:
        # Covers smaller than this are commonly icons/placeholders and should never
        # be allowed to merge works on their own.
        width, height = int(image.width), int(image.height)
        if width < 96 or height < 96:
            raise ValueError("thumbnail is too small for identity matching")
        frame = image.convert("RGBA")
        pixels = frame.tobytes()
        digest = hashlib.sha256()
        digest.update(f"RGBA:{width}x{height}:".encode("ascii"))
        digest.update(pixels)
        return {
            "fingerprint": digest.hexdigest(),
            "width": width,
            "height": height,
        }
