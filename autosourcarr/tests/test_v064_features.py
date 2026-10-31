import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.config import settings
from app.service import AutoSourcarrService


def test_builder_publishes_both_suwayomi_jar_and_tachiyomi_mihon_apk():
    root = Path(__file__).resolve().parents[2]
    builder = (root / "extension/build-in-container.sh").read_text(encoding="utf-8")
    validator = (root / "extension/verify-tachiyomi-apk.sh").read_text(encoding="utf-8")
    assert 'build/outputs/jar/release' in builder
    assert 'build/outputs/apk/release' in builder
    # Paired-build runtime tests verify each concrete normal/private filename.
    assert 'ARTIFACT_NAME=AutoSourcarr' in builder
    assert 'ARTIFACT_NAME=AutoSourcarr-private' in builder
    assert '${ARTIFACT_NAME}-v${AUTOSOURCARR_VERSION}-Suwayomi.jar' in builder
    assert '${ARTIFACT_NAME}-v${AUTOSOURCARR_VERSION}-Tachiyomi-Mihon.apk' in builder
    assert 'verify-suwayomi-jar.sh' in builder
    assert 'verify-tachiyomi-apk.sh' in builder
    assert 'AndroidManifest.xml' in validator
    assert 'classes([0-9]+)?\\.dex' in validator
    assert 'resources.arsc' in validator


def test_status_storage_reports_database_separately_and_excludes_download_mount(monkeypatch, tmp_path):
    data = tmp_path / "data"
    config = tmp_path / "config"
    downloads = data / "downloads"
    data.mkdir(); config.mkdir(); downloads.mkdir()
    (data / "catalog.sqlite").write_bytes(b"c" * 101)
    (data / "catalog.sqlite-wal").write_bytes(b"w" * 17)
    (data / "chapter-origins.sqlite").write_bytes(b"o" * 73)
    (config / "autosourcarr.env").write_bytes(b"x" * 29)
    # This is deliberately much larger than the local data files. If the exclusion
    # regresses it would dominate the reported footprint.
    (downloads / "manga.cbz").write_bytes(b"d" * 50000)

    # Service construction must see a real SQLite DB, not the byte-size fixtures.
    service = AutoSourcarrService()
    monkeypatch.setattr(settings, "database_dir", data)
    monkeypatch.setattr(settings, "data_dir", data)
    monkeypatch.setattr(settings, "settings_config_dir", config)
    monkeypatch.setattr(settings, "main_suwayomi_downloads_path", str(downloads))
    result = service._autosourcarr_storage_stats(force=True)

    assert result["database_bytes"] == 101 + 17 + 73
    assert result["database_files"] == 3
    assert result["downloads_excluded"] is True
    assert result["roots"]["data"]["bytes"] < 50000
    assert "source-host" in result["note"]


def test_rebuild_subscribed_entries_short_circuits_after_first_safe_priority_wave():
    import inspect

    source = inspect.getsource(AutoSourcarrService.discover_missing_source_matches)
    rebuild = inspect.getsource(AutoSourcarrService.rebrowse_subbed_reset_seeds)
    assert "priority_rebuild_short_circuit" in source
    assert "int(settings.source_host_work_concurrency) - 1" in source
    assert "for offset in range(0, len(sources), rebuild_limit):" in source
    assert "if added > 0:" in source
    assert "break" in source[source.index("if priority_rebuild:"):source.index("else:", source.index("if priority_rebuild:"))]
    # The old exception handler accidentally referenced import_id from a different
    # task family and could hide the real rebuild failure/cancellation.
    assert "_recount_import_run(import_id" not in rebuild


def test_status_ui_shows_autosourcarr_and_database_sizes():
    root = Path(__file__).resolve().parents[1]
    js = (root / "app/static/app.js").read_text(encoding="utf-8")
    assert "storage=st.storage||{}" in js
    assert "simpleMetric('AutoSourcarr size'" in js
    assert "simpleMetric('Database size'" in js
    assert "manga downloads excluded" in js
