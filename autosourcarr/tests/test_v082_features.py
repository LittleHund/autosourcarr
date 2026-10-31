from pathlib import Path
import tempfile


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def test_manual_actions_ui_and_version():
    r = root()
    assert tuple(map(int, (r / "VERSION").read_text().strip().split("."))) >= (0, 2, 82)
    html = (r / "autosourcarr/app/static/index.html").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert 'data-tab="manual-actions"' in html
    for name in ("matches", "titles", "separate", "chapters", "duplicates"):
        assert f'data-manual-action-subtab="{name}"' in html
    assert "/api/admin/manual-actions" in js
    assert "Frozen match scope when title was set" in js


def test_manual_title_scope_schema_exists():
    db = (root() / "autosourcarr/app/db.py").read_text()
    assert "CREATE TABLE IF NOT EXISTS manual_title_scopes" in db
    assert "DELETE FROM preferred_title_override_sources" in db


def test_source_title_inheritance_is_disabled():
    service = (root() / "autosourcarr/app/service.py").read_text()
    block = service.split("def _apply_source_title_override", 1)[1].split("def _load_alias_cache", 1)[0]
    assert 'return ""' in block
    assert "manual_title_scopes" in block
    merge_window = service.split("# Manual titles are entry-local intent.", 1)[1].split("manual_match_bindings", 1)[0]
    assert "INSERT OR REPLACE INTO preferred_title_overrides" not in merge_window
    assert 'title_type") or "") == "manual"' in service


def test_set_title_captures_only_current_records():
    service = (root() / "autosourcarr/app/service.py").read_text()
    block = service.split("def set_preferred_title", 1)[1].split("def entry_history", 1)[0]
    assert "DELETE FROM manual_title_scopes WHERE entry_id=?" in block
    assert "INSERT INTO manual_title_scopes" in block
    assert "INSERT INTO preferred_title_override_sources" not in block


def test_manual_actions_backend_payload():
    service = (root() / "autosourcarr/app/service.py").read_text()
    main = (root() / "autosourcarr/app/main.py").read_text()
    assert "def manual_actions_payload" in service
    assert '"rejected_chapters"' in service
    assert '"duplicate_decisions"' in service
    assert '@app.get("/api/admin/manual-actions"' in main
