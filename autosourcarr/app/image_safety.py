"""Bound untrusted raster decoding before full pixel allocation."""
from contextlib import contextmanager
import io
from PIL import Image

MAX_IMAGE_BYTES = 32 * 1024 * 1024
MAX_IMAGE_PIXELS = 40_000_000
RASTER_FORMATS = ("JPEG", "PNG", "WEBP", "GIF", "AVIF", "BMP")

@contextmanager
def open_raster(content: bytes, *, max_pixels: int | None = None):
    max_pixels = MAX_IMAGE_PIXELS if max_pixels is None else min(MAX_IMAGE_PIXELS, max_pixels)
    if not content or len(content) > MAX_IMAGE_BYTES:
        raise ValueError("Image is empty or exceeds the 32 MiB analysis limit")
    with Image.open(io.BytesIO(content), formats=list(RASTER_FORMATS)) as image:
        if image.width < 1 or image.height < 1 or image.width * image.height > max_pixels:
            raise ValueError("Image exceeds the analysis limit for decoded pixels (at most 40 megapixels)")
        yield image


open_sample_image = open_raster
