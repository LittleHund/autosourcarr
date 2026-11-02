#!/bin/sh
# Java launcher of the source host in the 0.5.0 image (AUTOSOURCARR_JAVA). Adds the JDK 25 AOT class cache that the
# image build made from a training run of the host start; a missing or mismatched cache is ignored by the JVM
# (AOTMode=auto), so the host always starts. Arguments are passed through unchanged.
cache=/opt/source-host/aot/source-host.aot
if [ -r "$cache" ]; then
  exec /opt/source-jre/bin/java -XX:AOTCache="$cache" "$@"
fi
exec /opt/source-jre/bin/java "$@"
