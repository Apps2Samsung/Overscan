#!/usr/bin/env bash
# Exercises NuiMseHold — issue #100's fix — against desktop chromium's real
# MediaSource.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# TikTok readies the next reel in a second <video> while the current one plays,
# the set has one hardware decoder for the two of them, and the reel on screen
# freezes. NuiMseHold holds a blob:/srcObject source set on a paused <video> back
# while another <video> is playing, and applies it the moment the page calls
# play() on that element.
#
# It is the one script we inject that changes how a page loads video, so the
# harness holds it to the contract and to nothing else: held while another video
# plays or is itself held (the tick between pausing one reel and playing the next,
# where the 2026-10-05 live-stream freeze was sourced), applied on play(), the
# source read back as if set meanwhile (src, the attribute, currentSrc), untouched
# when nothing plays and nothing is held, untouched on autoplay, dropped when the
# page takes the source away, and a revoke of a held URL surviving to the release. Every case
# is a real MediaSource on a real element, and "opened" means chromium fired
# sourceopen, which it only does once the engine has the element's source.
#
# The script is lifted out of src/nui/NuiMseHold.cs rather than copied: a
# harness that tests its own copy tests nothing.
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
    cp "$page" /mnt/c/Windows/Temp/ovs-msehold-test.html
    url="file:///C:/Windows/Temp/ovs-msehold-test.html"
    ;;
esac

# The autoplay policy flag is what lets play() on a muted element without a
# gesture flip `paused`, which is the state the hold keys on.
# Under a wall-clock limit: a harness that never comes back is the one failure
# mode nobody notices, and the first run of this one did exactly that once.
out="$(timeout 120 "$CHROME" --headless=new --disable-gpu --no-sandbox \
        --autoplay-policy=no-user-gesture-required \
        --virtual-time-budget=14000 --dump-dom "$url" 2>/dev/null \
      | sed -n '/RESULTS/,/<\/div>/{p;/<\/div>/q}' | sed 's/<[^>]*>//g')"

echo "$out"
if [ -z "$out" ] || echo "$out" | grep -q FAIL; then
  echo
  echo "msehold: FAILED" >&2
  exit 1
fi
echo
echo "msehold: all checks passed"
