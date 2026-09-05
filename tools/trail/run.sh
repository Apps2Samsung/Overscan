#!/usr/bin/env bash
# Exercises Breadcrumbs — the trail every diagnostic in this app reports through —
# off-device, against the shipping file.
#
#   ./run.sh
#
# The trail has two properties that pull against each other, and this holds both:
#
#   * A line is on disk before Drop returns, whenever the disk is behaving. That is
#     the property the whole diagnostic design rests on — the last line is the call
#     that killed us — and it is what makes the trail readable after a hard crash.
#   * A disk (or a dlog) that is not behaving cannot hold the thread that dropped
#     the line for more than two seconds, cannot hold the lines behind it at all,
#     and cannot hide: the report's header says which write is stuck, on what, for
#     how long, with how many lines behind it.
#
# The second was not true before build-1933ad8. Issue #17's set stopped launch after
# launch on calls that had returned promptly the launch before — and every one of
# those calls was immediately followed by a trail write. On 2026-09-05 a launch went
# silent with a watchdog and a heartbeat both armed on threads of their own, and
# another stayed alive enough to serve the diagnostics page with its trail stopped a
# second in. A trail written on the caller's thread cannot tell a stuck call from a
# stuck write, and instruments that report through the same write cannot say
# anything at all. Each scenario runs in a process of its own because the writer is
# static state.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet-local}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if [ ! -x "$DOTNET" ]; then
  echo "no dotnet at $DOTNET — see the toolchain notes in ../../build.sh" >&2
  exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== building the harness (with src/common/Breadcrumbs.cs and DiagLog.cs themselves)"
"$DOTNET" build Harness.csproj -c Release -o "$work/bin" --nologo -v quiet

trail="$work/bin/trail"

# Under a clock, because the failure this exists to catch is a Drop that never
# returns — and a harness that hangs is a harness nobody runs.
run() {
  local scenario="$1"
  mkdir -p "$work/$scenario"
  echo "== $scenario"
  timeout 60 "$trail" "$scenario" "$work/$scenario"
}

run plain
run filehang
run dloghang

echo
echo "PASS — the trail keeps both promises on all three shapes"
