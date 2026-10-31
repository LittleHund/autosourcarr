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
    // Extensions use BouncyCastle ciphers (e.g. Mangago: AES/CBC/ZEROBYTEPADDING); Suwayomi registers it too.
    implementation(libs.bouncycastle)

    // Extension loading: dex -> jar, bytecode fixes, manifests, icons.
    implementation(libs.asm)
    // Verifier-guided repair of translated extension classes (ExtensionBytecodeRepair); same version as asm.
    implementation("org.ow2.asm:asm-tree:9.9.1")
    implementation("org.ow2.asm:asm-analysis:9.9.1")
    implementation(libs.serialization.xml.core)
    implementation(libs.serialization.xml)
    implementation(libs.commonscompress)
    implementation(libs.cache4k)
    implementation(libs.sort)
    implementation(libs.bundles.twelvemonkeys)
    implementation(libs.imageio.webp)

    // Stable numeric manga/chapter ids (the C# catalogue stores them, as it stored Suwayomi's).
    implementation("org.xerial:sqlite-jdbc:3.49.1.0")
}

application {
    mainClass.set("autosourcarr.host.MainKt")
}

tasks.shadowJar {
    archiveBaseName.set("autosourcarr-source-host")
    archiveClassifier.set("")
    archiveVersion.set("")
    isZip64 = true
    duplicatesStrategy = DuplicatesStrategy.INCLUDE
    mergeServiceFiles("META-INF/services")
    manifest {
        attributes("Main-Class" to "autosourcarr.host.MainKt", "Multi-Release" to true) // multi-release: GraalJS
    }
    dependencies {
        // Same as Suwayomi: the GraalVM js-community POM-only metapackages put .pom files on the class path,
        // which Shadow cannot expand (GradleUp/shadow#1716); the real jars are still included.
        exclude(dependency("org.graalvm.js:js-community:.*"))
        exclude(dependency("org.graalvm.js:js:.*"))
    }
}
