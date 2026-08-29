#!/usr/bin/env bash
# Exercises NuiVideoRect's geometry probe against desktop chromium.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# The in-page video path on issue #20's set never draws a picture, because the
# engine hands its sink a render rectangle with a zero width or height, 88 times
# out of 88. NuiVideoRect asks the page what box it thinks the video has, so the
# next report says whether that zero starts in the page's layout or below it in
# the engine's own hosting — the answer decides whether in-page video is worth
# another build on that set at all.
#
# It is a read-only probe, and that is exactly what this harness is here to hold
# it to. A probe that quietly paused, reloaded or reflowed a reel would reach us
# from a TV as "the video is black now", which is indistinguishable from the
# failure it was sent to explain — and this issue has already had one build that
# changed a page on a guess and cost the reporter his one working configuration.
# So the page below marks every call the probe must not make, and the run fails if
# any of them happens.
#
# This extracts the script from src/nui/NuiVideoRect.cs rather than keeping a copy,
# for the same reason tools/cdpharness compiles the shipping file: a harness that
# tests its own copy of the code tests nothing.
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
    cp "$page" /mnt/c/Windows/Temp/ovs-videorect-test.html
    url="file:///C:/Windows/Temp/ovs-videorect-test.html"
    ;;
esac

out="$("$CHROME" --headless=new --disable-gpu --no-sandbox \
        --virtual-time-budget=12000 --dump-dom "$url" 2>/dev/null \
      | sed -n '/RESULTS/,/<\/div>/{p;/<\/div>/q}' | sed 's/<[^>]*>//g')"

echo "$out"
if [ -z "$out" ] || echo "$out" | grep -q FAIL; then
  echo
  echo "videorect: FAILED" >&2
  exit 1
fi
echo
echo "videorect: all checks passed"
