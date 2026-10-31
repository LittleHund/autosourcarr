import io
import os
import tempfile

from PIL import Image

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ["SOURCE_LANGUAGES"] = "en,all"

from app.color_profile import _classify_samples, _inspect_color


def _image_bytes(color):
    image = Image.new("RGB", (120, 160), color)
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    return buf.getvalue()


def _multicolor_bytes():
    image = Image.new("RGB", (120, 160), (240, 240, 240))
    palette = [(210, 40, 60), (40, 150, 220), (60, 180, 80), (220, 150, 40)]
    for i, color in enumerate(palette):
        for x in range(i * 30, (i + 1) * 30):
            for y in range(160):
                image.putpixel((x, y), color)
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    return buf.getvalue()


def test_page_color_classifier_separates_real_color_from_grayscale():
    colored = _inspect_color(_multicolor_bytes())
    gray = _inspect_color(_image_bytes((120, 120, 120)))
    assert colored["colored_fraction"] > gray["colored_fraction"]
    mode, confidence, _ = _classify_samples([colored, colored, colored, gray])
    assert mode == "colored"
    assert confidence >= 0.78
    mode, confidence, _ = _classify_samples([gray, gray, gray, gray])
    assert mode == "monochrome"
    assert confidence >= 0.78
