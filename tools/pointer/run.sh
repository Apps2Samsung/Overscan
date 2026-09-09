#!/usr/bin/env bash
# Exercises the page script's half of the D-pad pointer against desktop chromium.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# Issue #91: "sometimes when load app where it shows two cursor one on top of
# other". The NUI build draws the pointer itself and asks the page script to hide
# its arrow, and the page script re-installs that arrow on every visibilitychange
# — which the app coming to the front at launch is — with no memory of having
# been told to hide it. Both are positioned from the same viewport fraction, so
# the arrow came up exactly under the dot.
#
# This lifts the script out of src/common/PageScript.cs, which is in all six
# packages, and holds it to: a fresh install shows the arrow (what the ewk builds
# rely on), hide() is remembered through a visibilitychange, a re-install, a move
# and a re-evaluation of the whole script, show() is the only way back, and the
# arrow is still put back into the DOM after a single-page navigation wiped it —
# hidden. Against the PageScript.cs before this fix the third check fails.
set -euo pipefail
cd "$(dirname "$0")"

# CWD is this script's own directory by now — see the cd above.
. ../find-chrome.sh

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

python3 build-page.py "$work/test.html"

# A chrome.exe reached over /mnt/c cannot open a WSL path, so the page goes
# somewhere both sides can name.
page="$work/test.html"
url="file://$page"
case "$CHROME" in
  /mnt/c/*)
    cp "$page" /mnt/c/Windows/Temp/ovs-pointer-test.html
    url="file:///C:/Windows/Temp/ovs-pointer-test.html"
    ;;
esac

out="$("$CHROME" --headless=new --disable-gpu --no-sandbox \
        --virtual-time-budget=3000 --dump-dom "$url" 2>/dev/null \
      | sed -n '/RESULTS/,/<\/div>/{p;/<\/div>/q}' | sed 's/<[^>]*>//g')"

echo "$out"
if [ -z "$out" ] || echo "$out" | grep -q FAIL; then
  echo
  echo "pointer: FAILED" >&2
  exit 1
fi
echo
echo "pointer: all checks passed"
