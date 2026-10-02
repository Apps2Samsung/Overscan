#!/usr/bin/env bash
# Exercises EngineImports — the census of what a stub libprivileged-service-client.so
# would have to export (issue #105) — off-device, against the shipping file.
#
#   ./run.sh
#
# The census reads the engine implementation's dynamic section, dlopens each of its
# DT_NEEDED libraries, and names the imports that nothing loadable provides. Every
# way it can be wrong costs a reporter an evening: a symbol wrongly listed puts a
# useless export in the stub, a symbol wrongly omitted makes the stub fail to load
# the engine, and a parse that is off by a field names nonsense with confidence.
#
# So the harness builds the real shape in this box's own architecture: a library
# that needs libm, libc and a `libblocked.so`, imports two symbols from the latter
# (one function, one data object), and then makes libblocked.so unopenable the way
# the set does (a permission refusal, not a missing file). The census has to name
# exactly those two, say libblocked.so was refused and why, and leave cos and
# strlen out of it. The ELF32 path is exercised on the committed ARM
# Overscan5/res/libovprobe.so, which has nothing to import and must say so.
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

CC="${CC:-cc}"
if ! command -v "$CC" >/dev/null; then
  echo "no C compiler ($CC) to build the stand-in libraries — install one rather than skipping this" >&2
  exit 2
fi

work="$(mktemp -d)"
trap 'chmod -R u+w "$work" 2>/dev/null || true; rm -rf "$work"' EXIT

echo "== building the harness (with src/common/EngineImports.cs itself)"
"$DOTNET" build Harness.csproj -c Release -o "$work/bin" --nologo -v quiet
census="$work/bin/engineimports"

echo "== building the stand-in libraries"
mkdir -p "$work/lib"
printf 'int blocked_counter = 7;\nint blocked_alpha(int x) { return x + blocked_counter; }\n' > "$work/blocked.c"
"$CC" -shared -fPIC -Wl,-soname,libblocked.so -o "$work/lib/libblocked.so" "$work/blocked.c"
cat > "$work/needy.c" <<'C'
#include <math.h>
#include <string.h>
extern int blocked_counter;
int blocked_alpha(int x);
double needy_use(const char *s) { return cos((double)strlen(s)) + blocked_alpha(blocked_counter); }
C
"$CC" -shared -fPIC -Wl,-soname,libneedy.so -Wl,--no-as-needed -L"$work/lib" -o "$work/lib/libneedy.so" "$work/needy.c" -lblocked -lm

run() {
  local scenario="$1"; shift
  echo "== $scenario"
  timeout 120 "$census" "$scenario" "$@"
}

# The real shape. libblocked.so stays on disk, next to the implementation the way
# the set's is, and is made unopenable: the loader says "Permission denied", the
# set says "Operation not permitted", and the census only has to carry the words.
chmod 000 "$work/lib/libblocked.so"
run census "$work/lib/libneedy.so" libblocked.so
chmod 644 "$work/lib/libblocked.so"

# Nothing to import at all, on the committed ARM ELF32.
run clean ../../Overscan5/res/libovprobe.so

# A file that never answers: a FIFO with no writer blocks the open for as long as
# the census is willing to wait.
mkfifo "$work/hang.so"
run hang "$work/hang.so"

# No implementation anywhere.
run missing "$work/does-not-exist.so"

echo
echo "engineimports: all checks passed"
