#!/usr/bin/env bash
# Exercises NativeProbe's ladder — the walk that asks issue #17's set whether it
# will map native code of ours — off-device, against the shipping file.
#
#   ./run.sh
#
# The ladder's only real property is that it converges: whatever one rung does, the
# rungs and locations behind it get asked, and a launch that cannot finish leaves
# enough on disk for the next one to carry on. Two builds have now been spent
# finding out from a TV that it does not.
#
#   build-85d0e4e  ledgered nothing. The set died on the first rung and four of the
#                  five locations were never asked at all.
#   build-3368aea  ledgered the three rungs that looked dangerous. The set stopped
#                  between the open and the header read — two rungs earlier, in the
#                  window with nothing written down — so every launch walked into
#                  the same call, forever, and the report still read `(not asked)`.
#   build-f295172  put every rung under a deadline, and the set stopped in front of
#                  the first one: the ledger's own open, the one call on the path
#                  that was not. The walk wrote nothing for 84 seconds, and the page
#                  — fetched at launch, as every page from that set has been — read
#                  `(not asked)` over whatever the launch before had put on disk.
#   build-1a5fd68  reached the ladder on one launch in five — the other four stopped
#                  in the engine explainer in front of it — and that launch ended on
#                  the anonymous-exec control, a rung the set had answered `ok` three
#                  times before. One ended launch is not a refusal, and a ladder that
#                  is only reached one launch in three is not a ladder.
#
# Each round trip is somebody's evening: install, re-sign, launch, copy a page out
# of a TV browser. That is far too expensive a way to find out that a ladder does
# not climb, and every shape below is one this set has actually produced.
#
# The scenarios run one process each, because the probe's books are static state and
# the point of some of them is what a *fresh* launch does with a file left behind.
#
# `hang` is the one that holds this build to what it claims: against the previous
# NativeProbe.cs it never returns at all, and against the one before that the
# locations behind the stalled rung are never asked.
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
trap 'chmod -R u+w "$work" 2>/dev/null || true; rm -rf "$work"' EXIT

echo "== building the harness (with src/common/NativeProbe.cs itself)"
"$DOTNET" build Harness.csproj -c Release -o "$work/bin" --nologo -v quiet

probe="$work/bin/probeladder"

# A stand-in for Overscan5/res/libovprobe.so, in this box's own architecture and
# built to the same shape as the committed ARM one: one exported function, no
# DT_NEEDED, SONAME libovprobe.so. It is compiled rather than borrowed from the
# system because the probe's last rung dlopens it RTLD_GLOBAL and never closes it —
# a real system library dropped into this process's global namespace interposes on
# the runtime's own copy, which crashed this harness at shutdown before the answers
# could be read. A freestanding object with one symbol has nothing to interpose.
CC="${CC:-cc}"
if ! command -v "$CC" >/dev/null; then
  echo "no C compiler ($CC) to build the stand-in library — install one rather than skipping this" >&2
  exit 2
fi

library="$work/libovprobe.so"
printf 'int ov_probe_marker(void) { return 1; }\n' > "$work/ovprobe.c"
"$CC" -shared -nostdlib -fPIC -Wl,-soname,libovprobe.so -o "$library" "$work/ovprobe.c"

# The package layout the probe expects: res/, bin/ and lib/ each holding a copy, and
# a writable data/ for the ledger and the fourth copy.
layout() {
  local root="$1"
  mkdir -p "$root/res" "$root/bin" "$root/lib" "$root/data"
  cp "$library" "$root/res/libovprobe.so"
  cp "$library" "$root/bin/libovprobe.so"
  cp "$library" "$root/lib/libovprobe.so"
}

# Under a clock, because the failure this exists to catch is a walk that never
# finishes: pointed at build-3368aea's NativeProbe.cs the `hang` scenario below does
# not fail, it sits there — and a harness that hangs is a harness nobody runs.
run() {
  local scenario="$1" root="$work/$1"
  echo "== $scenario"
  timeout 120 "$probe" "$scenario" "$root"
}

# Every rung answers.
layout "$work/walk"
run walk

# A second launch replays instead of re-asking.
layout "$work/resume"
run resume

# A rung that never returns. A FIFO with no writer is an open() that blocks for as
# long as the probe is willing to wait, which is what this set does to us with a
# syscall on a file it will not talk about.
layout "$work/hang"
rm "$work/hang/res/libovprobe.so"
mkfifo "$work/hang/res/libovprobe.so"
run hang

# A rung whose name is on the ledger with no answer under it, from two launches:
# neither launch that asked it came back.
layout "$work/killed"
printf 'ledger 3\nlaunch\nres/:exec\nlaunch\nres/:exec\n' > "$work/killed/data/probe-ledger.txt"
run killed

# The same, once. build-1a5fd68's ledger came back exactly like this, on a rung the
# set had answered three times before, so one is asked again and two is a refusal.
layout "$work/abandoned"
printf 'ledger 3\nlaunch\nres/:exec\n' > "$work/abandoned/data/probe-ledger.txt"
run abandoned

# A ledger left by an earlier build, claiming an answer for a rung that may since
# have changed its meaning.
layout "$work/version"
printf 'ledger 1\nres/:exec\tPROT_READ|PROT_EXEC: ok\n' > "$work/version/data/probe-ledger.txt"
run version

# The ledger itself never opens. Same FIFO trick, on the one file the walk reads
# before it asks anything — the shape build-f295172 came back with.
layout "$work/ledgerhang"
mkfifo "$work/ledgerhang/data/probe-ledger.txt"
run ledgerhang

# A page loaded before the walk: the header and the block come off the disk, verdict
# included, and loading the page does not start the walk.
layout "$work/peek"
printf 'ledger 3\nlaunch\ncontrol:anon-exec\tok\nres/:exec\tPROT_READ|PROT_EXEC: EPERM (operation not permitted)\nverdict\tREFUSED in res/ — seeded by the harness\n' \
  > "$work/peek/data/probe-ledger.txt"
run peek

# An install where the engine has already failed and the ladder has no verdict: the
# walk goes ahead of the engine. Seeded with another build's ledger, which is the
# shape the Q80 has on disk right now.
layout "$work/early"
printf 'ledger 2\ncontrol:anon-exec\n' > "$work/early/data/probe-ledger.txt"
run early

# And the two installs it must leave alone: no ledger, and a finished one.
layout "$work/notearly"
run notearly

echo
echo "PASS — the ladder converges on all ten shapes"
