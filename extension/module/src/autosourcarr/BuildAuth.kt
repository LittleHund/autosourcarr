package eu.kanade.tachiyomi.extension.all.autosourcarr

// The release build keeps these empty: each server slot is configured in the reader's source settings.
// The legacy per-instance builder overwrites this file only inside its isolated build checkout, and the
// values then become server slot 1's defaults. The project copy intentionally contains no reader credential.
internal const val AUTOSOURCARR_API_PREFIX = "/api/v1"
internal const val AUTOSOURCARR_READER_API_KEY = ""
internal const val AUTOSOURCARR_DEFAULT_BASE_URL = ""
