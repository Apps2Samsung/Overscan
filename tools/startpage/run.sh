#!/usr/bin/env bash
# Exercises the start screen's bookkeeping — Store and HomePage — off-device.
#
#   ./run.sh
#
# Issue #53's black screen was the start screen eating itself. The NUI engine
# reports a page loaded from a string as a data: URL carrying the whole page, the
# store only knew the ElmSharp build's https://overscan.start/ marker, so every
# start screen was recorded as a visit and the next one carried it inside a
# tile. The page roughly doubled per launch until it passed Chromium's 2 MB URL
# ceiling, after which the engine dropped the load in silence — no start, no
# error, black. Nothing on the TV, and nothing in the ladder that ran on it,
# could tell that apart from an engine that will not navigate.
#
# This compiles the shipping src/common/Store.cs and src/common/HomePage.cs
# (with a stub for the diagnostics log) and holds them to three things:
#   1. a generated page — either shape — is never saved as a visit or favourite;
#   2. a history file an earlier build wrote, generated pages included, is healed
#      on load and written back clean;
#   3. twelve start screens in a row, each recorded the way the NUI engine would
#      report it, leave the page the size it started at;
#   4. a sign-in waypoint (a captcha, a code entry, an OAuth step, a URL too long
#      to be anything but a token) is passed through and not recorded, while a
#      host or a fragment that merely contains one of those words is kept;
#   5. a history file with waypoints in it is healed on load, favourites untouched.
# Number 3 is the bug itself: against the Store.cs before this fix it fails on
# the second screen. Numbers 4 and 5 are the same reporter's next report: six
# recent tiles, two of them 3 KB recaptcha pages nobody can go back to.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
if [ ! -x "$DOTNET" ]; then
  echo "startpage: no dotnet at $DOTNET (set DOTNET=...)" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp Program.cs "$work/"
cat > "$work/startpage.csproj" <<CSPROJ
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
    <Compile Include="$PWD/../../src/common/Store.cs" />
    <Compile Include="$PWD/../../src/common/HomePage.cs" />
  </ItemGroup>
</Project>
CSPROJ

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" run --project "$work/startpage.csproj" -c Release -- "$work/data"
