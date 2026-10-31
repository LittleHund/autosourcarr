from pathlib import Path

import pytest

from app.env_settings import file_path, parse_env, settings_payload, update_env


def _write_envs(root: Path) -> None:
    (root / "autosourcarr.env").write_text(
        "# Runtime\nTZ=Europe/Amsterdam\nSETTINGS_CONFIG_DIR=/config\n\n# Connections\nMAIN_SUWAYOMI_URL=http://suwayomi:4567\nADMIN_TOKEN=\n",
        encoding="utf-8",
    )
    (root / "source-host.env").write_text(
        "# Server\nBIND_PORT=4567\nWEB_UI_ENABLED=true\nEXTENSION_STORES=[\"https://example.invalid/index.pb\"]\n",
        encoding="utf-8",
    )


def test_settings_payload_reads_both_project_env_files(tmp_path: Path):
    _write_envs(tmp_path)
    payload = settings_payload(tmp_path)

    assert payload["config_dir"] == str(tmp_path)
    assert [item["id"] for item in payload["files"]] == ["autosourcarr", "source_host"]
    auto = payload["files"][0]
    assert auto["exists"] is True
    assert [group["name"] for group in auto["groups"]][:2] == ["General", "Connections"]
    assert auto["writable"] is True
    keys = {item["key"] for group in auto["groups"] for item in group["items"]}
    assert {"TZ", "MAIN_SUWAYOMI_URL", "ADMIN_TOKEN"} <= keys
    admin = next(item for group in auto["groups"] for item in group["items"] if item["key"] == "ADMIN_TOKEN")
    assert admin["kind"] == "password"


def test_update_env_preserves_comments_order_and_unmodified_values(tmp_path: Path):
    _write_envs(tmp_path)
    path = tmp_path / "autosourcarr.env"

    result = update_env(path, {"TZ": "UTC", "MAIN_SUWAYOMI_URL": "http://main-suwayomi:4567"})
    text = path.read_text(encoding="utf-8")

    assert result["updated"] == 2
    assert result["restart_required"] is True
    assert "# Runtime\nTZ=UTC\nSETTINGS_CONFIG_DIR=/config" in text
    assert "# Connections\nMAIN_SUWAYOMI_URL=http://main-suwayomi:4567\nADMIN_TOKEN=" in text
    pairs, errors = parse_env(path)
    assert errors == []
    assert dict(pairs)["SETTINGS_CONFIG_DIR"] == "/config"


def test_update_env_rejects_unknown_keys_and_multiline_values(tmp_path: Path):
    _write_envs(tmp_path)
    path = tmp_path / "autosourcarr.env"

    with pytest.raises(ValueError, match="Unknown environment variable"):
        update_env(path, {"DOES_NOT_EXIST": "1"})
    with pytest.raises(ValueError, match="may not contain newlines"):
        update_env(path, {"TZ": "UTC\nBAD=1"})


def test_file_path_rejects_unknown_file_id(tmp_path: Path):
    assert file_path(tmp_path, "source_host") == tmp_path / "source-host.env"
    with pytest.raises(KeyError):
        file_path(tmp_path, "not-a-file")


def test_settings_groups_prioritize_user_facing_sections_and_paths(tmp_path: Path):
    (tmp_path / "autosourcarr.env").write_text("TZ=Europe/Amsterdam\nMAIN_SUWAYOMI_URL=http://main\nSETTINGS_CONFIG_DIR=/config\nDUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD=0.70\nHTTP_MAX_CONNECTIONS=32\n")
    (tmp_path / "source-host.env").write_text("TZ=Europe/Amsterdam\nBIND_IP=0.0.0.0\n")
    payload=settings_payload(tmp_path)
    f=next(x for x in payload["files"] if x["id"]=="autosourcarr")
    names=[g["name"] for g in f["groups"]]
    assert names.index("Connections") < names.index("Paths") < names.index("Duplicates") < names.index("Advanced performance")
