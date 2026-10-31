// AutoSourcarr source host (conversion phase 4).
// Built as subproject ":source-host" inside a pinned Suwayomi-Server checkout (see scripts/build-source-host.sh),
// so it shares Suwayomi's version catalog and depends only on its AndroidCompat module, never on its server.
plugins {
    id(libs.plugins.kotlin.jvm.get().pluginId)
    id(libs.plugins.kotlin.serialization.get().pluginId)
    application
    alias(libs.plugins.shadowjar)
}

dependencies {
    implementation(libs.bundles.shared)
    implementation(libs.bundles.okhttp)
    implementation(libs.okio)
    implementation(projects.androidCompat)
    implementation(projects.androidCompat.config)
    implementation(libs.android.stubs)

    // What Tachiyomi/Mihon extensions expect on the class path.
    implementation(libs.injekt)
    implementation(libs.rxjava)
    implementation(libs.jsoup)

    // Extension loading: dex -> jar, bytecode fixes, manifests, icons.
    implementation(libs.asm)
    implementation(libs.serialization.xml.core)
    implementation(libs.serialization.xml)
    implementation(libs.commonscompress)
    implementation(libs.cache4k)
    implementation(libs.sort)
    implementation(libs.bundles.twelvemonkeys)
    implementation(libs.imageio.webp)
}

application {
    mainClass.set("autosourcarr.host.MainKt")
}

tasks.shadowJar {
    archiveBaseName.set("autosourcarr-source-host")
    archiveClassifier.set("")
    archiveVersion.set("")
    mergeServiceFiles()
    isZip64 = true
}
