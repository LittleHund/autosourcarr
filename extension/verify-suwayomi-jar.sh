#!/usr/bin/env bash
set -euo pipefail
JAR_PATH="${1:-}"
if [[ -z "$JAR_PATH" ]]; then
  echo "Usage: bash extension/verify-suwayomi-jar.sh extension/dist/AutoSourcarr-Suwayomi.jar" >&2
  exit 2
fi
[[ "$JAR_PATH" == *.jar ]] || { echo "FAIL: filename must end in .jar: $JAR_PATH" >&2; exit 1; }
[[ -s "$JAR_PATH" ]] || { echo "FAIL: missing/empty file: $JAR_PATH" >&2; exit 1; }
if command -v unzip >/dev/null 2>&1; then
  ARCHIVE_TOOL=unzip
elif command -v jar >/dev/null 2>&1; then
  ARCHIVE_TOOL=jar
else
  echo "FAIL: unzip or the JDK 'jar' command is required" >&2
  exit 1
fi
command -v od >/dev/null 2>&1 || { echo "FAIL: 'od' command is unavailable" >&2; exit 1; }
# The JDK fallback changes directories; retain a valid absolute archive path.
JAR_PATH="$(cd -- "$(dirname -- "$JAR_PATH")" && pwd -P)/$(basename -- "$JAR_PATH")"

LIST="$(mktemp)"
TMP="$(mktemp -d)"
cleanup(){ rm -f "$LIST"; rm -rf "$TMP"; }
trap cleanup EXIT
if [[ "$ARCHIVE_TOOL" == unzip ]]; then
  unzip -Z1 "$JAR_PATH" > "$LIST" || { echo "FAIL: file is not a valid ZIP/JAR archive" >&2; exit 1; }
else
  jar tf "$JAR_PATH" > "$LIST" || { echo "FAIL: file is not a valid ZIP/JAR archive" >&2; exit 1; }
fi
grep -qx 'AndroidManifest.xml' "$LIST" || { echo "FAIL: JAR is missing AndroidManifest.xml" >&2; exit 1; }
FIRST_CLASS="$(grep -m1 -E '\.class$' "$LIST" || true)"
[[ -n "$FIRST_CLASS" ]] || { echo "FAIL: JAR contains no JVM .class files" >&2; exit 1; }
(
  cd "$TMP"
  if [[ "$ARCHIVE_TOOL" == unzip ]]; then
    # Read into an owned filename; never extract an archive-controlled path.
    unzip -p "$JAR_PATH" "$FIRST_CLASS" > class.bin
    CLASS_FILE=class.bin
  else
    case "/$FIRST_CLASS/" in
      *'/../'*|*'/./'*|//*|*'\\'*) echo "FAIL: unsafe class entry path" >&2; exit 1;;
    esac
    jar xf "$JAR_PATH" "$FIRST_CLASS"
    CLASS_FILE="$FIRST_CLASS"
  fi
  [[ -s "$CLASS_FILE" ]] || { echo "FAIL: class entry could not be read: $FIRST_CLASS" >&2; exit 1; }
  MAGIC="$(od -An -tx1 -N4 "$CLASS_FILE" | tr -d '[:space:]')"
  [[ "$MAGIC" == "cafebabe" ]] || { echo "FAIL: $FIRST_CLASS is not JVM class bytecode" >&2; exit 1; }
)
COUNT="$(grep -cE '\.class$' "$LIST" || true)"
SIZE="$(wc -c < "$JAR_PATH" | tr -d '[:space:]')"
SHA="$(sha256sum "$JAR_PATH" | awk '{print $1}')"
printf 'OK: JVM extension JAR structure checks passed\n'
printf 'File: %s\n' "$(basename "$JAR_PATH")"
printf 'Size: %s bytes\n' "$SIZE"
printf 'JVM classes: %s\n' "$COUNT"
printf 'SHA-256: %s\n' "$SHA"
