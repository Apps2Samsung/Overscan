#!/usr/bin/env bash
# Builds the two native libraries this repo ships, from the assembly beside it:
#
#   ovprobe.s  ->  Overscan5/res/libovprobe.so                       (issue #17)
#   psstub.s   ->  Overscan6/res/libprivileged-service-client.so     (issue #105)
#
# Both results are committed, because CI has no ARM toolchain and these files
# change roughly never. Rebuild only if the source beside them changes.
#
# The toolchain is one .deb and needs no root — the same trick that supplies the
# openssl 1.1 shim in build.sh:
#
#   apt-get download binutils-arm-linux-gnueabihf
#   dpkg-deb -x binutils-arm-linux-gnueabihf_*.deb "$XTOOL"
#
# Point XTOOL at where that was extracted. The readelf at the end is the check:
# each must say DYN, ARM, its own SONAME, and no NEEDED and no TEXTREL — a
# dependency would make a refusal to load ambiguous, and a text relocation asks
# the loader for a writable code page on a firmware that refuses far less.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
xtool="${XTOOL:?set XTOOL to the extracted binutils-arm-linux-gnueabihf tree}"
bin="$xtool/usr/bin"
export LD_LIBRARY_PATH="$xtool/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

build() {
  local source="$1" soname="$2" out="$3"
  mkdir -p "$(dirname "$out")"
  "$bin/arm-linux-gnueabihf-as" -o "$here/build.o" "$here/$source"
  "$bin/arm-linux-gnueabihf-ld" -shared -soname "$soname" -o "$out" "$here/build.o"
  rm -f "$here/build.o"
  echo "== $out"
  "$bin/arm-linux-gnueabihf-readelf" -hd "$out" | grep -E 'Type:|Machine:|Flags:|SONAME|NEEDED|TEXTREL' || true
  "$bin/arm-linux-gnueabihf-readelf" --dyn-syms "$out" | grep -E 'FUNC|OBJECT' || true
}

build ovprobe.s libovprobe.so "$here/../../Overscan5/res/libovprobe.so"
build psstub.s libprivileged-service-client.so "$here/../../Overscan6/res/libprivileged-service-client.so"
