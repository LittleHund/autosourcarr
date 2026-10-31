from pathlib import Path


def project_root() -> Path:
    return Path(__file__).resolve().parents[2]


def test_reader_api_is_token_protected_and_fail_closed():
    root = project_root()
    main = (root / "autosourcarr/app/main.py").read_text(encoding="utf-8")
    config = (root / "autosourcarr/app/config.py").read_text(encoding="utf-8")
    env_settings = (root / "autosourcarr/app/env_settings.py").read_text(encoding="utf-8")

    assert 'autosourcarr_reader_api_token: str = ""' in config
    assert 'reader_prefix = "/api/reader/v1"' in main
    assert 'internal_prefix = "/api/v1"' in main
    assert 'b"x-autosourcarr-api-key"' in main
    assert "secrets.compare_digest" in main
    assert 'status_code=401' in main
    assert 'status_code=503' in main
    assert 'status_code=405' in main
    assert '{"GET", "HEAD"}' in main
    assert 'rewritten["path"] = rewritten_path' in main
    assert '"AUTOSOURCARR_READER_API_TOKEN"' in env_settings
    assert '"AUTOSOURCARR_READER_API_TOKEN",' in env_settings


def test_android_extension_uses_reader_prefix_and_api_key_for_all_requests():
    root = project_root()
    src = root / "extension/module/src/autosourcarr"
    source = (src / "AutoSourcarr.kt").read_text(encoding="utf-8")
    dto = (src / "Dto.kt").read_text(encoding="utf-8")
    auth = (src / "BuildAuth.kt").read_text(encoding="utf-8")
    builder = (root / "extension/build-in-container.sh").read_text(encoding="utf-8")

    assert 'set("X-AutoSourcarr-API-Key", AUTOSOURCARR_READER_API_KEY)' in source
    assert 'private fun endpoint(path: String)' in source
    assert 'override fun imageRequest(page: Page): Request = GET(requireNotNull(page.imageUrl), headers)' in source
    assert 'AUTOSOURCARR_API_PREFIX != "/api/v1"' in dto
    assert 'AUTOSOURCARR_API_PREFIX = "/api/v1"' in auth
    assert 'AUTOSOURCARR_READER_API_KEY = ""' in auth
    assert 'write_auth_config "/api/v1" ""' in builder
    assert 'write_auth_config "/api/reader/v1" "$READER_API_TOKEN"' in builder
    assert 'python3 "$READER_CONFIG_CHECK" --check-env' in builder
    from app.reader_build import evaluate_reader_config
    missing_key = evaluate_reader_config({
        'AUTOSOURCARR_INTERNAL_URL': 'http://autosourcarr:8282',
        'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example.invalid',
        'AUTOSOURCARR_READER_API_TOKEN': '',
    })
    assert not missing_key['ready']
    assert missing_key['errors'][0]['key'] == 'AUTOSOURCARR_READER_API_TOKEN'
    assert missing_key['errors'][0]['code'] == 12


def test_android_apk_uses_persistent_signing_identity():
    root = project_root()
    builder = (root / "extension/build-in-container.sh").read_text(encoding="utf-8")
    truenas = (root / "TRUENAS.md").read_text(encoding="utf-8")

    assert 'SIGN_DIR="$CONFIG_DIR/.extension-signing"' in builder
    assert 'autosourcarr-extension.jks' in builder
    assert 'python3 "$ROOT/extension/signing_state.py" "$CONFIG_DIR"' in builder
    assert (root / 'extension/signing_state.py').is_file()
    assert 'apksigner' in builder
    assert 'sign_android_apk "$SOURCE_APK" "$SIGNED_SOURCE_APK"' in builder
    assert 'Signing certificate SHA-256' in builder
    assert 'INSTALL_FAILED_UPDATE_INCOMPATIBLE' in truenas
    assert 'uninstall that old AutoSourcarr extension once' in truenas


def test_authelia_docs_bypass_only_dedicated_reader_path():
    root = project_root()
    readme = (root / "README.md").read_text(encoding="utf-8")
    truenas = (root / "TRUENAS.md").read_text(encoding="utf-8")

    for text in (readme, truenas):
        assert "^/api/reader/v1([/?].*)?$" in text
        assert "policy: bypass" in text
        assert "Do not bypass `/api/v1/`" in text or "Do **not** bypass `/api/v1/`" in text
        assert "AUTOSOURCARR_READER_API_TOKEN" in text


def test_v068_reader_auth_feature_remains_present_after_later_releases():
    root = project_root()
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    assert "## 0.2.68 - 2026-08-28" in changelog
    assert "0.2.68 → 1.4.2068" in changelog
    assert "/api/reader/v1/" in changelog
