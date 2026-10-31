from pathlib import Path
import re


APP_JS = Path(__file__).resolve().parents[1] / "app" / "static" / "app.js"


def _function_block(source: str, start: str, end: str) -> str:
    left = source.index(start)
    right = source.index(end, left)
    return source[left:right]


def test_metadata_heartbeat_does_not_unconditionally_rebuild_active_surfaces():
    source = APP_JS.read_text()
    block = _function_block(source, "async function refreshSparseEntryMetadata()", "function scheduleMetadataLivePoll")
    assert "rerenderMatrixPreservingScroll('matrix')" not in block
    assert "rerenderMatrixPreservingScroll('subbed')" not in block
    assert "renderBrowse();" not in block
    assert "renderImportItems();" not in block
    assert "renderDuplicates();" not in block
    assert "requestSurfaceRender" in block


def test_background_entry_sync_never_reopens_or_rebuilds_an_inspector():
    source = APP_JS.read_text()
    block = _function_block(source, "async function syncEntrySnapshot", "function scheduleEntrySnapshotSync")
    assert "renderInlineEntry(" not in block
    assert "openBrowseEntry(" not in block
    assert "patchOpenInspectorSnapshot(entry)" in block
    assert "entryInspectorGeneration" in source
    assert "deferredUiRenders" in source


def test_async_inline_renderer_cannot_change_inspector_visibility():
    source = APP_JS.read_text()
    block = _function_block(source, "async function renderInlineEntry", "window.closeInlineEntry")
    assert "target.hidden=false" not in block
    assert "target.hidden=true" not in block
    assert "Never change target.hidden here" in block


def test_entry_open_and_close_are_owned_by_trusted_user_events():
    source = APP_JS.read_text()
    open_block = _function_block(source, "window.openInlineEntry=async", "function metadataIdentityHtml")
    close_block = _function_block(source, "window.closeInlineEntry=", "async function refreshInlineEntryStable")
    browse_open = _function_block(source, "window.openBrowseEntry=async", "$('#closeBrowseInspector')")
    browse_close = _function_block(source, "function closeBrowseEntry", "window.closeBrowseEntry=closeBrowseEntry")
    assert "userEvent?.isTrusted" in open_block
    assert "target.hidden=false" in open_block
    assert "userEvent?.isTrusted" in close_block
    assert "inspector.hidden=true" in close_block
    assert "userEvent?.isTrusted" in browse_open
    assert "userEvent?.isTrusted" in browse_close


def test_sources_page_has_installed_and_manage_subtabs():
    html = (Path(__file__).resolve().parents[1] / "app" / "static" / "index.html").read_text()
    source = APP_JS.read_text()
    assert re.search(r'data-source-subtab="installed"[^>]*>Installed', html)
    assert re.search(r'data-source-subtab="manage"[^>]*>Discover', html)
    assert 'id="sourceInstalledPanel"' in html
    assert 'id="sourceManagePanel"' in html
    assert 'id="updateAllSourceExtensions"' in html
    assert '/api/admin/source-extensions' in source
    assert '/api/admin/source-extensions/update-all' in source
    assert "manageSourceExtension" in source
    assert "syncSourceRowsToMatrices" in source


def test_manage_sources_uses_collapsible_update_installed_available_sections_and_icons():
    root = Path(__file__).resolve().parents[1] / "app" / "static"
    html = (root / "index.html").read_text()
    source = APP_JS.read_text()
    css = (root / "style.css").read_text()
    assert 'id="sourceExtensionUpdatesSection"' in html
    assert 'data-source-section="installed"' in html
    assert 'data-source-section="available"' in html
    assert 'id="sourceExtensionUpdatesList"' in html
    assert 'id="sourceExtensionInstalledList"' in html
    assert 'id="sourceExtensionAvailableList"' in html
    assert '/api/v1/source-extension-icon/' in source
    assert '/api/v1/source-icon/' in source
    assert 'sourceExtensionIconWrap' in source
    assert '.sourceSubtab{' in css
    assert 'border-radius:7px' in css[css.rfind('/* v0.2.56'):]


def test_theme_and_server_user_preferences_are_present():
    root = Path(__file__).resolve().parents[1] / "app" / "static"
    html = (root / "index.html").read_text()
    source = APP_JS.read_text()
    css = (root / "style.css").read_text()
    for theme in ("blue", "amber", "emerald", "violet", "rose", "cyan"):
        assert f'data-theme="{theme}"' in html
    assert '/api/admin/ui-preferences' in source
    assert "queueUiPreferenceSave('theme'" in source
    assert "queueUiPreferenceSave('browsePageSize'" in source
    assert "queueUiPreferenceSave('duplicateScope'" in source
    assert "queueUiPreferenceSave('sourcesSubtab'" in source
    assert "queueUiPreferenceSave('sourceSections'" in source
    assert '[data-theme=emerald]' in (root / 'workbench.css').read_text()
    assert '/static/ui.css?v=' in html
    assert '[data-theme=emerald]' in (root / 'ui.css').read_text()
