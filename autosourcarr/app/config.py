from pathlib import Path
from typing import Literal
from pydantic import model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


BUNDLED_REPAIR_SERVER = Path('/opt/autosourcarr-tools/repair-worker/server.py')
BUNDLED_IMAGE_VERSION = Path('/opt/autosourcarr-image-version')


class _SettingsRoot(BaseSettings):
    # Bootstrap only the canonical directory. Managed values must be loaded
    # before the full model validates stale process or dotenv values.
    settings_config_dir: Path = Path("/config")
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")


class Settings(_SettingsRoot):
    # Bundled images own their tools; external is only for unbundled development.
    app_services_mode: Literal["integrated", "external"] = "external"
    # Internal supervisor owns these values in the bundled image. Retained legacy
    # values remain readable for unbundled compatibility; never expose the token.
    repair_worker_url: str = ""
    repair_worker_token: str = ""
    repair_ai_provider: Literal["chatgpt", "openai", "claude", "gemini", "openrouter", "ollama", "custom"] = "chatgpt"
    repair_ai_protocol: Literal["responses", "chat_completions", "anthropic"] = "responses"
    repair_ai_base_url: str = ""
    repair_ai_model: str = ""
    repair_ai_api_key: str = ""
    repair_ai_workspace_id: str = ""
    repair_ai_browser_url: str = ""
    suwayomi_browser_url: str = ""
    komga_browser_url: str = ""
    komf_browser_url: str = ""
    translation_browser_url: str = ""
    authelia_browser_url: str = ""
    main_suwayomi_username: str = ""
    main_suwayomi_password: str = ""
    # Local development may use an external engine; the container forces integrated.
    source_engine_mode: Literal["integrated", "external"] = "integrated"
    source_engine_uid: int = 0
    source_engine_gid: int = 0

    @model_validator(mode="after")
    def use_bundled_services(self):
        # Image-owned location, never a user path, hostname or Compose service name.
        # Old env files cannot send this instance's repair work to a sibling worker.
        # The image marker still identifies an integrated installation if a tool
        # is damaged or masked. Report that failure instead of using a remote worker.
        if BUNDLED_IMAGE_VERSION.is_file() or BUNDLED_REPAIR_SERVER.is_file():
            self.app_services_mode = "integrated"
        return self

    @property
    def effective_source_url(self) -> str:
        return "http://127.0.0.1:4567" if self.source_engine_mode == "integrated" else self.source_suwayomi_url

    source_suwayomi_url: str = "http://autosourcarr-source-host:4567"
    autosourcarr_public_url: str = "http://127.0.0.1:8282"
    autosourcarr_internal_url: str = "http://autosourcarr:8282"
    tz: str = "Europe/Amsterdam"
    main_suwayomi_url: str = ""
    main_suwayomi_downloads_path: str = ""
    data_dir: Path = Path("/data")
    # Large SQLite files may live on a different mounted dataset/drive. When unset,
    # keep backward compatibility by storing them under DATA_DIR.
    database_dir: Path | None = None
    max_source_concurrency: int = 6
    source_host_work_concurrency: int = 4
    # Background source traffic gets a physically separate budget so Tasks can
    # never consume every private-Suwayomi worker needed by interactive requests.
    background_source_concurrency: int = 1
    background_task_concurrency: int = 2
    # Explicit import Match/Retry actions are user-requested and should not queue
    # behind bulk discovery. Keep a couple of parallel slots while reserving source
    # host capacity for Browse/Matrix/reader traffic.
    import_resolve_concurrency: int = 2
    browse_page_size: int = 50
    auto_match_threshold: float = 0.94
    fuzzy_review_threshold: float = 0.78
    metadata_ttl_seconds: int = 21600
    # External metadata/identity catalogs are read-only and never serve chapters.
    # MangaDex + AniList form the primary identity layer; the remaining providers
    # improve coverage for manhwa/manhua/doujinshi/webtoons and western comics.
    metadata_identity_enabled: bool = True
    metadata_identity_providers: str = "mangadex,anilist,mangaupdates,kitsu,myanimelist,comicvine"
    metadata_identity_include_adult: bool = True
    metadata_identity_timeout: float = 8.0
    metadata_identity_overall_timeout: float = 12.0
    metadata_identity_concurrency: int = 3
    metadata_identity_cache_ttl_seconds: int = 604800
    metadata_identity_min_score: float = 0.94
    metadata_identity_description_threshold: float = 0.82
    metadata_identity_query_limit: int = 3
    metadata_identity_results_per_provider: int = 6
    metadata_identity_on_ingest: bool = True
    metadata_identity_entries_per_feed: int = 24
    comicvine_api_key: str = ""
    source_request_timeout: float = 45.0
    source_auto_challenges: bool = True
    http_max_connections: int = 64
    http_max_keepalive_connections: int = 32
    http_keepalive_expiry_seconds: float = 30.0
    http_transport_retries: int = 1
    federated_source_timeout: float = 8.0
    federated_overall_timeout: float = 14.0
    federated_items_per_source: int = 30
    interactive_source_timeout: float = 4.0
    interactive_overall_timeout: float = 6.0
    search_cache_ttl_seconds: int = 20
    search_cache_max_entries: int = 128
    subbed_cache_ttl_seconds: int = 10
    cover_hint_cache_max_entries: int = 10000
    browse_cache_refresh_seconds: int = 600
    browse_prefetch_pages: int = 2
    background_refresh_on_startup: bool = False
    # Continuously walk source feeds at low priority so the unified catalog keeps
    # growing while AutoSourcarr is online. Subscription rebuilds always preempt it.
    continuous_catalog_discovery: bool = True
    continuous_catalog_interval_seconds: float = 8.0
    continuous_catalog_modes: str = "LATEST,POPULAR"
    continuous_catalog_backlog_limit: int = 80
    startup_refresh_delay_seconds: float = 20.0
    background_dedup_on_startup: bool = True
    automatic_duplicate_matching: bool = True
    automatic_duplicate_matching_delay_seconds: float = 8.0
    duplicate_signature_max_bucket: int = 24
    duplicate_snapshot_ttl_seconds: int = 60
    # Automatic duplicate consolidation is deliberately stricter than ordinary
    # source matching. Summary similarity is corroboration only: automatic merges
    # require direct identity/title/thumbnail proof and are never transitive.
    duplicate_auto_merge_min_exact_titles: int = 2
    duplicate_auto_merge_summary_threshold: float = 0.70
    duplicate_auto_merge_min_summary_chars: int = 80
    exact_thumbnail_matching: bool = True
    thumbnail_fingerprint_ttl_seconds: int = 2592000
    thumbnail_fingerprint_timeout: float = 8.0
    thumbnail_fingerprint_entries_per_pass: int = 500
    refresh_source_timeout: float = 12.0
    refresh_overall_timeout: float = 25.0
    cover_fetch_timeout: float = 5.0
    cover_inline_wait: float = 0.35
    cover_cache_ttl_seconds: int = 604800
    cover_fetch_concurrency: int = 4
    metadata_enrichment_entries_per_feed: int = 30
    metadata_enrichment_concurrency: int = 4
    metadata_enrichment_source_limit: int = 3
    metadata_enrichment_timeout: float = 10.0
    metadata_enrichment_on_startup: bool = False
    metadata_enrichment_startup_delay_seconds: float = 8.0
    metadata_enrichment_startup_limit: int = 500
    chapter_page_cache_ttl_seconds: int = 1800
    page_info_cache_max_entries: int = 4096
    quality_analysis_concurrency: int = 2
    quality_sample_timeout: float = 20.0
    quality_overall_timeout: float = 60.0
    color_detection_enabled: bool = True
    color_detection_sample_pages: int = 4
    color_detection_colored_pixel_fraction: float = 0.06
    color_detection_min_confidence: float = 0.78
    color_detection_timeout: float = 8.0
    # Manual matching can afford to wait longer than interactive browsing. Try a
    # few strong aliases with enough time for slower sources instead of spraying
    # many translated queries at once.
    manual_search_timeout: float = 12.0
    manual_search_overall_timeout: float = 24.0
    manual_search_alias_limit: int = 30
    manual_search_live_query_limit: int = 4
    auto_missing_source_discovery: bool = True
    auto_missing_source_entries_per_feed: int = 10
    auto_missing_source_source_limit: int = 8
    # Full recovery includes alias expansion, live validation and chapters. Bound
    # each source and each Subbed item; retain completed work and retry the rest.
    source_recovery_timeout_seconds: float = 45.0
    subbed_search_entry_timeout_seconds: float = 120.0
    auto_variant_discovery: bool = True
    auto_variant_min_score: float = 0.96
    auto_variant_query_limit: int = 6
    auto_variant_entries_per_feed: int = 20
    auto_variant_source_timeout: float = 6.0
    auto_variant_overall_timeout: float = 14.0
    auto_variant_startup_entries: int = 0
    auto_variant_startup_delay_seconds: float = 70.0
    manual_translation_query_limit: int = 6
    manual_translate_candidate_limit: int = 12
    translation_api_url: str = ""
    translation_api_key: str = ""
    translation_mode: Literal["auto", "builtin", "external", "off"] = "auto"
    translation_timeout: float = 5.0
    translation_overall_timeout: float = 7.0
    translation_concurrency: int = 4
    translation_target_languages: str = "ja,ko,zh"
    background_translation_on_discovery: bool = True
    background_translation_entries_per_feed: int = 12
    background_translation_entries_per_startup: int = 0
    translation_startup_delay_seconds: float = 45.0
    source_failure_threshold: int = 3
    source_cooldown_seconds: float = 120.0
    background_idle_seconds: float = 2.0
    sqlite_cache_mb: int = 64
    # SQLite uses WAL and one connection per worker thread. A short busy timeout
    # prevents a background writer from making an interactive action appear frozen.
    sqlite_busy_timeout_ms: int = 3000
    source_languages: str = "en,all"
    admin_token: str = ""
    autosourcarr_reader_api_token: str = ""
    authelia_user_header: str = ""

    @property
    def database_base_dir(self) -> Path:
        return self.database_dir or self.data_dir

    @property
    def catalog_path(self) -> Path:
        return self.database_base_dir / "catalog.sqlite"

    @property
    def origins_path(self) -> Path:
        return self.database_base_dir / "chapter-origins.sqlite"

    @property
    def continuous_catalog_modes_list(self) -> list[str]:
        allowed = {"LATEST", "POPULAR"}
        modes = [x.strip().upper() for x in self.continuous_catalog_modes.split(",") if x.strip()]
        return [x for x in modes if x in allowed] or ["LATEST", "POPULAR"]

    @property
    def translation_target_languages_list(self) -> list[str]:
        return [x.strip().lower() for x in self.translation_target_languages.split(",") if x.strip()]

    @property
    def allowed_languages(self) -> set[str]:
        return {x.strip().lower() for x in self.source_languages.split(",") if x.strip()}


# Finish-setup values use the retained env files even when Compose still injects older values.
from .setup_store import startup_overrides
import os
_bootstrap_settings = _SettingsRoot()
settings = Settings(**startup_overrides(_bootstrap_settings.settings_config_dir))
os.environ["TZ"] = settings.tz
import time
if hasattr(time, "tzset"): time.tzset()
settings.data_dir.mkdir(parents=True, exist_ok=True)
# A separately configured DATABASE_DIR is expected to be a persistent mount that
# was staged by the in-app database mover. Never silently create a missing path
# inside the container overlay: that would look like a brand-new empty database
# after a host mount was accidentally removed or renamed.
if settings.database_dir is not None:
    database_dir = settings.database_dir.expanduser()
    data_dir = settings.data_dir.expanduser()
    # DATABASE_DIR=/data is the backwards-compatible default and may initialize an
    # empty installation. Any *separate* location is only valid after the mover has
    # copied the existing databases there. This catches a missing/wrong TrueNAS bind
    # mount before SQLite can silently create a brand-new empty catalog.
    if database_dir.resolve() == data_dir.resolve():
        database_dir.mkdir(parents=True, exist_ok=True)
    else:
        if not database_dir.exists():
            raise RuntimeError(
                f"Configured DATABASE_DIR does not exist: {database_dir}. "
                "Restore/mount the persistent database dataset at that container path; "
                "AutoSourcarr will not create a new empty database there."
            )
        if not database_dir.is_dir():
            raise RuntimeError(f"Configured DATABASE_DIR is not a directory: {database_dir}")
        missing = [name for name in ("catalog.sqlite", "chapter-origins.sqlite") if not (database_dir / name).is_file()]
        if missing:
            raise RuntimeError(
                f"Configured DATABASE_DIR is missing existing AutoSourcarr database file(s): {', '.join(missing)}. "
                "Restore the database mount or set DATABASE_DIR back to the previous location; "
                "AutoSourcarr will not initialize an empty replacement database here."
            )
else:
    settings.database_base_dir.mkdir(parents=True, exist_ok=True)
