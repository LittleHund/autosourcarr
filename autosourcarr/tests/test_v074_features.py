from __future__ import annotations

import ast
from pathlib import Path


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def function_text(path: str, name: str) -> str:
    text = (root() / path).read_text()
    for marker in (f"    def {name}", f"    async def {name}"):
        start = text.find(marker)
        if start >= 0:
            break
    assert start >= 0, name
    candidates = [x for x in (text.find("\n    def ", start + 5), text.find("\n    async def ", start + 5)) if x >= 0]
    end = min(candidates) if candidates else len(text)
    return text[start:end]


def test_v074_release_history_is_present():
    r = root()
    current = tuple(int(x) for x in (r / "VERSION").read_text().strip().split("."))
    assert current >= (0, 2, 74)
    changelog = (r / "CHANGELOG.md").read_text()
    assert "## 0.2.74 - 2026-08-29" in changelog
    assert "0.2.74 → 1.4.2074" in changelog

def test_manual_match_forces_ownership_instead_of_merging_entries():
    entrypoint = function_text("autosourcarr/app/service.py", "manual_match")
    assert "self._commit_manual_match(" in entrypoint
    # The accepted local decision now drains as one cancellation-safe worker.
    body = entrypoint + function_text("autosourcarr/app/service.py", "_commit_manual_match")
    assert "forced_from_entry_ids" in body
    assert "Manual selection is authoritative ownership" in body
    assert 'merge_entries(entry_id, existing_owner, "manual-match")' not in body
    claim = function_text("autosourcarr/app/service.py", "_claim_manual_source_record")
    assert "source_matches SET active=0" in claim
    assert "source_variants SET active=0" in claim
    assert "entry_id<>?" in claim
    upsert = function_text("autosourcarr/app/service.py", "_upsert_match")
    variant = function_text("autosourcarr/app/service.py", "_upsert_source_variant")
    assert 'if match_type == "manual"' in upsert and "_claim_manual_source_record" in upsert
    assert 'if match_type == "manual"' in variant and "_claim_manual_source_record" in variant


def test_merge_handles_inactive_unique_tombstone():
    body = function_text("autosourcarr/app/service.py", "_merge_entries_locked")
    assert 'SELECT * FROM source_matches WHERE entry_id=? AND source_id=?"' in body
    assert 'if conflict and not int(conflict["active"] or 0):' in body
    assert 'DELETE FROM source_matches WHERE id=?' in body


def test_manual_ui_reports_forced_move():
    js = (root() / "autosourcarr/app/static/app.js").read_text()
    assert "entry.forced_from_entry_ids" in js
    assert "Match moved from" in js


def test_v074_python_files_parse():
    ast.parse((root() / "autosourcarr/app/service.py").read_text())
    ast.parse((root() / "autosourcarr/app/main.py").read_text())
