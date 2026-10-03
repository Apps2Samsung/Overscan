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
# strlen out of it. A second shape is a library that only loads in company — one
# that imports a data symbol from a sibling it does not name in its own DT_NEEDED
# — which the census must hold back, load on a second pass once the rest are in,
# and never count as refused or its exports as a stub's work (the AU7200's
# libwgt-manifest-handlers.so.1 put twenty-three C++ names on the first census
# that way). The ELF32 path is exercised on the committed ARM
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
# The same, taken on a thread of its own with the caller waiting — how OnCreate
# runs it ahead of the engine.
run background "$work/lib/libneedy.so" libblocked.so
chmod 644 "$work/lib/libblocked.so"

# A dependency that only loads in company (the AU7200's libwgt-manifest-handlers
# .so.1): libunder.so imports a data symbol from libsib.so without naming it in
# its own DT_NEEDED, and the implementation lists libunder.so first. Alone it is
# "undefined symbol"; after the rest have loaded it is not.
printf 'int sib_value = 3;\n' > "$work/sib.c"
"$CC" -shared -fPIC -Wl,-soname,libsib.so -o "$work/lib/libsib.so" "$work/sib.c"
printf 'extern int sib_value;\nint under_get(void) { return sib_value; }\n' > "$work/under.c"
"$CC" -shared -fPIC -Wl,-soname,libunder.so -o "$work/lib/libunder.so" "$work/under.c"
printf 'extern int sib_value;\nint under_get(void);\nint needy2_use(void) { return under_get() + sib_value; }\n' > "$work/needy2.c"
"$CC" -shared -fPIC -Wl,-soname,libneedy2.so -Wl,--no-as-needed -L"$work/lib" -o "$work/lib/libneedy2.so" "$work/needy2.c" -lunder -lsib
run underlinked "$work/lib/libneedy2.so"

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
