from pathlib import Path


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def test_legacy_extension_avoids_generated_kotlinx_serialization_runtime_abi():
    root = project_root()
    src = root / "extension/module/src/autosourcarr"
    combined = "\n".join(p.read_text(encoding="utf-8") for p in src.glob("*.kt"))
    assert "import kotlinx.serialization" not in combined
    assert "@Serializable" not in combined
    assert "parseAs<" not in combined
    dto = (src / "Dto.kt").read_text(encoding="utf-8")
    mini = (src / "MiniJson.kt").read_text(encoding="utf-8")
    source = (src / "AutoSourcarr.kt").read_text(encoding="utf-8")
    assert "parseBrowseDto(readerJsonBody(response))" in source
    assert "parseRefreshDto(readerJsonBody(response))" in source
    assert "parsePagesDto(readerJsonBody(response))" in source
    assert "parseSourcesDto(readerJsonBody(response))" in source
    assert "MiniJson.parseObject" in dto
    assert "parseUnicodeEscape" in mini


def test_v067_legacy_basic_auth_fields_remain_upgrade_compatible():
    root = project_root()
    builder = (root / "extension/build-in-container.sh").read_text(encoding="utf-8")
    env = (root / "config/autosourcarr.env.example").read_text(encoding="utf-8")

    # 0.2.68 superseded the v0.2.67 Basic service-account implementation, but
    # existing env files must still load cleanly during an overlay upgrade.
    assert "AUTOSOURCARR_AUTHELIA_BASIC_USERNAME=" in env
    assert "AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD=" in env
    assert "deprecated and ignored" in builder
    assert "AUTOSOURCARR_READER_API_TOKEN" in builder


def test_v067_history_is_retained():
    root = project_root()
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    assert "## 0.2.67 - 2026-08-28" in changelog
    assert "dependency-free reader" in changelog
