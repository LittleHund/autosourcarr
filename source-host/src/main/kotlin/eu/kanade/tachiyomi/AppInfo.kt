package eu.kanade.tachiyomi

import suwayomi.tachidesk.manga.impl.util.storage.ImageUtil
import autosourcarr.host.HostConfig

/**
 * Used by extensions.
 *
 * @since extension-lib 1.3
 */
object AppInfo {
    /**
     *
     * should be something like 74
     *
     * @since extension-lib 1.3
     */
    fun getVersionCode() =
        HostConfig.COMPAT_VERSION
            .replace("v", "")
            .split('.')
            .joinToString("")
            .toInt()

    /**
     * should be something like "0.13.1"
     *
     * @since extension-lib 1.3
     */
    fun getVersionName() = HostConfig.COMPAT_VERSION.substring(1)

    /**
     * A list of supported image MIME types by the reader.
     * e.g. ["image/jpeg", "image/png", ...]
     *
     * @since extension-lib 1.5
     */
    fun getSupportedImageMimeTypes(): List<String> = ImageUtil.ImageType.entries.map { it.mime }
}
