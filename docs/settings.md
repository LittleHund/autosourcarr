# AutoSourcarr settings

Generated from the settings schema (`src/AutoSourcarr.Core/Settings`); do not edit by hand. CI regenerates this file and fails when it differs.

Settings are stored in `config.json` in the config folder and edited on the Settings page or in the setup wizard. Secrets are stored encrypted with `config.key`; keep both files together in backups.

## Compose overrides

Any setting can be set by an environment variable with the same name in Compose. A Compose variable wins over the built-in default and over values in `config.json` that you never changed in the app; a value you saved on the Settings page or in the setup wizard wins over Compose (as wizard values did in 0.4.x), and the Settings page shows which settings Compose currently decides. Use the page's *Use default* to hand a setting back to Compose. Settings marked *Compose only* can only be set in Compose, because they name container folders or identities. Keep Compose small: set only the variables marked *Compose* or *Compose only*, and remove the old `env_file:` line once `autosourcarr.env` has been converted.

Precedence, lowest first: built-in default, `config.json`, Compose environment variable, value saved in the app.

## Runtime

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `APP_SERVICES_MODE` | choice (integrated, external) | `external` | integrated runs the bundled helper services inside this container; external keeps separately deployed helpers (0.4.x layouts). | Compose only |  |
| `DATABASE_DIR` | path | empty | Folder for the SQLite databases. Empty uses the data folder. Set it in Compose only, pointing at a mounted folder that already holds the databases; moving them from the app arrives with the new data layer (conversion phase 2). | Compose only |  |
| `DATA_DIR` | path | `/data` | Folder inside the container for covers, caches and runtime data. Mount it as a persistent volume. | Compose only |  |
| `SETTINGS_CONFIG_DIR` | path | `/config` | Folder inside the container that holds config.json, config.key and setup state. Mount it as a persistent volume. | Compose only |  |
| `SOURCE_ENGINE_GID` | integer | `0` | Group ID the source engine runs as. 0 picks the group of the existing source folder. | Compose only |  |
| `SOURCE_ENGINE_MODE` | choice (integrated, external) | `integrated` | integrated runs sources inside this image; external uses a separate source host (rollback and development only). | Compose only |  |
| `SOURCE_ENGINE_UID` | integer | `0` | User ID the source engine runs as. 0 picks the owner of the existing source folder (TrueNAS often uses 568). | Compose only |  |
| `TZ` | text | `Europe/Amsterdam` | Used for readable times and scheduled work. Example: Europe/Amsterdam. | Compose | library |

## Security

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `ADMIN_TOKEN` | secret | — | Optional protection for administrative actions. After a restart, the app asks for the new token. Keep a copy before saving; this is separate from the reader key. |  | access |
| `AUTHELIA_BROWSER_URL` | url | empty | Optional link to your Authelia or other authentication portal. Sign in there; AutoSourcarr does not store that account password. |  | access |
| `AUTH_ADMIN_GROUPS` | text | `admins` | External sign-in: comma-separated provider groups whose members get the admin role (for example admins). Groups never make someone the owner. |  |  |
| `AUTH_GUEST_GROUPS` | text | empty | External sign-in: comma-separated provider groups whose members get the read-only guest role. Leave empty to refuse users who are in no admin group. |  |  |
| `AUTH_METHOD` | choice (forms, basic, external) | `forms` | How people sign in: forms (login page, the default), basic (the browser's sign-in pop-up) or external (a sign-in proxy such as Authelia sends the user name and groups in headers). API keys and the admin token work with every method. |  |  |
| `AUTH_OWNER_EXTERNAL_USER` | text | empty | External sign-in: the provider user name bound as owner on first boot. Only this account can become owner; group mappings never can. |  |  |
| `AUTH_RESET_OWNER` | boolean | `false` | Last-resort owner reset: set to true in Compose and restart once when the owner has lost the password and every recovery code. That start clears the owner's password (data is kept) and writes a one-time setup code to the server log; the login page asks for it and a new password. It applies once per use of the variable; remove it afterwards. | Compose only |  |
| `AUTH_TRUSTED_PROXIES` | text | empty | Addresses of your reverse proxy (comma-separated IPs or CIDR ranges, for example 172.18.0.0/16). Only requests from them may name the user (Remote-User, Remote-Groups) for external sign-in or give the client address and https (X-Forwarded-For, X-Forwarded-Proto). Empty: loopback and private networks are trusted; set it when the app's port can be reached from other machines. |  |  |
| `KOMF_BROWSER_URL` | url | empty | Optional shortcut to an existing Komf web interface. Komf stays separate and uses its own configuration and credentials. |  | links |
| `KOMGA_BROWSER_URL` | url | empty | Optional shortcut to your Komga library. Sign in on Komga itself; this does not configure a metadata integration. |  | links |
| `MAIN_SUWAYOMI_PASSWORD` | secret | — | Used only for requests to the configured main Suwayomi origin. Never sent to source websites. |  | library |
| `MAIN_SUWAYOMI_USERNAME` | text | empty | Optional HTTP Basic authentication for the main Suwayomi connection. Leave empty when authentication is disabled; browser form login is separate. |  | library |
| `REPAIR_AI_API_KEY` | secret | — | Optional for a local server without authentication. The key stays with the connection service and is not given to repair code. |  | repair |
| `REPAIR_AI_BASE_URL` | url | empty | The selected provider API base address, usually ending in /v1. Use an address reachable from AutoSourcarr; localhost means this container. |  | repair |
| `REPAIR_AI_BROWSER_URL` | url | empty | Optional browser link to your AI server or provider dashboard. This does not change the API address or sign you in. |  | repair |
| `REPAIR_AI_MODEL` | text | empty | The exact model ID available to your account or local server. Choose a coding model with tool calling. Provider presets fill the address, not a model you may not have access to. |  | repair |
| `REPAIR_AI_PROTOCOL` | choice (responses, chat_completions, anthropic) | `responses` | Choose the format your custom server actually supports. The model must support tool calling. |  | repair |
| `REPAIR_AI_PROVIDER` | choice (chatgpt, openai, claude, gemini, openrouter, ollama, custom) | `chatgpt` | ChatGPT uses official account sign-in. Other providers use their API connection; consumer subscriptions do not supply API credit. No extra repair container is needed. |  | repair |
| `REPAIR_AI_WORKSPACE_ID` | text | empty | Optional for a workspace-scoped Claude key; required for a multi-workspace key. Copy the ID from Claude Console → Settings → Workspaces. This selects the API billing workspace, not your chat account. |  | repair |
| `SUWAYOMI_BROWSER_URL` | url | empty | Optional address you open on your computer, such as http://server:30058. This is separate from the Docker service address. |  | library |
| `TRANSLATION_BROWSER_URL` | url | empty | Optional link to your separate translation server or account dashboard. The built-in translator has no separate login or browser interface. |  | tools |
| `TRANSLATION_MODE` | choice (auto, builtin, external, off) | `auto` | Built-in models run inside this instance and load on demand. External mode uses the address below. |  | tools |

## Connections & languages

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `AUTOSOURCARR_INTERNAL_URL` | url | `http://autosourcarr:8282` | Baked into this instance’s JAR. Normally http://autosourcarr:8282; for the private instance use http://private-autosourcarr:8282. | Compose | readers |
| `AUTOSOURCARR_PUBLIC_URL` | url | `http://127.0.0.1:8282` | URL reachable from Mihon/Tachiyomi. Use HTTPS for remote access. It must point to this same AutoSourcarr instance. | Compose | readers |
| `AUTOSOURCARR_READER_API_TOKEN` | secret | — | Keep the saved key or enter a replacement of 32–256 letters, digits, dots, tildes, dashes or underscores. Changing it requires rebuilding and reinstalling the APK. |  | readers |
| `MAIN_SUWAYOMI_DOWNLOADS_PATH` | path | empty | Optional existing container path used to report downloaded sizes. This does not move files or create a Docker mount. |  | library |
| `MAIN_SUWAYOMI_URL` | url | empty | The reader server that owns this instance’s library. Use its Docker service name and internal port, for example http://suwayomi:30058. |  | library |
| `SOURCE_LANGUAGES` | languages | `en,all` | Comma-separated codes: en for English, all for multilingual. Existing source selections stay unchanged; manage them in Sources. |  | sources |
| `SOURCE_SUWAYOMI_URL` | text | `http://autosourcarr-source-host:4567` | Address of the external source host when the source engine runs in external mode. |  |  |
| `TRANSLATION_API_KEY` | secret | — | Only needed if your separate translation server requires a key. The built-in service does not require one. |  | tools |
| `TRANSLATION_API_URL` | translation | empty | Turn built-in translation on or off. Models load on demand and are cached; translated aliases do not prove two works are identical. |  | tools |

## Additional source records

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `AUTO_VARIANT_DISCOVERY` | boolean | `true` | Look for the same work on other sources in the background. |  |  |
| `AUTO_VARIANT_ENTRIES_PER_FEED` | integer | `20` | Most entries per feed checked for records on other sources. |  |  |
| `AUTO_VARIANT_MIN_SCORE` | number | `0.96` | Lowest title score (0–1) for attaching another source's record automatically. |  |  |
| `AUTO_VARIANT_OVERALL_TIMEOUT` | number | `14` | Total time limit per entry when looking for other records, in seconds. |  |  |
| `AUTO_VARIANT_QUERY_LIMIT` | integer | `6` | Most title variants searched per entry when looking for other sources. |  |  |
| `AUTO_VARIANT_SOURCE_TIMEOUT` | number | `6` | Time limit per source when looking for other records, in seconds. |  |  |
| `AUTO_VARIANT_STARTUP_DELAY_SECONDS` | number | `70` | Wait after start before that check, in seconds. |  |  |
| `AUTO_VARIANT_STARTUP_ENTRIES` | integer | `0` | Entries checked for other records shortly after start (0 = none). |  |  |

## Background refresh

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `AUTHELIA_USER_HEADER` | text | empty | Optional header supplied by your trusted authentication proxy to identify users. Your proxy handles its own login; do not put your Authelia password here. |  | access |
| `AUTOMATIC_DUPLICATE_MATCHING` | boolean | `true` | Merge only when the existing identity and evidence checks agree. Uncertain pairs remain available for manual review. |  | matching |
| `AUTOMATIC_DUPLICATE_MATCHING_DELAY_SECONDS` | number | `8` | Wait before automatic duplicate matching runs after a change, in seconds. |  |  |
| `BACKGROUND_DEDUP_ON_STARTUP` | boolean | `true` | Check for duplicate entries shortly after start. |  |  |
| `BACKGROUND_REFRESH_ON_STARTUP` | boolean | `false` | Refresh source feeds shortly after start. |  |  |
| `CONTINUOUS_CATALOG_BACKLOG_LIMIT` | integer | `80` | Most feed pages waiting in the background catalogue queue. |  |  |
| `CONTINUOUS_CATALOG_DISCOVERY` | boolean | `true` | Collect source feeds at low priority so later browsing can use saved entries. Existing source pacing still applies. |  | matching |
| `CONTINUOUS_CATALOG_INTERVAL_SECONDS` | number | `8` | Pause between background catalogue feed requests, in seconds. |  |  |
| `CONTINUOUS_CATALOG_MODES` | text | `LATEST,POPULAR` | Feeds the background catalogue reads: LATEST, POPULAR or both, comma-separated. |  |  |
| `EXACT_THUMBNAIL_MATCHING` | boolean | `true` | Use identical cover images as matching evidence. |  |  |
| `STARTUP_REFRESH_DELAY_SECONDS` | number | `20` | Wait after start before the first background refresh, in seconds. |  |  |
| `THUMBNAIL_FINGERPRINT_ENTRIES_PER_PASS` | integer | `500` | Most entries fingerprinted per background pass. |  |  |
| `THUMBNAIL_FINGERPRINT_TIMEOUT` | number | `8` | Time limit for downloading a cover to fingerprint it, in seconds. |  |  |
| `THUMBNAIL_FINGERPRINT_TTL_SECONDS` | integer | `2592000` | How long a cover fingerprint is kept, in seconds. |  |  |

## Browse & cache

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `BROWSE_CACHE_REFRESH_SECONDS` | integer | `600` | Age after which a saved browse page is refreshed in the background, in seconds. |  |  |
| `BROWSE_PAGE_SIZE` | integer | `50` | Smaller pages render sooner; larger pages show more entries at once. |  | performance |
| `BROWSE_PREFETCH_PAGES` | integer | `2` | How many following browse pages are loaded ahead. |  |  |
| `FEDERATED_ITEMS_PER_SOURCE` | integer | `30` | Most results taken from each source in a combined browse or search. |  |  |
| `FEDERATED_OVERALL_TIMEOUT` | number | `14` | Bound live multi-source work; previously cached entries remain available. |  | performance |
| `FEDERATED_SOURCE_TIMEOUT` | number | `8` | Time limit per source when browsing or searching several sources, in seconds. |  |  |
| `INTERACTIVE_OVERALL_TIMEOUT` | number | `6` | Total time limit for an interactive multi-source request, in seconds. |  |  |
| `INTERACTIVE_SOURCE_TIMEOUT` | number | `4` | Time limit per source for requests you are waiting on in the UI, in seconds. |  |  |
| `SEARCH_CACHE_MAX_ENTRIES` | integer | `128` | Most search results kept in memory. |  |  |
| `SEARCH_CACHE_TTL_SECONDS` | integer | `20` | How long search results are reused, in seconds. |  |  |
| `SUBBED_CACHE_TTL_SECONDS` | integer | `10` | How long the subscribed-library view is reused, in seconds. |  |  |

## Duplicate detection

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `DUPLICATE_AUTO_MERGE_MIN_EXACT_TITLES` | integer | `2` | Exact title matches needed before two entries can merge automatically. |  |  |
| `DUPLICATE_AUTO_MERGE_MIN_SUMMARY_CHARS` | integer | `80` | Shortest summary, in characters, that counts as merge evidence. |  |  |
| `DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD` | number | `0.7` | Lowest summary similarity (0–1) for an automatic merge. |  |  |
| `DUPLICATE_SIGNATURE_MAX_BUCKET` | integer | `24` | Largest group of similar titles compared in one duplicate check. |  |  |
| `DUPLICATE_SNAPSHOT_TTL_SECONDS` | integer | `60` | How long the duplicate list is reused before it is rebuilt, in seconds. |  |  |

## Manual matching

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `AUTO_MISSING_SOURCE_DISCOVERY` | boolean | `true` | Search for sources missing from an entry while retaining the strict matching rules. |  | matching |
| `AUTO_MISSING_SOURCE_ENTRIES_PER_FEED` | integer | `10` | Most entries per feed checked for sources they are still missing. |  |  |
| `AUTO_MISSING_SOURCE_SOURCE_LIMIT` | integer | `8` | Most sources searched per entry for a missing source. |  |  |
| `MANUAL_SEARCH_ALIAS_LIMIT` | integer | `30` | Most alternative titles tried in a manual search. |  |  |
| `MANUAL_SEARCH_LIVE_QUERY_LIMIT` | integer | `4` | Most live source queries per manual search. |  |  |
| `MANUAL_SEARCH_OVERALL_TIMEOUT` | number | `24` | Total time limit for a manual match search, in seconds. |  |  |
| `MANUAL_SEARCH_TIMEOUT` | number | `12` | Time limit per source for a manual match search, in seconds. |  |  |
| `MANUAL_TRANSLATE_CANDIDATE_LIMIT` | integer | `12` | Most search results translated for comparison in a manual search. |  |  |
| `MANUAL_TRANSLATION_QUERY_LIMIT` | integer | `6` | Most translated titles searched in a manual search. |  |  |
| `SOURCE_RECOVERY_TIMEOUT_SECONDS` | number | `45` | Time budget for recovering one slow source or item; finished work is kept and the rest stays retryable. |  |  |
| `SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS` | number | `120` | Time limit for searching sources for one subscribed entry, in seconds. |  |  |

## Matching

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `AUTO_MATCH_THRESHOLD` | number | `0.94` | Higher values are stricter. The application never allows the automatic threshold below 0.94. |  | matching |
| `BACKGROUND_SOURCE_CONCURRENCY` | integer | `1` | Keep this below total capacity so interactive browsing and manual actions have room. |  | performance |
| `BACKGROUND_TASK_CONCURRENCY` | integer | `2` | How many background tasks run at the same time. |  |  |
| `FUZZY_REVIEW_THRESHOLD` | number | `0.78` | Lower-scoring suggestions may appear for review; they are not automatically accepted. |  | matching |
| `IMPORT_RESOLVE_CONCURRENCY` | integer | `2` | Bound concurrent import matching work; manual actions stay separate from automatic queues. |  | performance |
| `MAX_SOURCE_CONCURRENCY` | integer | `6` | Maximum independent source requests. More is not always faster; keep conservative limits to avoid upstream timeouts. |  | performance |
| `SOURCE_HOST_WORK_CONCURRENCY` | integer | `4` | Shared source-engine work limit. Background work retains its own smaller allowance. |  | performance |

## Metadata & covers

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `COVER_CACHE_TTL_SECONDS` | integer | `604800` | How long a downloaded cover is kept before it is checked again, in seconds. |  |  |
| `COVER_FETCH_CONCURRENCY` | integer | `4` | How many covers are downloaded at the same time. |  |  |
| `COVER_FETCH_TIMEOUT` | number | `5` | Time limit for downloading a cover, in seconds. |  |  |
| `COVER_HINT_CACHE_MAX_ENTRIES` | integer | `10000` | Most cover addresses remembered in memory. |  |  |
| `COVER_INLINE_WAIT` | number | `0.35` | How long a page waits for a cover before showing a placeholder, in seconds. |  |  |
| `METADATA_ENRICHMENT_CONCURRENCY` | integer | `4` | How many metadata lookups run at the same time. |  |  |
| `METADATA_ENRICHMENT_ENTRIES_PER_FEED` | integer | `30` | Most entries per feed that get extra metadata in the background. |  |  |
| `METADATA_ENRICHMENT_ON_STARTUP` | boolean | `false` | Fill in missing metadata shortly after start. |  |  |
| `METADATA_ENRICHMENT_SOURCE_LIMIT` | integer | `3` | Most sources asked for details of one entry. |  |  |
| `METADATA_ENRICHMENT_STARTUP_DELAY_SECONDS` | number | `8` | Wait after start before the metadata pass, in seconds. |  |  |
| `METADATA_ENRICHMENT_STARTUP_LIMIT` | integer | `500` | Most entries handled by the start-up metadata pass. |  |  |
| `METADATA_ENRICHMENT_TIMEOUT` | number | `10` | Time limit for one metadata lookup, in seconds. |  |  |
| `METADATA_TTL_SECONDS` | integer | `21600` | How long fetched metadata is kept before it is refreshed, in seconds. |  |  |

## Metadata identity

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `COMICVINE_API_KEY` | secret | — | Optional. Other built-in metadata lookups use public APIs and need no login. Comic Vine needs a key from its API account page. |  | matching |
| `METADATA_IDENTITY_CACHE_TTL_SECONDS` | integer | `604800` | How long identity lookup results are cached, in seconds. |  |  |
| `METADATA_IDENTITY_CONCURRENCY` | integer | `3` | How many identity lookups run at the same time. |  |  |
| `METADATA_IDENTITY_DESCRIPTION_THRESHOLD` | number | `0.82` | Lowest summary similarity (0–1) used as supporting identity evidence. |  |  |
| `METADATA_IDENTITY_ENABLED` | boolean | `true` | Consult supported identity databases to improve names, aliases and metadata. They do not supply chapter pages. |  | matching |
| `METADATA_IDENTITY_ENTRIES_PER_FEED` | integer | `24` | Most new entries per source feed that get an identity lookup. |  |  |
| `METADATA_IDENTITY_INCLUDE_ADULT` | boolean | `true` | Allow identity providers to return adult entries when applicable; this does not install or enable sources. |  | matching |
| `METADATA_IDENTITY_MIN_SCORE` | number | `0.94` | Lowest title score (0–1) for a provider result to count as the same work. |  |  |
| `METADATA_IDENTITY_ON_INGEST` | boolean | `true` | Look up the identity of new entries when they are first added. |  |  |
| `METADATA_IDENTITY_OVERALL_TIMEOUT` | number | `12` | Time limit for one identity lookup across all providers, in seconds. |  |  |
| `METADATA_IDENTITY_PROVIDERS` | providers | `mangadex,anilist,mangaupdates,kitsu,myanimelist,comicvine` | Comma-separated provider identifiers. Keep the supplied set unless you intentionally want fewer external lookups. |  | matching |
| `METADATA_IDENTITY_QUERY_LIMIT` | integer | `3` | Most title variants searched per identity lookup. |  |  |
| `METADATA_IDENTITY_RESULTS_PER_PROVIDER` | integer | `6` | Most results read from each provider per search. |  |  |
| `METADATA_IDENTITY_TIMEOUT` | number | `8` | Time limit for one metadata provider request, in seconds. |  |  |

## Performance & resilience

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `BACKGROUND_IDLE_SECONDS` | number | `2` | Quiet time after your last action before background work resumes, in seconds. |  |  |
| `HTTP_KEEPALIVE_EXPIRY_SECONDS` | number | `30` | How long an idle outgoing connection is kept open, in seconds. |  |  |
| `HTTP_MAX_CONNECTIONS` | integer | `64` | Most open outgoing HTTP connections in total. |  |  |
| `HTTP_MAX_KEEPALIVE_CONNECTIONS` | integer | `32` | Most idle outgoing HTTP connections kept open for reuse. |  |  |
| `HTTP_TRANSPORT_RETRIES` | integer | `1` | How often a request is retried after a connection error (not after an HTTP error). |  |  |
| `SOURCE_COOLDOWN_SECONDS` | number | `120` | How long a failing source pauses, in seconds. |  |  |
| `SOURCE_FAILURE_THRESHOLD` | integer | `3` | Failures in a row after which a source pauses. |  |  |
| `SOURCE_MIN_INTERVAL_MS` | integer | `250` | Least time between two requests the source host sends to the same website, in milliseconds. Keeps traffic paced so sites do not rate-limit or block you; 0 turns pacing off. Applies after the source host restarts. |  |  |
| `SOURCE_REQUEST_TIMEOUT` | number | `45` | Maximum upstream request time. Raising this cannot repair an unavailable website. |  | performance |
| `SQLITE_BUSY_TIMEOUT_MS` | integer | `3000` | How long a database write waits for a lock before failing, in milliseconds. |  |  |
| `SQLITE_CACHE_MB` | integer | `64` | Memory SQLite may use as page cache, in MB. |  |  |

## Quality analysis

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `COLOR_DETECTION_COLORED_PIXEL_FRACTION` | number | `0.06` | Share of coloured pixels (0–1) above which a page counts as colour. |  |  |
| `COLOR_DETECTION_ENABLED` | boolean | `true` | Use page evidence when available to keep colored and monochrome editions separate. |  | matching |
| `COLOR_DETECTION_MIN_CONFIDENCE` | number | `0.78` | Lowest confidence (0–1) needed to label a source colour or black-and-white. |  |  |
| `COLOR_DETECTION_SAMPLE_PAGES` | integer | `4` | Pages sampled to decide whether a chapter is in colour. |  |  |
| `COLOR_DETECTION_TIMEOUT` | number | `8` | Time limit for the colour check of one chapter, in seconds. |  |  |
| `QUALITY_ANALYSIS_CONCURRENCY` | integer | `2` | On-demand page sampling uses this bounded capacity. It is not run on every browse entry. |  | tools |
| `QUALITY_OVERALL_TIMEOUT` | number | `60` | Total time limit for scoring one chapter, in seconds. |  |  |
| `QUALITY_SAMPLE_TIMEOUT` | number | `20` | Time limit for downloading one sample page for the quality score, in seconds. |  |  |

## Refresh & chapters

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `CHAPTER_PAGE_CACHE_TTL_SECONDS` | integer | `1800` | How long a chapter's page list is reused, in seconds. |  |  |
| `PAGE_INFO_CACHE_MAX_ENTRIES` | integer | `4096` | Most page lists kept in memory. |  |  |
| `REFRESH_OVERALL_TIMEOUT` | number | `25` | Total time limit for refreshing one entry, in seconds. |  |  |
| `REFRESH_SOURCE_TIMEOUT` | number | `12` | Time limit per source when refreshing an entry, in seconds. |  |  |

## Repair

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `REPAIR_WORKER_TOKEN` | secret | — | Shared secret for the external repair worker. Not needed with the integrated image. |  |  |
| `REPAIR_WORKER_URL` | text | empty | Address of an external repair worker. Leave empty with the integrated image. |  |  |

## Sources

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `SOURCE_AUTO_CHALLENGES` | boolean | `true` | Use supported extension handlers and the bundled solver. Unresolved CAPTCHA/WAF pauses the website and asks for help, with at most one automatic retry an hour. |  | sources |

## Translation

| Variable | Type | Default | Description | Compose | Wizard step |
| --- | --- | --- | --- | --- | --- |
| `BACKGROUND_TRANSLATION_ENTRIES_PER_FEED` | integer | `12` | Most new entries per feed translated in the background. |  |  |
| `BACKGROUND_TRANSLATION_ENTRIES_PER_STARTUP` | integer | `0` | Entries translated shortly after start (0 = none). |  |  |
| `BACKGROUND_TRANSLATION_ON_DISCOVERY` | boolean | `true` | Translate titles of newly found entries in the background. |  |  |
| `TRANSLATION_CONCURRENCY` | integer | `4` | How many translation requests run at the same time. |  |  |
| `TRANSLATION_OVERALL_TIMEOUT` | number | `7` | Total time limit for translating one entry's titles, in seconds. |  |  |
| `TRANSLATION_STARTUP_DELAY_SECONDS` | number | `45` | Wait after start before the translation pass, in seconds. |  |  |
| `TRANSLATION_TARGET_LANGUAGES` | translation_languages | `ja,ko,zh` | Comma-separated ja, ko and/or zh. Known aliases and transliteration still work when translation is off. |  | tools |
| `TRANSLATION_TIMEOUT` | number | `5` | Time limit for one translation request, in seconds. |  |  |

