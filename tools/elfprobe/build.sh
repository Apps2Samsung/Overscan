#!/usr/bin/env bash
# Builds Overscan5/res/libovprobe.so, the native library issue #17's test loads.
#
# The result is committed, because CI has no ARM toolchain and this file changes
# roughly never. Rebuild it only if the probe itself changes.
#
# The toolchain is one .deb and needs no root — the same trick that supplies the
# openssl 1.1 shim in build.sh:
#
#   apt-get download binutils-arm-linux-gnueabihf
#   dpkg-deb -x binutils-arm-linux-gnueabihf_*.deb "$XTOOL"
#
# Point XTOOL at where that was extracted.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
xtool="${XTOOL:?set XTOOL to the extracted binutils-arm-linux-gnueabihf tree}"
bin="$xtool/usr/bin"
export LD_LIBRARY_PATH="$xtool/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

out="$here/../../Overscan5/res/libovprobe.so"
mkdir -p "$(dirname "$out")"

"$bin/arm-linux-gnueabihf-as" -o "$here/ovprobe.o" "$here/ovprobe.s"
"$bin/arm-linux-gnueabihf-ld" -shared -soname libovprobe.so -o "$out" "$here/ovprobe.o"
rm -f "$here/ovprobe.o"

"$bin/arm-linux-gnueabihf-readelf" -hd "$out"
