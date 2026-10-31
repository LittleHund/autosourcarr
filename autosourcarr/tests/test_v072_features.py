from __future__ import annotations

import ast
from pathlib import Path


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def _route_node(name: str):
    tree = ast.parse((root() / "autosourcarr/app/main.py").read_text())
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return node
    raise AssertionError(f"route {name} not found")


def test_background_scheduling_routes_execute_on_application_event_loop():
    # FastAPI runs ordinary `def` endpoints in a worker thread. Every route below
    # immediately calls a service scheduler that creates asyncio tasks, so keeping
    # any of them synchronous recreates `RuntimeError: no running event loop`.
    names = {
        "admin_redo_entry_automation",
        "admin_reject_import_item",
        "admin_refresh_feeds",
        "admin_refresh_metadata",
        "admin_discover_additional_catalog",
        "admin_run_automatic_duplicates",
    }
    for name in names:
        assert isinstance(_route_node(name), ast.AsyncFunctionDef), name


def test_redo_scheduler_captures_loop_before_constructing_task():
    text = (root() / "autosourcarr/app/service.py").read_text()
    start = text.index("    def schedule_entry_automation_redo")
    end = text.index("    async def snapshot_subbed_reset_seeds", start)
    body = text[start:end]
    assert "loop = asyncio.get_running_loop()" in body
    assert "runtime = loop.create_task(runner())" in body
    assert "runtime = asyncio.create_task(runner())" not in body


def test_v072_changelog_history_is_retained():
    changelog = (root() / "CHANGELOG.md").read_text()
    assert "## 0.2.72 - 2026-08-28" in changelog
    assert "no running event loop" in changelog
    assert "0.2.72 → 1.4.2072" in changelog
