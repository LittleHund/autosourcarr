# syntax=docker/dockerfile:1.12@sha256:93bfd3b68c109427185cd78b4779fc82b484b0b7618e36d0f104d4d801e66d25
# AutoSourcarr 0.5.0 image (conversion phase 13). Build context: the repository root.
#
#   docker buildx build -f docker/Dockerfile -t autosourcarr:0.5.0 .
#
# The Python 0.4.5 image stays autosourcarr/Dockerfile (context ./autosourcarr) until the cutover (phase 14).
#
# Stages: ui (Web UI bundle) · app (.NET 10 ReadyToRun) · source-host (Kotlin host jar) · jre (jlink Java 25) ·
# jcef (JetBrains JCEF build for the WebView) · native (repair runner, launcher, source host sandbox) · reader-extension (APK/JAR repo) ·
# runtime (final). `source-host` and `reader-extension` are plain file stages, so the release pipeline can hand in
# prebuilt copies with --build-context source-host=DIR / --build-context reader-extension=DIR (Kotlin host only
# rebuilt when its files change; extension signed with the owner's secrets outside the image build). A plain
# `docker build` builds both itself.
#
# PIPE-3: every base image is pinned by digest and every download by sha256/sha512 in this file. Run by hand,
# scripts/update-pins.py refreshes the image digests (diff for review) and only reports changed upstream tool hashes,
# which a person verifies and edits here. OS packages are not pinned: each build installs the current signed Ubuntu
# packages (apt-get upgrade, forced by PACKAGES_EPOCH) and records the list in /opt/autosourcarr-image-packages.txt.
#
# Users (ARC-8, hard rule 13): the app runs as 568:568 (TrueNAS "apps"); the source host has its own low-rights
# account 569:569 "asc-source" (no shell, no groups, home /home/asc-source) for area R to start the extension JVM
# under. Nothing runs as root at run time, no Docker socket, no helper services.

ARG DOTNET_SDK=mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG DOTNET_RUNTIME=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
ARG JDK25=eclipse-temurin:25-jdk-noble@sha256:95e350ad1eb738f943e141640f7e3d797e483359a3b33c3864255bfd1ad71f8a
ARG JDK21=eclipse-temurin:21-jdk-noble@sha256:114cabb73033ca4ce7ceec3deb62396bc1b716282bae051ed3c1d82e38fcab2d
ARG NODE=node:22-bookworm-slim@sha256:4b150b48b4116e2a22f2c39cc27025fbf4f153e2078fd3b14d159fdb5c7279da
ARG UBUNTU=ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852
ARG ANDROID_SDK=ghcr.io/cirruslabs/android-sdk:35@sha256:c724009e305b4607157287624033ab97f319af44c244bfc9f73b6293f3bb01b9

# ---------------------------------------------------------------------------------------------------------------
# Web UI bundle (phase 11): JS/CSS bundled and minified; the image serves dist/index.html as the page.
FROM ${NODE} AS ui
WORKDIR /src
COPY package.json package-lock.json ./
RUN --mount=type=cache,target=/root/.npm,sharing=locked \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci --no-audit --no-fund --ignore-scripts
COPY scripts/bundle-ui.mjs scripts/
COPY autosourcarr/app/static autosourcarr/app/static
RUN rm -rf autosourcarr/app/static/dist \
    && node scripts/bundle-ui.mjs --check \
    && node scripts/bundle-ui.mjs \
    && test -s autosourcarr/app/static/dist/index.html

# ---------------------------------------------------------------------------------------------------------------
# .NET 10 app: ReadyToRun, linux-x64, framework-dependent (the runtime base carries ASP.NET Core 10.0.14).
FROM ${DOTNET_SDK} AS app
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/AutoSourcarr.Core/AutoSourcarr.Core.csproj src/AutoSourcarr.Core/packages.lock.json src/AutoSourcarr.Core/
COPY src/AutoSourcarr.Api/AutoSourcarr.Api.csproj src/AutoSourcarr.Api/packages.lock.json src/AutoSourcarr.Api/
# Locked restore: the committed lock files (with their linux-x64 section) are the package pins.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/AutoSourcarr.Api/AutoSourcarr.Api.csproj -r linux-x64 -p:PublishReadyToRun=true --locked-mode
COPY src src
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/AutoSourcarr.Api/AutoSourcarr.Api.csproj -c Release -r linux-x64 --self-contained false \
       -p:PublishReadyToRun=true --no-restore -o /out \
    && rm -f /out/*.pdb \
    && test -s /out/AutoSourcarr.Api.dll

# ---------------------------------------------------------------------------------------------------------------
# Kotlin source host, built inside the pinned Suwayomi-Server v2.4.2366 checkout (PIPE-7) by the same script CI uses.
FROM ${JDK21} AS source-host-build
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked --mount=type=cache,target=/var/lib/apt,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
    && apt-get update && apt-get install -y --no-install-recommends git ca-certificates
WORKDIR /src
COPY scripts/build-source-host.sh scripts/
COPY source-host source-host
RUN --mount=type=cache,target=/root/.gradle,sharing=locked \
    --mount=type=cache,target=/work/suwayomi,sharing=locked \
    SOURCE_HOST_WORK=/work/suwayomi bash scripts/build-source-host.sh \
    && mkdir /out && cp .build/autosourcarr-source-host.jar /out/

FROM scratch AS source-host
COPY --from=source-host-build /out/ /

# ---------------------------------------------------------------------------------------------------------------
# Java 25 runtime made with jlink: only the modules the host jar needs (jdeps) plus the ones extensions and the
# WebView reach by reflection or service loading. The default CDS archive is regenerated for the smaller image.
FROM ${JDK25} AS jre
COPY --from=source-host /autosourcarr-source-host.jar /tmp/host.jar
# Extra modules: java.desktop (AWT/jawt for JCEF), jdk.charsets + jdk.localedata (Shift_JIS/EUC-KR pages, localised
# dates), jdk.unsupported (GraalJS/Truffle, Unsafe users), jdk.crypto.cryptoki/java.security.jgss (TLS edge cases).
# Names this JDK does not have (merged or removed modules) are dropped.
RUN set -eu; \
    found=$(jdeps --ignore-missing-deps --multi-release 25 --print-module-deps /tmp/host.jar 2>/dev/null || true); \
    extra="java.base,java.desktop,java.logging,java.management,java.naming,java.net.http,java.scripting,java.security.jgss,java.sql,java.xml,java.xml.crypto,jdk.charsets,jdk.crypto.cryptoki,jdk.crypto.ec,jdk.localedata,jdk.management,jdk.net,jdk.unsupported,jdk.zipfs"; \
    java --list-modules | sed 's/@.*//' > /tmp/available.txt; \
    modules=$(printf '%s,%s' "$found" "$extra" | tr ',' '\n' | sed '/^$/d' | sort -u | grep -Fx -f /tmp/available.txt | paste -sd, -); \
    echo "jlink modules: $modules"; \
    jlink --add-modules "$modules" --strip-debug --no-man-pages --no-header-files --compress=zip-6 \
          --generate-cds-archive --output /opt/source-jre; \
    /opt/source-jre/bin/java -version; \
    printf '%s\n' "$modules" > /opt/source-jre/autosourcarr-modules.txt

# ---------------------------------------------------------------------------------------------------------------
# JCEF (JetBrains Runtime 25 JCEF build) for the source host WebView (R25, SRC-5). Same release the host would
# otherwise download on first start (WebViewEngine.JBR_RELEASE); baked in so the image needs no runtime download.
FROM ${JDK25} AS jcef
ARG JCEF_URL=https://cache-redirector.jetbrains.com/intellij-jbr/jbr_jcef-25.0.4-linux-x64-b508.27.tar.gz
ARG JCEF_SHA512=e92a8c168ae82e522ffa8348d36d4384b70a3aad34cdf350a111c36a73c2fbe31f08420a8287fd4646d1bd0f4c60ba777572a04ee71b92740b4d08f8de86b05e
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked --mount=type=cache,target=/var/lib/apt,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
    && apt-get update && apt-get install -y --no-install-recommends curl ca-certificates
RUN set -eu; \
    curl --fail --location --retry 3 --proto '=https' "$JCEF_URL" -o /tmp/jcef.tar.gz; \
    printf '%s  /tmp/jcef.tar.gz\n' "$JCEF_SHA512" | sha512sum -c -; \
    mkdir -p /tmp/x /opt/source-host/kcef; tar -xzf /tmp/jcef.tar.gz -C /tmp/x; \
    top=$(find /tmp/x -mindepth 1 -maxdepth 1 -type d | head -n 1); \
    test -d "$top/lib" && test -f "$top/release"; \
    cp -a "$top/lib/." /opt/source-host/kcef/; cp "$top/release" /opt/source-host/kcef/release; \
    grep -q JCEF_VERSION_DETAILED /opt/source-host/kcef/release; \
    rm -rf /tmp/x /tmp/jcef.tar.gz

# ---------------------------------------------------------------------------------------------------------------
# Repair runner (Landlock sandbox), launcher and the source host sandbox (area R, SRC-5): static PIE, hardened
# flags, no runtime library dependency.
FROM ${UBUNTU} AS native
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked --mount=type=cache,target=/var/lib/apt,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
    && apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev linux-libc-dev binutils
COPY native/repair-runner.c native/repair-launcher.c native/source-host-sandbox.c /src/
RUN set -eu; mkdir /out; \
    flags="-O2 -Wall -Werror -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=3 -fstack-protector-strong -fstack-clash-protection -fcf-protection=full -fPIE -static-pie -Wl,-z,relro,-z,now,-z,noexecstack"; \
    for tool in repair-runner repair-launcher source-host-sandbox; do \
      gcc $flags -o /out/$tool /src/$tool.c; \
      if readelf -d /out/$tool | grep -q NEEDED; then echo "$tool is not static" >&2; exit 1; fi; \
      strip --strip-unneeded /out/$tool; \
    done

# ---------------------------------------------------------------------------------------------------------------
# Reader extension repository (READ-1) built by scripts/build-reader-extension.sh. Without the signing secrets the
# build is UNSIGNED-FOR-RELEASE (throwaway key); the release pipeline normally builds it outside and hands it in.
FROM ${JDK21} AS jdk21
FROM ${ANDROID_SDK} AS reader-extension-build
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked --mount=type=cache,target=/var/lib/apt,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
    && apt-get update && apt-get install -y --no-install-recommends git unzip python3 ca-certificates
COPY --from=jdk21 /opt/java/openjdk /opt/jdk21
ENV JAVA_HOME=/opt/jdk21 PATH=/opt/jdk21/bin:${PATH}
WORKDIR /src
COPY VERSION ./
COPY scripts/build-reader-extension.sh scripts/
COPY extension extension
RUN --mount=type=cache,target=/root/.gradle,sharing=locked \
    --mount=type=secret,id=ext_keystore_b64,env=AUTOSOURCARR_EXT_KEYSTORE_B64 \
    --mount=type=secret,id=ext_keystore_password,env=AUTOSOURCARR_EXT_KEYSTORE_PASSWORD \
    --mount=type=secret,id=ext_key_alias,env=AUTOSOURCARR_EXT_KEY_ALIAS \
    --mount=type=secret,id=ext_key_password,env=AUTOSOURCARR_EXT_KEY_PASSWORD \
    READER_EXTENSION_WORK=/tmp/keiyoushi bash scripts/build-reader-extension.sh /out

FROM scratch AS reader-extension
COPY --from=reader-extension-build /out/ /

# ---------------------------------------------------------------------------------------------------------------
FROM ${DOTNET_RUNTIME} AS runtime
ARG VERSION=0.5.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="AutoSourcarr" \
      org.opencontainers.image.source="https://github.com/LittleHund/autosourcarr" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
USER root
# Fresh OS packages at every build (PIPE-1/PIPE-2). Xvfb + X11/NSS/GTK libraries for JCEF (R25), tini as PID 1 (child
# reaping for Xvfb and Chromium helpers), curl for the health check, tzdata for TZ, fontconfig for AWT.
# PACKAGES_EPOCH changes for every release build (the pipeline passes its run id), so this layer is never a layer-cache
# hit from an earlier release: apt-get upgrade really runs each time. A plain local build without it uses "local".
ARG PACKAGES_EPOCH=local
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked --mount=type=cache,target=/var/lib/apt,sharing=locked \
    set -eu; echo "OS package refresh, epoch ${PACKAGES_EPOCH}"; rm -f /etc/apt/apt.conf.d/docker-clean; \
    apt-get update; DEBIAN_FRONTEND=noninteractive apt-get -y upgrade; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      tini curl ca-certificates tzdata util-linux fontconfig fonts-liberation libfreetype6 libgomp1 \
      xvfb xauth libx11-6 libx11-xcb1 libxcb1 libxext6 libxrender1 libxtst6 libxi6 libxcomposite1 libxdamage1 \
      libxfixes3 libxrandr2 libxkbcommon0 libxshmfence1 libxss1 libnss3 libnspr4 libdbus-1-3 libdrm2 libgbm1 \
      libasound2t64 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 libpango-1.0-0 libcairo2 libglib2.0-0t64 \
      libgtk-3-0t64; \
    dpkg-query -W -f '${Package} ${Version}\n' | sort > /opt/autosourcarr-image-packages.txt; \
    test -x /usr/bin/setsid && test -x /usr/bin/setpriv && test -x /usr/bin/Xvfb && test -x /usr/bin/tini
# Accounts: 568:568 for the app; 569:569 for the source host JVM (area R). Neither has a login shell.
RUN set -eu; \
    groupadd --system --gid 568 autosourcarr; \
    useradd --system --uid 568 --gid 568 --no-user-group --home-dir /home/autosourcarr --create-home --shell /usr/sbin/nologin autosourcarr; \
    groupadd --system --gid 569 asc-source; \
    useradd --system --uid 569 --gid 569 --no-user-group --home-dir /home/asc-source --create-home --shell /usr/sbin/nologin asc-source; \
    chmod 0750 /home/autosourcarr /home/asc-source; \
    mkdir -p /data /config; chown 568:568 /data /config; chmod 0750 /data /config

COPY --from=jre /opt/source-jre /opt/source-jre
COPY --from=jcef /opt/source-host/kcef /opt/source-host/kcef
COPY --from=native /out/repair-runner /out/repair-launcher /out/source-host-sandbox /opt/autosourcarr-native/
COPY --from=source-host /autosourcarr-source-host.jar /app/source-host/autosourcarr-source-host.jar
COPY docker/source-host-java.sh /opt/source-host/bin/java
COPY --from=app /out/ /app/
COPY autosourcarr/app/static /app/static
COPY --from=ui /src/autosourcarr/app/static/dist /app/static/dist
COPY --from=reader-extension / /app/reader-extension/
COPY src/AutoSourcarr.Core/Compatibility/compatibility.json /app/compatibility.json
COPY VERSION /opt/autosourcarr-image-version
# The bundle replaces the page; the separate sources stay for login.html and other pages.
RUN set -eu; \
    cp /app/static/dist/index.html /app/static/index.html; \
    chown -R root:root /app /opt/source-jre /opt/source-host /opt/autosourcarr-native; \
    chmod -R go-w /app /opt/source-jre /opt/source-host; \
    chmod 0755 /opt/source-host/bin/java /opt/autosourcarr-native /opt/autosourcarr-native/repair-runner /opt/autosourcarr-native/repair-launcher \
          /opt/autosourcarr-native/source-host-sandbox; \
    test -s /app/reader-extension/index.min.json; \
    grep -q '/static/dist/' /app/static/index.html; \
    sed -i 's/\r$//' /opt/source-host/bin/java

# JDK 25 AOT class cache (JEP 483/514) from a training run of the source host start, as the source host user, with
# the final runtime and jar paths (the cache is only used when they match). WebView off: no display at build time.
# A run that does not reach the health check leaves no cache; the host then starts without it (AOTMode=auto).
RUN set -eu; \
    train=$(mktemp -d); chown 569:569 "$train"; mkdir -p /opt/source-host/aot; chown 569:569 /opt/source-host/aot; \
    setpriv --reuid=569 --regid=569 --clear-groups --inh-caps=-all \
      env -i PATH=/usr/bin:/bin HOME=/home/asc-source SOURCE_HOST_DATA="$train" SOURCE_HOST_PORT=4599 \
          SOURCE_HOST_BIND=127.0.0.1 SOURCE_HOST_TOKEN=aot-training WEBVIEW_ENABLED=false \
      /opt/source-jre/bin/java -XX:-UsePerfData -XX:AOTCacheOutput=/opt/source-host/aot/source-host.aot \
          -jar /app/source-host/autosourcarr-source-host.jar > /tmp/aot-training.log 2>&1 & \
    pid=$!; ok=0; \
    for i in $(seq 1 90); do \
      if curl -fsS -m 5 -H 'X-Source-Host-Token: aot-training' http://127.0.0.1:4599/v1/health > /dev/null 2>&1; then ok=1; break; fi; \
      kill -0 "$pid" 2>/dev/null || break; sleep 2; \
    done; \
    if [ "$ok" = 1 ]; then \
      for path in /v1/health /v1/sources /v1/extensions; do curl -s -m 10 -o /dev/null -H 'X-Source-Host-Token: aot-training' "http://127.0.0.1:4599$path" || true; done; \
    fi; \
    kill -TERM "$pid" 2>/dev/null || true; wait "$pid" || true; \
    if [ "$ok" = 1 ] && [ -s /opt/source-host/aot/source-host.aot ]; then \
      echo "AOT cache: $(du -h /opt/source-host/aot/source-host.aot | cut -f1)" | tee /opt/source-host/aot/status.txt; \
    else \
      rm -f /opt/source-host/aot/source-host.aot; tail -n 40 /tmp/aot-training.log; \
      echo "AOT cache: none (training run did not reach the health check)" | tee /opt/source-host/aot/status.txt; \
    fi; \
    chown -R root:root /opt/source-host/aot; chmod -R a+rX,go-w /opt/source-host/aot; \
    rm -rf "$train" /tmp/aot-training.log /tmp/hsperfdata_* /tmp/*.aot* 2>/dev/null || true

# Safe defaults (ARC-8): integrated source host, the image's own runtime paths, no .NET diagnostics port.
# The source host runs under the Landlock source-host-sandbox as the app's user (568). AUTOSOURCARR_SOURCE_HOST_UID/GID
# are deliberately NOT set: with cap_drop ALL + no-new-privileges the launcher cannot switch users (see the optional,
# not recommended uid 569 variant in docker-compose.0.5.0.yml). JCEF needs no extra sandbox path: the Java wrapper
# puts the JVM temp folder, HOME and XDG caches under the host's data folder, which the sandbox already allows.
# Everything else (auth, keys, paths) has its default in the settings schema and lives in /config/config.json.
ENV AUTOSOURCARR_SOURCE_HOST_JAR=/app/source-host/autosourcarr-source-host.jar \
    AUTOSOURCARR_JAVA=/opt/source-host/bin/java \
    AUTOSOURCARR_READER_EXTENSION_DIR=/app/reader-extension \
    AUTOSOURCARR_REPAIR_RUNNER=/opt/autosourcarr-native/repair-runner \
    AUTOSOURCARR_SOURCE_HOST_SANDBOX=/opt/autosourcarr-native/source-host-sandbox \
    SOURCE_ENGINE_MODE=integrated \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    HOME=/home/autosourcarr
WORKDIR /app
USER 568:568
EXPOSE 8282
# PERF-7: boot under 1 min preferred, 5 min allowed; the start period covers the 5-minute budget.
HEALTHCHECK --interval=30s --timeout=5s --start-period=300s --retries=3 \
    CMD curl -fsS -m 4 -o /dev/null http://127.0.0.1:8282/api/v1/live || exit 1
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "/app/AutoSourcarr.Api.dll"]
