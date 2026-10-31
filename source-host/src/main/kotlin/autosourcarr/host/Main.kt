package autosourcarr.host

import eu.kanade.tachiyomi.App
import eu.kanade.tachiyomi.createAppModule
import eu.kanade.tachiyomi.network.NetworkHelper
import io.github.oshai.kotlinlogging.KotlinLogging
import org.koin.core.context.startKoin
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import xyz.nulldev.androidcompat.AndroidCompat
import xyz.nulldev.androidcompat.AndroidCompatInitializer
import xyz.nulldev.androidcompat.androidCompatModule
import xyz.nulldev.ts.config.CONFIG_PREFIX
import xyz.nulldev.ts.config.configManagerModule

private val logger = KotlinLogging.logger {}

/**
 * Starts the Android compatibility layer the way Suwayomi does (Koin modules, AndroidCompat, the app context),
 * loads every installed extension and serves the REST API. No database and no Suwayomi server code run here.
 */
fun main() {
    HostConfig.extensionsDir.mkdirs()
    // AndroidCompat keeps shared preferences and files below this folder.
    System.setProperty("$CONFIG_PREFIX.server.rootDir", HostConfig.dataDir.absolutePath)

    val app = App()
    startKoin {
        modules(createAppModule(app), androidCompatModule(), configManagerModule())
    }
    AndroidCompatInitializer().init()
    AndroidCompat().startApp(app)

    val network = Injekt.get<NetworkHelper>()
    System.setProperty("http.agent", network.defaultUserAgentProvider())

    val store = ExtensionStore(HostConfig.extensionsDir)
    store.loadInstalled()
    logger.info { "Loaded ${store.installed().size} extensions with ${SourceRegistry.all().size} sources" }
    // Cloudflare clearance changed the user agent: sources rebuild their headers.
    SourceRegistry.onUserAgentChanged = { store.reloadAll() }

    val repos = ExtensionRepos(java.io.File(HostConfig.dataDir, "extension-stores.json"))
    // EXTENSION_STORES: the repositories configured in AutoSourcarr (JSON array of index URLs), as Suwayomi received them.
    System.getenv("EXTENSION_STORES")?.takeIf { it.isNotBlank() }?.let { raw ->
        val urls =
            runCatching { kotlinx.serialization.json.Json.parseToJsonElement(raw.trim().removeSurrounding("'")) }
                .getOrNull()
                ?.let { it as? kotlinx.serialization.json.JsonArray }
                ?.mapNotNull { (it as? kotlinx.serialization.json.JsonPrimitive)?.content }
        if (urls != null) {
            repos.setStores(urls)
            Thread({ runCatching { repos.fetch() } }, "extension-store-fetch").apply { isDaemon = true }.start()
        } else {
            logger.warn { "EXTENSION_STORES is not a JSON array of repository URLs; repositories unchanged" }
        }
    }
    val db = HostDb(java.io.File(HostConfig.dataDir, "source-host.sqlite"))
    val server = HostApi(store, repos, db, SourceBackoff()).start(HostConfig.bind, HostConfig.port)
    logger.info { "AutoSourcarr source host ${HostConfig.HOST_VERSION} listening on ${HostConfig.bind}:${HostConfig.port}" }
    Runtime.getRuntime().addShutdownHook(Thread { server.stop(1) })
}
