from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
STATIC = ROOT / "app" / "static"


def _block(source: str, start: str, end: str) -> str:
    left = source.index(start)
    right = source.index(end, left)
    return source[left:right]


def test_status_storage_has_own_panel_and_metrics_are_not_in_catalog_review():
    html = (STATIC / "index.html").read_text(encoding="utf-8")
    js = (STATIC / "app.js").read_text(encoding="utf-8")
    assert '<h3>Storage</h3>' in html
    assert 'id="statusStorage"' in html
    catalog = _block(js, "$('#statusCatalog').innerHTML=[", "$('#statusStorage').innerHTML=[")
    storage = _block(js, "$('#statusStorage').innerHTML=[", "const latest=cache.latest_browse_refresh")
    for label in ("AutoSourcarr size", "Database size", "Downloaded size"):
        assert label not in catalog
        assert f"simpleMetric('{label}'" in storage


def test_browse_metadata_heartbeat_patches_cards_without_rebuilding_browse_grid():
    js = (STATIC / "app.js").read_text(encoding="utf-8")
    patch = _block(js, "function patchCompactSummary", "async function refreshSparseEntryMetadata")
    heartbeat = _block(js, "async function refreshSparseEntryMetadata", "function scheduleMetadataLivePoll")
    assert "patchBrowseCardNode(browseCard,browseEntry)" in patch
    assert "['matrix','subbed','imports','duplicates']" in heartbeat
    assert "['matrix','subbed','browse','imports','duplicates']" not in heartbeat


def test_browse_reload_uses_keyed_in_place_patch_and_viewport_anchor():
    js = (STATIC / "app.js").read_text(encoding="utf-8")
    render = _block(js, "function browseCardHtml", "async function loadBrowse")
    assert "function patchBrowseCardNode" in render
    assert "function captureBrowseViewportAnchor" in render
    assert "function restoreBrowseViewportAnchor" in render
    assert "const sameOrder=" in render
    assert "items.forEach((e,index)=>patchBrowseCardNode(current[index],e))" in render


def test_browse_css_reserves_stable_geometry_and_scrollbar():
    css = (STATIC / "style.css").read_text(encoding="utf-8")
    tail = css[css.rfind("/* v0.2.65"):]
    assert "scrollbar-gutter:stable" in tail
    assert ".browseCard{content-visibility:visible!important" in tail
    assert ".browseGrid{overflow-anchor:none}" in tail
    assert "-webkit-line-clamp:2" in tail
    assert "-webkit-line-clamp:6" in tail
