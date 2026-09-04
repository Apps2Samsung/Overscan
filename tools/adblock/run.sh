#!/usr/bin/env bash
# Exercises the ad-block host matcher — src/nui/AdHosts.cs with the shipping
# src/nui/adhosts.txt embedded — off-device.
#
#   ./run.sh
#
# The matcher answers one question per request the engine makes, on a TV, on a
# thread that is not ours (see NuiAdBlock.cs). Every way it can be wrong reaches
# us from a set as either "the site is broken" or "pages got slower", and neither
# says whether the list, the match or the parsing did it. So this holds it to:
#   1. the embedded list loads and is the size it should be;
#   2. host extraction: scheme, credentials, port, IPv6, query, and the
#      addresses with no host at all (data:, about:, relative);
#   3. matching: a listed host, every subdomain of it, and none of the look-alikes
#      (a longer name that merely ends in a listed one, the top-level domain);
#   4. the sites this app is used on are not on the list — Instagram, Spotify,
#      Google sign-in, their CDNs — because a list that refuses the page itself is
#      worse than no list;
#   5. cost: a lookup stays in the low microseconds on the desktop, which is the
#      number the report's "handler" line is compared against later;
#   6. the request trail (RequestTrail.cs, the report's "requests this run"
#      section): the key folds a URL to host/first-segment and never further,
#      the header lookup is case-blind, the line cap holds, and a record costs
#      microseconds too.
# Needs only the .NET 6 SDK under ~/.dotnet-local.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
if [ ! -x "$DOTNET" ]; then
  echo "adblock: no dotnet at $DOTNET (set DOTNET=...)" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp Program.cs "$work/"
cat > "$work/adblock.csproj" <<CSPROJ
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
    <Compile Include="$PWD/../../src/nui/AdHosts.cs" />
    <Compile Include="$PWD/../../src/nui/RequestTrail.cs" />
    <EmbeddedResource Include="$PWD/../../src/nui/adhosts.txt" LogicalName="Overscan.adhosts.txt" />
  </ItemGroup>
</Project>
CSPROJ

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" run --project "$work/adblock.csproj" -c Release
