#!/usr/bin/env bash
# Builds the AutoSourcarr source host (source-host/) inside a pinned Suwayomi-Server checkout.
# Only Suwayomi's AndroidCompat module and version catalog are used; the result is one jar:
#   .build/autosourcarr-source-host.jar
# Needs JDK 21+ and network access to GitHub, Maven Central, Google Maven and JitPack (CI has them).
# Suwayomi's android-jar stubs come from a fixed commit and are checked by SHA-256: Suwayomi replaced android-jar
# 1.0.0 in place on 2026-11-01 (dropping android.net.Network*, which its newer AndroidCompat implements), and v2.4.2366
# does not compile against that copy.
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
git -C "$work" clean -qfdx -e .gradle -e build -e .android-jar-repo
stubs_commit=3983289d9481a8ba99c0b16350b27f41364906a2
stubs_path=com/github/Suwayomi/android-jar/1.0.0
stubs_jar_sha256=2743be4f4c9ba50f8c86ccc0769e5be5246542488b34e6085a2574ca8ac303e3
stubs_pom_sha256=fef714b8dd4add38340746a073f74a375d5baac5be966e72a4d917b51e30d431
stubs_dir=$work/.android-jar-repo/$stubs_path
stubs_ok() {
  printf '%s  %s\n%s  %s\n' "$stubs_jar_sha256" "$stubs_dir/android-jar-1.0.0.jar" "$stubs_pom_sha256" "$stubs_dir/android-jar-1.0.0.pom" \
    | sha256sum -c --status 2>/dev/null
}
if ! stubs_ok; then
  git -C "$work" fetch -q --depth 1 origin "$stubs_commit"
  mkdir -p "$stubs_dir"
  for file in android-jar-1.0.0.jar android-jar-1.0.0.pom; do
    git -C "$work" show "$stubs_commit:$stubs_path/$file" > "$stubs_dir/$file"
  done
  stubs_ok || { echo "android-jar stubs from $stubs_commit do not match the pinned SHA-256; refusing to build." >&2; exit 1; }
fi
android_repo='maven("https://github.com/Suwayomi/Suwayomi-Server/raw/android-jar/")'
[ "$(grep -cF "$android_repo" "$work/build.gradle.kts")" = 1 ] \
  || { echo "Suwayomi build.gradle.kts no longer has exactly one android-jar repository line; update the stubs pin." >&2; exit 1; }
sed -i "s|$android_repo|maven(rootDir.resolve(\".android-jar-repo\"))|" "$work/build.gradle.kts"
rm -rf "$work/source-host"
cp -r "$root/source-host" "$work/source-host"
grep -q 'include("source-host")' "$work/settings.gradle.kts" || printf '\ninclude("source-host")\n' >> "$work/settings.gradle.kts"
(cd "$work" && ./gradlew --no-daemon -q :source-host:shadowJar)
mkdir -p "$root/.build"
cp "$work/source-host/build/libs/autosourcarr-source-host.jar" "$root/.build/autosourcarr-source-host.jar"
echo "Built $root/.build/autosourcarr-source-host.jar"
