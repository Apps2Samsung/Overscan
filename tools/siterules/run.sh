#!/usr/bin/env bash
# Exercises the per-site settings — SiteRules — off-device.
#
#   ./run.sh
#
# Issue #74 asked for images and identity to be remembered per site, because
# Instagram wants the desktop identity and no images while Spotify will not play
# without them, and one switch for both means flipping it by hand on every
# crossing. Issue #75 asked for a way between two sites that is faster than
# typing an address, which is the same question about the same word: what counts
# as one site.
#
# That word is the whole risk. This file is in src/common, so it is in all six
# packages, and every way of getting "the same site" wrong is silent from a TV:
# too narrow and a rule stops applying the moment a site hands you to another of
# its own hosts, too wide and one site's settings land on a stranger's — a
# suffix test that lets notinstagram.com match instagram.com looks exactly like
# a rule that works, right up until it does not.
#
# This compiles the shipping src/common/SiteRules.cs against the Store and
# HomePage it leans on (with a stub for the diagnostics log) and holds it to:
#   1. the name a site is remembered under — www., case, port, credentials — and
#      the addresses that are on no site at all, both shapes of this app's own
#      start screen among them;
#   2. what a rule covers: the site, anything under it, and nothing that merely
#      ends with the same letters; the most specific rule winning where two apply;
#   3. what a rule says: a field nobody set is not "off" — it follows the
#      browser-wide switch — and a second press edits the covering rule rather
#      than shadowing it;
#   4. the file: a round trip through the disk, forgetting that reports whether
#      it did anything, and a hand-edited or future-build file that cannot stop
#      the browser from starting;
#   5. the other site: that with favourites it goes round them in tile order,
#      each at the last page seen on that site (issue 100's third site), and
#      without them alternates between the two sites somebody is going back and
#      forth between; treats another host of the same site as the same site,
#      skips this app's own generated pages, and has an answer when there is
#      nowhere to go.
#   6. the proxy (issue 97, NUI only): the third field follows the same rules as
#      the other two, and the typed address is taken apart so the login never
#      reaches the engine's proxy string or anything shown on screen or on the
#      diagnostics page.
# Needs only the .NET 6 SDK under ~/.dotnet-local.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
if [ ! -x "$DOTNET" ]; then
  echo "siterules: no dotnet at $DOTNET (set DOTNET=...)" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp Program.cs "$work/"
cat > "$work/siterules.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <Nullable>disable</Nullable>
    <RootNamespace>Overscan</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$PWD/../../src/common/SiteRules.cs" />
    <Compile Include="$PWD/../../src/common/Store.cs" />
    <Compile Include="$PWD/../../src/common/HomePage.cs" />
    <Compile Include="$PWD/../../src/nui/ProxyAddress.cs" />
  </ItemGroup>
</Project>
CSPROJ

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" run --project "$work/siterules.csproj" -c Release -- "$work/data"
