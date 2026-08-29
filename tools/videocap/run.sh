#!/usr/bin/env bash
# Exercises NuiVideoCap's sweep against desktop chromium.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# Issue #20's set segfaults inside the engine's GStreamer path as a third
# GstOmxUhdVideoDec is allocated, and the census on the same trail says
# `playing=1 of 16` — fifteen paused reels each still holding the decoder they
# were given. NuiVideoCap takes those back. It is the first script in this repo
# that *changes* the page rather than reading it, so the ways it can be wrong are
# the ways that matter: releasing something the viewer can see, failing to put an
# ordinary source back, or guessing at a blob: URL it cannot recreate.
#
# None of those are visible from a TV — they arrive as "video is black now",
# which is indistinguishable from the engine's own failures. So the sweep is
# asked here first, against a real <video> in a real layout engine.
#
# This extracts the script from src/nui/NuiVideoCap.cs rather than keeping a copy,
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
    cp "$page" /mnt/c/Windows/Temp/ovs-videocap-test.html
    url="file:///C:/Windows/Temp/ovs-videocap-test.html"
    ;;
esac

out="$("$CHROME" --headless=new --disable-gpu --no-sandbox \
        --virtual-time-budget=12000 --dump-dom "$url" 2>/dev/null \
      | sed -n '/RESULTS/,/<\/div>/{p;/<\/div>/q}' | sed 's/<[^>]*>//g')"

echo "$out"
if [ -z "$out" ] || echo "$out" | grep -q FAIL; then
  echo
  echo "videocap: FAILED" >&2
  exit 1
fi
echo
echo "videocap: all checks passed"
