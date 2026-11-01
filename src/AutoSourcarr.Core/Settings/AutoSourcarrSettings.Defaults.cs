// Initial defaults translated from the protected 0.4.3 configuration contract.
// This is compiled C#; no Python runtime is used by these settings.
namespace AutoSourcarr.Core.Settings;

public sealed partial record AutoSourcarrSettings
{
    [System.Text.Json.Serialization.JsonPropertyName("settings_config_dir")]
    public string SettingsConfigDir { get; init; } = "/config";

    [System.Text.Json.Serialization.JsonPropertyName("app_services_mode")]
    public string AppServicesMode { get; init; } = "external";

    [System.Text.Json.Serialization.JsonPropertyName("repair_worker_url")]
    public string RepairWorkerUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_worker_token")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string RepairWorkerToken { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_provider")]
    public string RepairAiProvider { get; init; } = "chatgpt";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_protocol")]
    public string RepairAiProtocol { get; init; } = "responses";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_base_url")]
    public string RepairAiBaseUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_model")]
    public string RepairAiModel { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_api_key")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string RepairAiApiKey { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_workspace_id")]
    public string RepairAiWorkspaceId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("repair_ai_browser_url")]
    public string RepairAiBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("suwayomi_browser_url")]
    public string SuwayomiBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("komga_browser_url")]
    public string KomgaBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("komf_browser_url")]
    public string KomfBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("translation_browser_url")]
    public string TranslationBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("authelia_browser_url")]
    public string AutheliaBrowserUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("main_suwayomi_username")]
    public string MainSuwayomiUsername { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("main_suwayomi_password")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string MainSuwayomiPassword { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("source_engine_mode")]
    public string SourceEngineMode { get; init; } = "integrated";

    [System.Text.Json.Serialization.JsonPropertyName("source_engine_uid")]
    [System.Text.Json.Serialization.JsonConverter(typeof(SourceIdentityIntegerJsonConverter))]
    public System.Numerics.BigInteger SourceEngineUid { get; init; } = 0;

    [System.Text.Json.Serialization.JsonPropertyName("source_engine_gid")]
    [System.Text.Json.Serialization.JsonConverter(typeof(SourceIdentityIntegerJsonConverter))]
    public System.Numerics.BigInteger SourceEngineGid { get; init; } = 0;

    [System.Text.Json.Serialization.JsonPropertyName("source_suwayomi_url")]
    public string SourceSuwayomiUrl { get; init; } = "http://autosourcarr-source-host:4567";

    [System.Text.Json.Serialization.JsonPropertyName("autosourcarr_public_url")]
    public string AutosourcarrPublicUrl { get; init; } = "http://127.0.0.1:8282";

    [System.Text.Json.Serialization.JsonPropertyName("autosourcarr_internal_url")]
    public string AutosourcarrInternalUrl { get; init; } = "http://autosourcarr:8282";

    [System.Text.Json.Serialization.JsonPropertyName("tz")]
    public string Tz { get; init; } = "Europe/Amsterdam";

    [System.Text.Json.Serialization.JsonPropertyName("main_suwayomi_url")]
    public string MainSuwayomiUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("main_suwayomi_downloads_path")]
    public string MainSuwayomiDownloadsPath { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("data_dir")]
    public string DataDir { get; init; } = "/data";

    [System.Text.Json.Serialization.JsonPropertyName("database_dir")]
    public string? DatabaseDir { get; init; } = null;

    [System.Text.Json.Serialization.JsonPropertyName("library_dir")]
    public string? LibraryDir { get; init; } = null;

    [System.Text.Json.Serialization.JsonPropertyName("max_source_concurrency")]
    public int MaxSourceConcurrency { get; init; } = 6;

    [System.Text.Json.Serialization.JsonPropertyName("source_host_work_concurrency")]
    public int SourceHostWorkConcurrency { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("background_source_concurrency")]
    public int BackgroundSourceConcurrency { get; init; } = 1;

    [System.Text.Json.Serialization.JsonPropertyName("background_task_concurrency")]
    public int BackgroundTaskConcurrency { get; init; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("import_resolve_concurrency")]
    public int ImportResolveConcurrency { get; init; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("browse_page_size")]
    public int BrowsePageSize { get; init; } = 50;

    [System.Text.Json.Serialization.JsonPropertyName("auto_match_threshold")]
    public double AutoMatchThreshold { get; init; } = 0.94;

    [System.Text.Json.Serialization.JsonPropertyName("fuzzy_review_threshold")]
    public double FuzzyReviewThreshold { get; init; } = 0.78;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_ttl_seconds")]
    public int MetadataTtlSeconds { get; init; } = 21600;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_enabled")]
    public bool MetadataIdentityEnabled { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_providers")]
    public string MetadataIdentityProviders { get; init; } = "mangadex,anilist,mangaupdates,kitsu,myanimelist,comicvine";

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_include_adult")]
    public bool MetadataIdentityIncludeAdult { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_timeout")]
    public double MetadataIdentityTimeout { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_overall_timeout")]
    public double MetadataIdentityOverallTimeout { get; init; } = 12.0;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_concurrency")]
    public int MetadataIdentityConcurrency { get; init; } = 3;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_cache_ttl_seconds")]
    public int MetadataIdentityCacheTtlSeconds { get; init; } = 604800;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_min_score")]
    public double MetadataIdentityMinScore { get; init; } = 0.94;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_description_threshold")]
    public double MetadataIdentityDescriptionThreshold { get; init; } = 0.82;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_query_limit")]
    public int MetadataIdentityQueryLimit { get; init; } = 3;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_results_per_provider")]
    public int MetadataIdentityResultsPerProvider { get; init; } = 6;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_on_ingest")]
    public bool MetadataIdentityOnIngest { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_identity_entries_per_feed")]
    public int MetadataIdentityEntriesPerFeed { get; init; } = 24;

    [System.Text.Json.Serialization.JsonPropertyName("comicvine_api_key")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string ComicvineApiKey { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("source_request_timeout")]
    public double SourceRequestTimeout { get; init; } = 45.0;

    [System.Text.Json.Serialization.JsonPropertyName("source_auto_challenges")]
    public bool SourceAutoChallenges { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("http_max_connections")]
    public int HttpMaxConnections { get; init; } = 64;

    [System.Text.Json.Serialization.JsonPropertyName("http_max_keepalive_connections")]
    public int HttpMaxKeepaliveConnections { get; init; } = 32;

    [System.Text.Json.Serialization.JsonPropertyName("http_keepalive_expiry_seconds")]
    public double HttpKeepaliveExpirySeconds { get; init; } = 30.0;

    [System.Text.Json.Serialization.JsonPropertyName("http_transport_retries")]
    public int HttpTransportRetries { get; init; } = 1;

    [System.Text.Json.Serialization.JsonPropertyName("federated_source_timeout")]
    public double FederatedSourceTimeout { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("federated_overall_timeout")]
    public double FederatedOverallTimeout { get; init; } = 14.0;

    [System.Text.Json.Serialization.JsonPropertyName("federated_items_per_source")]
    public int FederatedItemsPerSource { get; init; } = 30;

    [System.Text.Json.Serialization.JsonPropertyName("interactive_source_timeout")]
    public double InteractiveSourceTimeout { get; init; } = 4.0;

    [System.Text.Json.Serialization.JsonPropertyName("interactive_overall_timeout")]
    public double InteractiveOverallTimeout { get; init; } = 6.0;

    [System.Text.Json.Serialization.JsonPropertyName("search_cache_ttl_seconds")]
    public int SearchCacheTtlSeconds { get; init; } = 20;

    [System.Text.Json.Serialization.JsonPropertyName("search_cache_max_entries")]
    public int SearchCacheMaxEntries { get; init; } = 128;

    [System.Text.Json.Serialization.JsonPropertyName("subbed_cache_ttl_seconds")]
    public int SubbedCacheTtlSeconds { get; init; } = 10;

    [System.Text.Json.Serialization.JsonPropertyName("cover_hint_cache_max_entries")]
    public int CoverHintCacheMaxEntries { get; init; } = 10000;

    [System.Text.Json.Serialization.JsonPropertyName("browse_cache_refresh_seconds")]
    public int BrowseCacheRefreshSeconds { get; init; } = 600;

    [System.Text.Json.Serialization.JsonPropertyName("browse_prefetch_pages")]
    public int BrowsePrefetchPages { get; init; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("background_refresh_on_startup")]
    public bool BackgroundRefreshOnStartup { get; init; } = false;

    [System.Text.Json.Serialization.JsonPropertyName("continuous_catalog_discovery")]
    public bool ContinuousCatalogDiscovery { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("continuous_catalog_interval_seconds")]
    public double ContinuousCatalogIntervalSeconds { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("continuous_catalog_modes")]
    public string ContinuousCatalogModes { get; init; } = "LATEST,POPULAR";

    [System.Text.Json.Serialization.JsonPropertyName("continuous_catalog_backlog_limit")]
    public int ContinuousCatalogBacklogLimit { get; init; } = 80;

    [System.Text.Json.Serialization.JsonPropertyName("startup_refresh_delay_seconds")]
    public double StartupRefreshDelaySeconds { get; init; } = 20.0;

    [System.Text.Json.Serialization.JsonPropertyName("background_dedup_on_startup")]
    public bool BackgroundDedupOnStartup { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("automatic_duplicate_matching")]
    public bool AutomaticDuplicateMatching { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("automatic_duplicate_matching_delay_seconds")]
    public double AutomaticDuplicateMatchingDelaySeconds { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_signature_max_bucket")]
    public int DuplicateSignatureMaxBucket { get; init; } = 24;

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_snapshot_ttl_seconds")]
    public int DuplicateSnapshotTtlSeconds { get; init; } = 60;

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_auto_merge_min_exact_titles")]
    public int DuplicateAutoMergeMinExactTitles { get; init; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_auto_merge_summary_threshold")]
    public double DuplicateAutoMergeSummaryThreshold { get; init; } = 0.7;

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_auto_merge_min_summary_chars")]
    public int DuplicateAutoMergeMinSummaryChars { get; init; } = 80;

    [System.Text.Json.Serialization.JsonPropertyName("exact_thumbnail_matching")]
    public bool ExactThumbnailMatching { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("thumbnail_fingerprint_ttl_seconds")]
    public int ThumbnailFingerprintTtlSeconds { get; init; } = 2592000;

    [System.Text.Json.Serialization.JsonPropertyName("thumbnail_fingerprint_timeout")]
    public double ThumbnailFingerprintTimeout { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("thumbnail_fingerprint_entries_per_pass")]
    public int ThumbnailFingerprintEntriesPerPass { get; init; } = 500;

    [System.Text.Json.Serialization.JsonPropertyName("refresh_source_timeout")]
    public double RefreshSourceTimeout { get; init; } = 12.0;

    [System.Text.Json.Serialization.JsonPropertyName("refresh_overall_timeout")]
    public double RefreshOverallTimeout { get; init; } = 25.0;

    [System.Text.Json.Serialization.JsonPropertyName("cover_fetch_timeout")]
    public double CoverFetchTimeout { get; init; } = 5.0;

    [System.Text.Json.Serialization.JsonPropertyName("cover_inline_wait")]
    public double CoverInlineWait { get; init; } = 0.35;

    [System.Text.Json.Serialization.JsonPropertyName("cover_cache_ttl_seconds")]
    public int CoverCacheTtlSeconds { get; init; } = 604800;

    [System.Text.Json.Serialization.JsonPropertyName("cover_fetch_concurrency")]
    public int CoverFetchConcurrency { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_entries_per_feed")]
    public int MetadataEnrichmentEntriesPerFeed { get; init; } = 30;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_concurrency")]
    public int MetadataEnrichmentConcurrency { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_source_limit")]
    public int MetadataEnrichmentSourceLimit { get; init; } = 3;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_timeout")]
    public double MetadataEnrichmentTimeout { get; init; } = 10.0;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_on_startup")]
    public bool MetadataEnrichmentOnStartup { get; init; } = false;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_startup_delay_seconds")]
    public double MetadataEnrichmentStartupDelaySeconds { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("metadata_enrichment_startup_limit")]
    public int MetadataEnrichmentStartupLimit { get; init; } = 500;

    [System.Text.Json.Serialization.JsonPropertyName("chapter_page_cache_ttl_seconds")]
    public int ChapterPageCacheTtlSeconds { get; init; } = 1800;

    [System.Text.Json.Serialization.JsonPropertyName("page_info_cache_max_entries")]
    public int PageInfoCacheMaxEntries { get; init; } = 4096;

    [System.Text.Json.Serialization.JsonPropertyName("quality_analysis_concurrency")]
    public int QualityAnalysisConcurrency { get; init; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("quality_sample_timeout")]
    public double QualitySampleTimeout { get; init; } = 20.0;

    [System.Text.Json.Serialization.JsonPropertyName("quality_overall_timeout")]
    public double QualityOverallTimeout { get; init; } = 60.0;

    [System.Text.Json.Serialization.JsonPropertyName("color_detection_enabled")]
    public bool ColorDetectionEnabled { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("color_detection_sample_pages")]
    public int ColorDetectionSamplePages { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("color_detection_colored_pixel_fraction")]
    public double ColorDetectionColoredPixelFraction { get; init; } = 0.06;

    [System.Text.Json.Serialization.JsonPropertyName("color_detection_min_confidence")]
    public double ColorDetectionMinConfidence { get; init; } = 0.78;

    [System.Text.Json.Serialization.JsonPropertyName("color_detection_timeout")]
    public double ColorDetectionTimeout { get; init; } = 8.0;

    [System.Text.Json.Serialization.JsonPropertyName("manual_search_timeout")]
    public double ManualSearchTimeout { get; init; } = 12.0;

    [System.Text.Json.Serialization.JsonPropertyName("manual_search_overall_timeout")]
    public double ManualSearchOverallTimeout { get; init; } = 24.0;

    [System.Text.Json.Serialization.JsonPropertyName("manual_search_alias_limit")]
    public int ManualSearchAliasLimit { get; init; } = 30;

    [System.Text.Json.Serialization.JsonPropertyName("manual_search_live_query_limit")]
    public int ManualSearchLiveQueryLimit { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("auto_missing_source_discovery")]
    public bool AutoMissingSourceDiscovery { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("auto_missing_source_entries_per_feed")]
    public int AutoMissingSourceEntriesPerFeed { get; init; } = 10;

    [System.Text.Json.Serialization.JsonPropertyName("auto_missing_source_source_limit")]
    public int AutoMissingSourceSourceLimit { get; init; } = 8;

    [System.Text.Json.Serialization.JsonPropertyName("source_recovery_timeout_seconds")]
    public double SourceRecoveryTimeoutSeconds { get; init; } = 45.0;

    [System.Text.Json.Serialization.JsonPropertyName("subbed_search_entry_timeout_seconds")]
    public double SubbedSearchEntryTimeoutSeconds { get; init; } = 120.0;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_discovery")]
    public bool AutoVariantDiscovery { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_min_score")]
    public double AutoVariantMinScore { get; init; } = 0.96;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_query_limit")]
    public int AutoVariantQueryLimit { get; init; } = 6;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_entries_per_feed")]
    public int AutoVariantEntriesPerFeed { get; init; } = 20;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_source_timeout")]
    public double AutoVariantSourceTimeout { get; init; } = 6.0;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_overall_timeout")]
    public double AutoVariantOverallTimeout { get; init; } = 14.0;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_startup_entries")]
    public int AutoVariantStartupEntries { get; init; } = 0;

    [System.Text.Json.Serialization.JsonPropertyName("auto_variant_startup_delay_seconds")]
    public double AutoVariantStartupDelaySeconds { get; init; } = 70.0;

    [System.Text.Json.Serialization.JsonPropertyName("manual_translation_query_limit")]
    public int ManualTranslationQueryLimit { get; init; } = 6;

    [System.Text.Json.Serialization.JsonPropertyName("manual_translate_candidate_limit")]
    public int ManualTranslateCandidateLimit { get; init; } = 12;

    [System.Text.Json.Serialization.JsonPropertyName("translation_api_url")]
    public string TranslationApiUrl { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("translation_api_key")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string TranslationApiKey { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("translation_mode")]
    public string TranslationMode { get; init; } = "auto";

    [System.Text.Json.Serialization.JsonPropertyName("translation_timeout")]
    public double TranslationTimeout { get; init; } = 5.0;

    [System.Text.Json.Serialization.JsonPropertyName("translation_overall_timeout")]
    public double TranslationOverallTimeout { get; init; } = 7.0;

    [System.Text.Json.Serialization.JsonPropertyName("translation_concurrency")]
    public int TranslationConcurrency { get; init; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("translation_target_languages")]
    public string TranslationTargetLanguages { get; init; } = "ja,ko,zh";

    [System.Text.Json.Serialization.JsonPropertyName("background_translation_on_discovery")]
    public bool BackgroundTranslationOnDiscovery { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("background_translation_entries_per_feed")]
    public int BackgroundTranslationEntriesPerFeed { get; init; } = 12;

    [System.Text.Json.Serialization.JsonPropertyName("background_translation_entries_per_startup")]
    public int BackgroundTranslationEntriesPerStartup { get; init; } = 0;

    [System.Text.Json.Serialization.JsonPropertyName("translation_startup_delay_seconds")]
    public double TranslationStartupDelaySeconds { get; init; } = 45.0;

    [System.Text.Json.Serialization.JsonPropertyName("source_failure_threshold")]
    public int SourceFailureThreshold { get; init; } = 3;

    [System.Text.Json.Serialization.JsonPropertyName("source_cooldown_seconds")]
    public double SourceCooldownSeconds { get; init; } = 120.0;

    /// <summary>SRC-9 paced traffic: least milliseconds between two source host requests to one website (0 = off).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("source_min_interval_ms")]
    public int SourceMinIntervalMs { get; init; } = 250;

    [System.Text.Json.Serialization.JsonPropertyName("background_idle_seconds")]
    public double BackgroundIdleSeconds { get; init; } = 2.0;

    [System.Text.Json.Serialization.JsonPropertyName("sqlite_cache_mb")]
    public int SqliteCacheMb { get; init; } = 64;

    [System.Text.Json.Serialization.JsonPropertyName("sqlite_busy_timeout_ms")]
    public int SqliteBusyTimeoutMs { get; init; } = 3000;

    [System.Text.Json.Serialization.JsonPropertyName("source_languages")]
    public string SourceLanguages { get; init; } = "en,all";

    [System.Text.Json.Serialization.JsonPropertyName("admin_token")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string AdminToken { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("autosourcarr_reader_api_token")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string AutosourcarrReaderApiToken { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("auth_method")]
    public string AuthMethod { get; init; } = "forms";

    [System.Text.Json.Serialization.JsonPropertyName("auth_admin_groups")]
    public string AuthAdminGroups { get; init; } = "admins";

    [System.Text.Json.Serialization.JsonPropertyName("auth_guest_groups")]
    public string AuthGuestGroups { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("auth_owner_external_user")]
    public string AuthOwnerExternalUser { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("auth_trusted_proxies")]
    public string AuthTrustedProxies { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("auth_reset_owner")]
    public bool AuthResetOwner { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("authelia_user_header")]
    public string AutheliaUserHeader { get; init; } = "";

    public static IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("SETTINGS_CONFIG_DIR", nameof(SettingsConfigDir), SettingKind.Path, false, []),
        new("APP_SERVICES_MODE", nameof(AppServicesMode), SettingKind.Text, false, ["integrated", "external"]),
        new("REPAIR_WORKER_URL", nameof(RepairWorkerUrl), SettingKind.Text, false, []),
        new("REPAIR_WORKER_TOKEN", nameof(RepairWorkerToken), SettingKind.Text, true, []),
        new("REPAIR_AI_PROVIDER", nameof(RepairAiProvider), SettingKind.Text, false, ["chatgpt", "openai", "claude", "gemini", "openrouter", "ollama", "custom"]),
        new("REPAIR_AI_PROTOCOL", nameof(RepairAiProtocol), SettingKind.Text, false, ["responses", "chat_completions", "anthropic"]),
        new("REPAIR_AI_BASE_URL", nameof(RepairAiBaseUrl), SettingKind.Text, false, []),
        new("REPAIR_AI_MODEL", nameof(RepairAiModel), SettingKind.Text, false, []),
        new("REPAIR_AI_API_KEY", nameof(RepairAiApiKey), SettingKind.Text, true, []),
        new("REPAIR_AI_WORKSPACE_ID", nameof(RepairAiWorkspaceId), SettingKind.Text, false, []),
        new("REPAIR_AI_BROWSER_URL", nameof(RepairAiBrowserUrl), SettingKind.Text, false, []),
        new("SUWAYOMI_BROWSER_URL", nameof(SuwayomiBrowserUrl), SettingKind.Text, false, []),
        new("KOMGA_BROWSER_URL", nameof(KomgaBrowserUrl), SettingKind.Text, false, []),
        new("KOMF_BROWSER_URL", nameof(KomfBrowserUrl), SettingKind.Text, false, []),
        new("TRANSLATION_BROWSER_URL", nameof(TranslationBrowserUrl), SettingKind.Text, false, []),
        new("AUTHELIA_BROWSER_URL", nameof(AutheliaBrowserUrl), SettingKind.Text, false, []),
        new("MAIN_SUWAYOMI_USERNAME", nameof(MainSuwayomiUsername), SettingKind.Text, false, []),
        new("MAIN_SUWAYOMI_PASSWORD", nameof(MainSuwayomiPassword), SettingKind.Text, true, []),
        new("SOURCE_ENGINE_MODE", nameof(SourceEngineMode), SettingKind.Text, false, ["integrated", "external"]),
        new("SOURCE_ENGINE_UID", nameof(SourceEngineUid), SettingKind.Integer, false, []),
        new("SOURCE_ENGINE_GID", nameof(SourceEngineGid), SettingKind.Integer, false, []),
        new("SOURCE_SUWAYOMI_URL", nameof(SourceSuwayomiUrl), SettingKind.Text, false, []),
        new("AUTOSOURCARR_PUBLIC_URL", nameof(AutosourcarrPublicUrl), SettingKind.Text, false, []),
        new("AUTOSOURCARR_INTERNAL_URL", nameof(AutosourcarrInternalUrl), SettingKind.Text, false, []),
        new("TZ", nameof(Tz), SettingKind.Text, false, []),
        new("MAIN_SUWAYOMI_URL", nameof(MainSuwayomiUrl), SettingKind.Text, false, []),
        new("MAIN_SUWAYOMI_DOWNLOADS_PATH", nameof(MainSuwayomiDownloadsPath), SettingKind.Text, false, []),
        new("DATA_DIR", nameof(DataDir), SettingKind.Path, false, []),
        new("DATABASE_DIR", nameof(DatabaseDir), SettingKind.NullablePath, false, []),
        new("LIBRARY_DIR", nameof(LibraryDir), SettingKind.NullablePath, false, []),
        new("MAX_SOURCE_CONCURRENCY", nameof(MaxSourceConcurrency), SettingKind.Integer, false, []),
        new("SOURCE_HOST_WORK_CONCURRENCY", nameof(SourceHostWorkConcurrency), SettingKind.Integer, false, []),
        new("BACKGROUND_SOURCE_CONCURRENCY", nameof(BackgroundSourceConcurrency), SettingKind.Integer, false, []),
        new("BACKGROUND_TASK_CONCURRENCY", nameof(BackgroundTaskConcurrency), SettingKind.Integer, false, []),
        new("IMPORT_RESOLVE_CONCURRENCY", nameof(ImportResolveConcurrency), SettingKind.Integer, false, []),
        new("BROWSE_PAGE_SIZE", nameof(BrowsePageSize), SettingKind.Integer, false, []),
        new("AUTO_MATCH_THRESHOLD", nameof(AutoMatchThreshold), SettingKind.Number, false, []),
        new("FUZZY_REVIEW_THRESHOLD", nameof(FuzzyReviewThreshold), SettingKind.Number, false, []),
        new("METADATA_TTL_SECONDS", nameof(MetadataTtlSeconds), SettingKind.Integer, false, []),
        new("METADATA_IDENTITY_ENABLED", nameof(MetadataIdentityEnabled), SettingKind.Boolean, false, []),
        new("METADATA_IDENTITY_PROVIDERS", nameof(MetadataIdentityProviders), SettingKind.Text, false, []),
        new("METADATA_IDENTITY_INCLUDE_ADULT", nameof(MetadataIdentityIncludeAdult), SettingKind.Boolean, false, []),
        new("METADATA_IDENTITY_TIMEOUT", nameof(MetadataIdentityTimeout), SettingKind.Number, false, []),
        new("METADATA_IDENTITY_OVERALL_TIMEOUT", nameof(MetadataIdentityOverallTimeout), SettingKind.Number, false, []),
        new("METADATA_IDENTITY_CONCURRENCY", nameof(MetadataIdentityConcurrency), SettingKind.Integer, false, []),
        new("METADATA_IDENTITY_CACHE_TTL_SECONDS", nameof(MetadataIdentityCacheTtlSeconds), SettingKind.Integer, false, []),
        new("METADATA_IDENTITY_MIN_SCORE", nameof(MetadataIdentityMinScore), SettingKind.Number, false, []),
        new("METADATA_IDENTITY_DESCRIPTION_THRESHOLD", nameof(MetadataIdentityDescriptionThreshold), SettingKind.Number, false, []),
        new("METADATA_IDENTITY_QUERY_LIMIT", nameof(MetadataIdentityQueryLimit), SettingKind.Integer, false, []),
        new("METADATA_IDENTITY_RESULTS_PER_PROVIDER", nameof(MetadataIdentityResultsPerProvider), SettingKind.Integer, false, []),
        new("METADATA_IDENTITY_ON_INGEST", nameof(MetadataIdentityOnIngest), SettingKind.Boolean, false, []),
        new("METADATA_IDENTITY_ENTRIES_PER_FEED", nameof(MetadataIdentityEntriesPerFeed), SettingKind.Integer, false, []),
        new("COMICVINE_API_KEY", nameof(ComicvineApiKey), SettingKind.Text, true, []),
        new("SOURCE_REQUEST_TIMEOUT", nameof(SourceRequestTimeout), SettingKind.Number, false, []),
        new("SOURCE_AUTO_CHALLENGES", nameof(SourceAutoChallenges), SettingKind.Boolean, false, []),
        new("HTTP_MAX_CONNECTIONS", nameof(HttpMaxConnections), SettingKind.Integer, false, []),
        new("HTTP_MAX_KEEPALIVE_CONNECTIONS", nameof(HttpMaxKeepaliveConnections), SettingKind.Integer, false, []),
        new("HTTP_KEEPALIVE_EXPIRY_SECONDS", nameof(HttpKeepaliveExpirySeconds), SettingKind.Number, false, []),
        new("HTTP_TRANSPORT_RETRIES", nameof(HttpTransportRetries), SettingKind.Integer, false, []),
        new("FEDERATED_SOURCE_TIMEOUT", nameof(FederatedSourceTimeout), SettingKind.Number, false, []),
        new("FEDERATED_OVERALL_TIMEOUT", nameof(FederatedOverallTimeout), SettingKind.Number, false, []),
        new("FEDERATED_ITEMS_PER_SOURCE", nameof(FederatedItemsPerSource), SettingKind.Integer, false, []),
        new("INTERACTIVE_SOURCE_TIMEOUT", nameof(InteractiveSourceTimeout), SettingKind.Number, false, []),
        new("INTERACTIVE_OVERALL_TIMEOUT", nameof(InteractiveOverallTimeout), SettingKind.Number, false, []),
        new("SEARCH_CACHE_TTL_SECONDS", nameof(SearchCacheTtlSeconds), SettingKind.Integer, false, []),
        new("SEARCH_CACHE_MAX_ENTRIES", nameof(SearchCacheMaxEntries), SettingKind.Integer, false, []),
        new("SUBBED_CACHE_TTL_SECONDS", nameof(SubbedCacheTtlSeconds), SettingKind.Integer, false, []),
        new("COVER_HINT_CACHE_MAX_ENTRIES", nameof(CoverHintCacheMaxEntries), SettingKind.Integer, false, []),
        new("BROWSE_CACHE_REFRESH_SECONDS", nameof(BrowseCacheRefreshSeconds), SettingKind.Integer, false, []),
        new("BROWSE_PREFETCH_PAGES", nameof(BrowsePrefetchPages), SettingKind.Integer, false, []),
        new("BACKGROUND_REFRESH_ON_STARTUP", nameof(BackgroundRefreshOnStartup), SettingKind.Boolean, false, []),
        new("CONTINUOUS_CATALOG_DISCOVERY", nameof(ContinuousCatalogDiscovery), SettingKind.Boolean, false, []),
        new("CONTINUOUS_CATALOG_INTERVAL_SECONDS", nameof(ContinuousCatalogIntervalSeconds), SettingKind.Number, false, []),
        new("CONTINUOUS_CATALOG_MODES", nameof(ContinuousCatalogModes), SettingKind.Text, false, []),
        new("CONTINUOUS_CATALOG_BACKLOG_LIMIT", nameof(ContinuousCatalogBacklogLimit), SettingKind.Integer, false, []),
        new("STARTUP_REFRESH_DELAY_SECONDS", nameof(StartupRefreshDelaySeconds), SettingKind.Number, false, []),
        new("BACKGROUND_DEDUP_ON_STARTUP", nameof(BackgroundDedupOnStartup), SettingKind.Boolean, false, []),
        new("AUTOMATIC_DUPLICATE_MATCHING", nameof(AutomaticDuplicateMatching), SettingKind.Boolean, false, []),
        new("AUTOMATIC_DUPLICATE_MATCHING_DELAY_SECONDS", nameof(AutomaticDuplicateMatchingDelaySeconds), SettingKind.Number, false, []),
        new("DUPLICATE_SIGNATURE_MAX_BUCKET", nameof(DuplicateSignatureMaxBucket), SettingKind.Integer, false, []),
        new("DUPLICATE_SNAPSHOT_TTL_SECONDS", nameof(DuplicateSnapshotTtlSeconds), SettingKind.Integer, false, []),
        new("DUPLICATE_AUTO_MERGE_MIN_EXACT_TITLES", nameof(DuplicateAutoMergeMinExactTitles), SettingKind.Integer, false, []),
        new("DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD", nameof(DuplicateAutoMergeSummaryThreshold), SettingKind.Number, false, []),
        new("DUPLICATE_AUTO_MERGE_MIN_SUMMARY_CHARS", nameof(DuplicateAutoMergeMinSummaryChars), SettingKind.Integer, false, []),
        new("EXACT_THUMBNAIL_MATCHING", nameof(ExactThumbnailMatching), SettingKind.Boolean, false, []),
        new("THUMBNAIL_FINGERPRINT_TTL_SECONDS", nameof(ThumbnailFingerprintTtlSeconds), SettingKind.Integer, false, []),
        new("THUMBNAIL_FINGERPRINT_TIMEOUT", nameof(ThumbnailFingerprintTimeout), SettingKind.Number, false, []),
        new("THUMBNAIL_FINGERPRINT_ENTRIES_PER_PASS", nameof(ThumbnailFingerprintEntriesPerPass), SettingKind.Integer, false, []),
        new("REFRESH_SOURCE_TIMEOUT", nameof(RefreshSourceTimeout), SettingKind.Number, false, []),
        new("REFRESH_OVERALL_TIMEOUT", nameof(RefreshOverallTimeout), SettingKind.Number, false, []),
        new("COVER_FETCH_TIMEOUT", nameof(CoverFetchTimeout), SettingKind.Number, false, []),
        new("COVER_INLINE_WAIT", nameof(CoverInlineWait), SettingKind.Number, false, []),
        new("COVER_CACHE_TTL_SECONDS", nameof(CoverCacheTtlSeconds), SettingKind.Integer, false, []),
        new("COVER_FETCH_CONCURRENCY", nameof(CoverFetchConcurrency), SettingKind.Integer, false, []),
        new("METADATA_ENRICHMENT_ENTRIES_PER_FEED", nameof(MetadataEnrichmentEntriesPerFeed), SettingKind.Integer, false, []),
        new("METADATA_ENRICHMENT_CONCURRENCY", nameof(MetadataEnrichmentConcurrency), SettingKind.Integer, false, []),
        new("METADATA_ENRICHMENT_SOURCE_LIMIT", nameof(MetadataEnrichmentSourceLimit), SettingKind.Integer, false, []),
        new("METADATA_ENRICHMENT_TIMEOUT", nameof(MetadataEnrichmentTimeout), SettingKind.Number, false, []),
        new("METADATA_ENRICHMENT_ON_STARTUP", nameof(MetadataEnrichmentOnStartup), SettingKind.Boolean, false, []),
        new("METADATA_ENRICHMENT_STARTUP_DELAY_SECONDS", nameof(MetadataEnrichmentStartupDelaySeconds), SettingKind.Number, false, []),
        new("METADATA_ENRICHMENT_STARTUP_LIMIT", nameof(MetadataEnrichmentStartupLimit), SettingKind.Integer, false, []),
        new("CHAPTER_PAGE_CACHE_TTL_SECONDS", nameof(ChapterPageCacheTtlSeconds), SettingKind.Integer, false, []),
        new("PAGE_INFO_CACHE_MAX_ENTRIES", nameof(PageInfoCacheMaxEntries), SettingKind.Integer, false, []),
        new("QUALITY_ANALYSIS_CONCURRENCY", nameof(QualityAnalysisConcurrency), SettingKind.Integer, false, []),
        new("QUALITY_SAMPLE_TIMEOUT", nameof(QualitySampleTimeout), SettingKind.Number, false, []),
        new("QUALITY_OVERALL_TIMEOUT", nameof(QualityOverallTimeout), SettingKind.Number, false, []),
        new("COLOR_DETECTION_ENABLED", nameof(ColorDetectionEnabled), SettingKind.Boolean, false, []),
        new("COLOR_DETECTION_SAMPLE_PAGES", nameof(ColorDetectionSamplePages), SettingKind.Integer, false, []),
        new("COLOR_DETECTION_COLORED_PIXEL_FRACTION", nameof(ColorDetectionColoredPixelFraction), SettingKind.Number, false, []),
        new("COLOR_DETECTION_MIN_CONFIDENCE", nameof(ColorDetectionMinConfidence), SettingKind.Number, false, []),
        new("COLOR_DETECTION_TIMEOUT", nameof(ColorDetectionTimeout), SettingKind.Number, false, []),
        new("MANUAL_SEARCH_TIMEOUT", nameof(ManualSearchTimeout), SettingKind.Number, false, []),
        new("MANUAL_SEARCH_OVERALL_TIMEOUT", nameof(ManualSearchOverallTimeout), SettingKind.Number, false, []),
        new("MANUAL_SEARCH_ALIAS_LIMIT", nameof(ManualSearchAliasLimit), SettingKind.Integer, false, []),
        new("MANUAL_SEARCH_LIVE_QUERY_LIMIT", nameof(ManualSearchLiveQueryLimit), SettingKind.Integer, false, []),
        new("AUTO_MISSING_SOURCE_DISCOVERY", nameof(AutoMissingSourceDiscovery), SettingKind.Boolean, false, []),
        new("AUTO_MISSING_SOURCE_ENTRIES_PER_FEED", nameof(AutoMissingSourceEntriesPerFeed), SettingKind.Integer, false, []),
        new("AUTO_MISSING_SOURCE_SOURCE_LIMIT", nameof(AutoMissingSourceSourceLimit), SettingKind.Integer, false, []),
        new("SOURCE_RECOVERY_TIMEOUT_SECONDS", nameof(SourceRecoveryTimeoutSeconds), SettingKind.Number, false, []),
        new("SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS", nameof(SubbedSearchEntryTimeoutSeconds), SettingKind.Number, false, []),
        new("AUTO_VARIANT_DISCOVERY", nameof(AutoVariantDiscovery), SettingKind.Boolean, false, []),
        new("AUTO_VARIANT_MIN_SCORE", nameof(AutoVariantMinScore), SettingKind.Number, false, []),
        new("AUTO_VARIANT_QUERY_LIMIT", nameof(AutoVariantQueryLimit), SettingKind.Integer, false, []),
        new("AUTO_VARIANT_ENTRIES_PER_FEED", nameof(AutoVariantEntriesPerFeed), SettingKind.Integer, false, []),
        new("AUTO_VARIANT_SOURCE_TIMEOUT", nameof(AutoVariantSourceTimeout), SettingKind.Number, false, []),
        new("AUTO_VARIANT_OVERALL_TIMEOUT", nameof(AutoVariantOverallTimeout), SettingKind.Number, false, []),
        new("AUTO_VARIANT_STARTUP_ENTRIES", nameof(AutoVariantStartupEntries), SettingKind.Integer, false, []),
        new("AUTO_VARIANT_STARTUP_DELAY_SECONDS", nameof(AutoVariantStartupDelaySeconds), SettingKind.Number, false, []),
        new("MANUAL_TRANSLATION_QUERY_LIMIT", nameof(ManualTranslationQueryLimit), SettingKind.Integer, false, []),
        new("MANUAL_TRANSLATE_CANDIDATE_LIMIT", nameof(ManualTranslateCandidateLimit), SettingKind.Integer, false, []),
        new("TRANSLATION_API_URL", nameof(TranslationApiUrl), SettingKind.Text, false, []),
        new("TRANSLATION_API_KEY", nameof(TranslationApiKey), SettingKind.Text, true, []),
        new("TRANSLATION_MODE", nameof(TranslationMode), SettingKind.Text, false, ["auto", "builtin", "external", "off"]),
        new("TRANSLATION_TIMEOUT", nameof(TranslationTimeout), SettingKind.Number, false, []),
        new("TRANSLATION_OVERALL_TIMEOUT", nameof(TranslationOverallTimeout), SettingKind.Number, false, []),
        new("TRANSLATION_CONCURRENCY", nameof(TranslationConcurrency), SettingKind.Integer, false, []),
        new("TRANSLATION_TARGET_LANGUAGES", nameof(TranslationTargetLanguages), SettingKind.Text, false, []),
        new("BACKGROUND_TRANSLATION_ON_DISCOVERY", nameof(BackgroundTranslationOnDiscovery), SettingKind.Boolean, false, []),
        new("BACKGROUND_TRANSLATION_ENTRIES_PER_FEED", nameof(BackgroundTranslationEntriesPerFeed), SettingKind.Integer, false, []),
        new("BACKGROUND_TRANSLATION_ENTRIES_PER_STARTUP", nameof(BackgroundTranslationEntriesPerStartup), SettingKind.Integer, false, []),
        new("TRANSLATION_STARTUP_DELAY_SECONDS", nameof(TranslationStartupDelaySeconds), SettingKind.Number, false, []),
        new("SOURCE_FAILURE_THRESHOLD", nameof(SourceFailureThreshold), SettingKind.Integer, false, []),
        new("SOURCE_COOLDOWN_SECONDS", nameof(SourceCooldownSeconds), SettingKind.Number, false, []),
        new("SOURCE_MIN_INTERVAL_MS", nameof(SourceMinIntervalMs), SettingKind.Integer, false, []),
        new("BACKGROUND_IDLE_SECONDS", nameof(BackgroundIdleSeconds), SettingKind.Number, false, []),
        new("SQLITE_CACHE_MB", nameof(SqliteCacheMb), SettingKind.Integer, false, []),
        new("SQLITE_BUSY_TIMEOUT_MS", nameof(SqliteBusyTimeoutMs), SettingKind.Integer, false, []),
        new("SOURCE_LANGUAGES", nameof(SourceLanguages), SettingKind.Text, false, []),
        new("ADMIN_TOKEN", nameof(AdminToken), SettingKind.Text, true, []),
        new("AUTOSOURCARR_READER_API_TOKEN", nameof(AutosourcarrReaderApiToken), SettingKind.Text, true, []),
        new("AUTHELIA_USER_HEADER", nameof(AutheliaUserHeader), SettingKind.Text, false, []),
        new("AUTH_METHOD", nameof(AuthMethod), SettingKind.Text, false, ["forms", "basic", "external"]),
        new("AUTH_ADMIN_GROUPS", nameof(AuthAdminGroups), SettingKind.Text, false, []),
        new("AUTH_GUEST_GROUPS", nameof(AuthGuestGroups), SettingKind.Text, false, []),
        new("AUTH_OWNER_EXTERNAL_USER", nameof(AuthOwnerExternalUser), SettingKind.Text, false, []),
        new("AUTH_TRUSTED_PROXIES", nameof(AuthTrustedProxies), SettingKind.Text, false, []),
        new("AUTH_RESET_OWNER", nameof(AuthResetOwner), SettingKind.Boolean, false, []),
    ];
}
