#!/usr/bin/env bash
# Build both .tpk packages with the userspace toolchains on this box (no sudo,
# nothing installed system-wide). Output is copied into dist/.
#
#   ./build.sh          # all three
#   ./build.sh tizen8   # net6.0-tizen8.0, Tizen.WebView  -> TV 8.0
#   ./build.sh tizen5   # tizen50, Tizen.WebView          -> TV 5.0+
#   ./build.sh tizen6   # tizen60, Tizen.WebView          -> TV 6.0-7.0 (2021-2023)
#   ./build.sh tizen4   # tizen40, Tizen.WebView          -> TV 4.0 (2018, oldest possible)
#   ./build.sh nui      # net6.0-tizen8.0, NUI WebView    -> TV 9.0+
#
# Toolchain notes (both were installed with dot.net/v1/dotnet-install.sh):
#   ~/.dotnet-local  .NET 6 SDK + `tizen` workload  -> net6.0-tizen8.0
#   ~/.dotnet-31     .NET Core 3.1 SDK              -> tizen50 (a .NET 5+ SDK
#                    fails with NETSDK1013 "tizen50 was not recognized")
# The 3.1 SDK needs OpenSSL 1.1, which Ubuntu 24.04 does not ship, so a copy is
# extracted under ~/.local/openssl11 (from the focal libssl1.1 .deb) and fed in
# via LD_LIBRARY_PATH, with OPENSSL_CONF=/dev/null because those 1.1 libs cannot
# parse this system OpenSSL 3 config ("unknown module name") the moment a restore
# needs the network. Without the libs, restore dies with "No usable version of libssl
# was found".
set -euo pipefail

cd "$(dirname "$0")"
mkdir -p dist

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

DOTNET6="$HOME/.dotnet-local/dotnet"
DOTNET31="$HOME/.dotnet-31/dotnet"
OPENSSL11="$HOME/.local/openssl11/usr/lib/x86_64-linux-gnu"

target="${1:-both}"

build_tizen8() {
  echo "== Overscan (net6.0-tizen8.0)"
  DOTNET_ROOT="$HOME/.dotnet-local" "$DOTNET6" \
    build Overscan/Overscan.csproj -c Release
  cp Overscan/bin/Release/net6.0-tizen8.0/*.tpk dist/Overscan-tizen8.tpk
}

build_nui() {
  echo "== OverscanNui (net6.0-tizen8.0, NUI WebView, TV 9.0+)"
  DOTNET_ROOT="$HOME/.dotnet-local" "$DOTNET6" \
    build OverscanNui/OverscanNui.csproj -c Release
  cp OverscanNui/bin/Release/net6.0-tizen8.0/*.tpk dist/Overscan-nui.tpk
}

# Shared by every classic tizenXX target.
legacy_build() {
  local project="$1" tfm="$2" out="$3"
  DOTNET_ROOT="$HOME/.dotnet-31" \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  OPENSSL_CONF=/dev/null \
  LD_LIBRARY_PATH="$OPENSSL11:${LD_LIBRARY_PATH:-}" \
    "$DOTNET31" build "$project" -c Debug
  cp "$(dirname "$project")/bin/Debug/$tfm"/*.tpk "dist/$out"
}

build_tizen4() {
  echo "== Overscan4 (tizen40, Tizen.WebView, TV 4.0 / 2018 sets)"
  legacy_build Overscan4/Overscan4.csproj tizen40 Overscan-tizen4.tpk
}

build_tizen6() {
  echo "== Overscan6 (tizen60, Tizen.WebView, TV 6.0-7.0 / 2021-2023 sets)"
  legacy_build Overscan6/Overscan6.csproj tizen60 Overscan-tizen6.tpk
}

build_tizen5() {
  echo "== Overscan5 (tizen50, Tizen.WebView, TV 5.0-5.5 / 2019-2020 sets)"
  legacy_build Overscan5/Overscan5.csproj tizen50 Overscan-tizen5.tpk
}

case "$target" in
  tizen8) build_tizen8 ;;
  tizen5) build_tizen5 ;;
  tizen4) build_tizen4 ;;
  tizen6) build_tizen6 ;;
  nui)    build_nui ;;
  both|all) build_tizen8; build_tizen6; build_tizen5; build_tizen4; build_nui ;;
  *) echo "usage: $0 [all|tizen8|tizen5|nui]" >&2; exit 2 ;;
esac

echo
echo "Default-signed packages in dist/ (re-sign with a DUID cert before installing):"
ls -lh dist/*.tpk
