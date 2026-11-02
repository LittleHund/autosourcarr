#!/bin/sh
# Java launcher of the source host in the 0.5.0 image (AUTOSOURCARR_JAVA). The app starts it through
# /opt/autosourcarr-native/source-host-sandbox, where only the host's data folder is writable.
#  * AOT class cache from the image build's training run; a missing or mismatched cache is ignored by the JVM
#    (AOTMode=auto), so the host always starts.
#  * java.io.tmpdir follows TMPDIR (the app points it at <data>/tmp inside the sandbox); the JVM would otherwise use
#    /tmp, which the sandbox does not allow. No hsperfdata file in /tmp either (-XX:-UsePerfData).
# Arguments are passed through unchanged.
set -- -XX:-UsePerfData "$@"
if [ -n "${TMPDIR:-}" ] && [ -d "$TMPDIR" ] && [ -w "$TMPDIR" ]; then
  set -- "-Djava.io.tmpdir=$TMPDIR" "$@"
fi
cache=/opt/source-host/aot/source-host.aot
if [ -r "$cache" ]; then
  exec /opt/source-jre/bin/java -XX:AOTCache="$cache" "$@"
fi
exec /opt/source-jre/bin/java "$@"
