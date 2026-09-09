#!/usr/bin/env bash
# Exercises the on-screen keyboard's bookkeeping — KeyboardLayouts and
# KeyboardEntry — off-device.
#
#   ./run.sh
#
# Issue #92: "when we move selector from last 2nd row to last row in keyboard it
# moves 2 character back and moves 2 character forward when move from last to 1st
# row". The rows are centred and the action row is wider than the letter rows, so
# a column index carried across unchanged is a different place on the screen; both
# keyboards did exactly that. The same report asked for arrow keys, which is the
# caret the entry never had — a typo in the middle of an address meant deleting
# everything after it.
#
# This compiles the shipping src/common/KeyboardLayouts.cs and KeyboardEntry.cs
# (with stubs for the store and the diagnostics log) and holds them to:
#   1. every grid the keyboard can show still has the shape the keyboards build
#      their cells for — five rows, the four letter/digit rows all one width;
#   2. a move between rows lands on the key underneath, not the key with the same
#      index: from a letter row to the action row and back returns to the same
#      key, the edge columns land inside the narrower row, and the offset the
#      reporter saw is gone in both directions;
#   3. the caret: opens at the end, types at the caret, backspaces before it,
#      stops at both ends, and reports "nothing to delete" only when the caret is
#      at the start so the remote's Back key closes the keyboard exactly then.
# Needs only the .NET 6 SDK under ~/.dotnet-local.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
if [ ! -x "$DOTNET" ]; then
  echo "keyboard: no dotnet at $DOTNET (set DOTNET=...)" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp Program.cs "$work/"
cat > "$work/keyboard.csproj" <<CSPROJ
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
    <Compile Include="$PWD/../../src/common/KeyboardLayouts.cs" />
    <Compile Include="$PWD/../../src/common/KeyboardEntry.cs" />
  </ItemGroup>
</Project>
CSPROJ

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" run --project "$work/keyboard.csproj" -c Release
