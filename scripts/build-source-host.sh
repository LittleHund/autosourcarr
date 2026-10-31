#!/usr/bin/env bash
# Builds the AutoSourcarr source host (source-host/) inside a pinned Suwayomi-Server checkout.
# Only Suwayomi's AndroidCompat module and version catalog are used; the result is one jar:
#   .build/autosourcarr-source-host.jar
# Needs JDK 21+ and network access to GitHub, Maven Central, Google Maven and JitPack (CI has them).
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
work=${SOURCE_HOST_WORK:-$root/.build/suwayomi}
# Suwayomi-Server v2.4.2366 (MPL-2.0); AndroidCompat and the vendored loader code come from this commit.
commit=40bc5da57820324ed0ae724cbbc020cc4673b594
if [ ! -d "$work/.git" ]; then
  rm -rf "$work"; mkdir -p "$work"
  git -C "$work" init -q
  git -C "$work" remote add origin https://github.com/Suwayomi/Suwayomi-Server
fi
git -C "$work" fetch -q --depth 1 origin "$commit"
git -C "$work" checkout -q --force FETCH_HEAD
git -C "$work" clean -qfdx -e .gradle -e build
rm -rf "$work/source-host"
cp -r "$root/source-host" "$work/source-host"
grep -q 'include("source-host")' "$work/settings.gradle.kts" || printf '\ninclude("source-host")\n' >> "$work/settings.gradle.kts"
(cd "$work" && ./gradlew --no-daemon -q :source-host:shadowJar)
mkdir -p "$root/.build"
cp "$work/source-host/build/libs/autosourcarr-source-host.jar" "$root/.build/autosourcarr-source-host.jar"
echo "Built $root/.build/autosourcarr-source-host.jar"
