#!/usr/bin/env bash
# Exercises NuiMseWatch's MSE probe against desktop chromium's real MediaSource.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# Issue #100: TikTok reels freeze a few seconds in with data buffered, and the
# engine says nothing. NuiMseWatch wraps addSourceBuffer, appendBuffer,
# changeType, isTypeSupported and URL.createObjectURL to write down what the
# player feeds the decoder: the format, every init segment (a quality switch is
# a second one), and the state of the feed at the moment of a stall.
#
# Wrapping the player's own calls is the one way this probe could cause the
# failure it was sent to explain, so the harness holds it to being invisible:
# every wrapped call returns what the original returned, an exception from the
# original reaches the caller with its own name, and the probe never pauses,
# plays, loads or seeks. It also checks that it reads an init segment's codec
# and size, counts a second init as a switch, and reports a stall and its end.
#
# The script is lifted out of src/nui/NuiMseWatch.cs rather than copied, for the
# same reason as tools/msewatch: a harness that tests its own copy tests nothing.
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
    cp "$page" /mnt/c/Windows/Temp/ovs-msewatch-test.html
    url="file:///C:/Windows/Temp/ovs-msewatch-test.html"
    ;;
esac

out="$("$CHROME" --headless=new --disable-gpu --no-sandbox \
        --virtual-time-budget=12000 --dump-dom "$url" 2>/dev/null \
      | sed -n '/RESULTS/,/<\/div>/{p;/<\/div>/q}' | sed 's/<[^>]*>//g')"

echo "$out"
if [ -z "$out" ] || echo "$out" | grep -q FAIL; then
  echo
  echo "msewatch: FAILED" >&2
  exit 1
fi
echo
echo "msewatch: all checks passed"
