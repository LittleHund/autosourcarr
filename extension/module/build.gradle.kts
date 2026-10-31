import io.github.keiyoushi.gradle.api.ContentWarning

plugins {
    alias(kei.plugins.extension)
}

val autosourcarrVersion = providers.environmentVariable("AUTOSOURCARR_VERSION").orElse("0.4.5").get()
val autosourcarrBaseUrl = providers.environmentVariable("AUTOSOURCARR_EXTENSION_BASE_URL").orElse("http://autosourcarr:8282").get().trimEnd('/')
val versionParts = autosourcarrVersion.split(".").map { it.toIntOrNull() ?: 0 }
val autosourcarrVersionCode = (
    versionParts.getOrElse(0) { 0 } * 1_000_000 +
    versionParts.getOrElse(1) { 0 } * 1_000 +
    versionParts.getOrElse(2) { 0 }
).coerceAtLeast(9)

keiyoushi {
    // Suwayomi 2.3.x compatibility mode: use the legacy HttpSource ABI instead of
    // KeiSource so Suwayomi does not have to verify KeiSource bytecode when loading the extension JAR.
    name = "AutoSourcarr"
    versionCode = autosourcarrVersionCode
    contentWarning = ContentWarning.MIXED
    libVersion = "1.4"

    source {
        // Keep the actual source name stable so Suwayomi's source ID stays stable.
        // Keiyoushi/Suwayomi shows the generated 1.4.<versionCode> separately.
        name = "AutoSourcarr"
        lang = "all"
        baseUrl = autosourcarrBaseUrl
    }
}
