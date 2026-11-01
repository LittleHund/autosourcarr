import io.github.keiyoushi.gradle.api.ContentWarning
import java.security.MessageDigest

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

// READ-1: one prebuilt extension with several server slots, like the Komga extension. Each slot is its own
// source with its own address and device key in the reader's source settings. Mihon builds an extension's
// sources once, so the slot count is fixed per build: 5 by default, more with AUTOSOURCARR_READER_SLOTS (max 20).
val readerSlots = providers.environmentVariable("AUTOSOURCARR_READER_SLOTS").orElse("5").get().trim().toIntOrNull()?.coerceIn(1, 20) ?: 5

fun slotName(slot: Int): String = if (slot == 1) "AutoSourcarr" else "AutoSourcarr ($slot)"

// Stable ids: Keiyoushi's own name/lang/versionId digest, pinned per slot so a later display-name change
// cannot move a library. Slot 1 keeps the id every earlier AutoSourcarr build had.
fun slotSourceId(slot: Int): Long {
    // Imported: inside the Gradle Kotlin DSL a bare `java` is the `java` extension, not the package.
    val bytes = MessageDigest.getInstance("MD5").digest("${slotName(slot).lowercase()}/all/1".toByteArray())
    return (0..7).map { index -> bytes[index].toLong() and 0xffL }.reduce { acc: Long, value: Long -> (acc shl 8) or value } and Long.MAX_VALUE
}

keiyoushi {
    // Suwayomi 2.3.x compatibility mode: use the legacy HttpSource ABI instead of
    // KeiSource so Suwayomi does not have to verify KeiSource bytecode when loading the extension JAR.
    name = "AutoSourcarr"
    versionCode = autosourcarrVersionCode
    contentWarning = ContentWarning.MIXED
    libVersion = "1.4"

    for (slot in 1..readerSlots) {
        source {
            // Keep the source names stable so the source ids stay stable.
            // Keiyoushi/Suwayomi shows the generated 1.4.<versionCode> separately.
            name = slotName(slot)
            lang = "all"
            // Metadata only: every slot reads its address from its own settings.
            baseUrl = autosourcarrBaseUrl
            id = slotSourceId(slot)
        }
    }
}
