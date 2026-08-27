#!/usr/bin/env bash
# Clicks into a cross-site <iframe> with the NUI build's own inspector client,
# against desktop chromium, and checks that the frame saw a real click.
#
#   ./run.sh
#   CHROME=/path/to/chrome ./run.sh
#
# This is the captcha of issue #20 reduced to its shape: a page on one site with
# a button inside a frame served from another, which chromium puts in its own
# renderer exactly as it does a reCAPTCHA. Nothing in the page can reach into it,
# and a click that arrives has to have been routed there by the browser's own hit
# test — so if the frame reports `trusted=true`, the mechanism the TV build
# depends on is working.
#
# The point of running it here is the round trip: a build for the TV has to be
# signed, installed and reported back on by somebody who owns the set, and three
# separate bugs in this client showed up in the first ten minutes against desktop
# chromium (a server that ignores HTTP/1.0, a server that ignores
# `Connection: close`, and a socket that reports itself open long after its target
# has gone). None of them would have been distinguishable, from a TV, from "the
# engine ignores the click".
set -euo pipefail

cd "$(dirname "$0")"

PAGE_PORT=8801
FRAME_PORT=8802
DEBUG_PORT=9333

CHROME="${CHROME:-}"
if [ -z "$CHROME" ]; then
  for candidate in google-chrome google-chrome-stable chromium chromium-browser; do
    if command -v "$candidate" >/dev/null 2>&1; then
      CHROME="$(command -v "$candidate")"
      break
    fi
  done
fi

if [ -z "$CHROME" ]; then
  echo "no chrome found — set CHROME=/path/to/chrome" >&2
  exit 2
fi

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
work="$(mktemp -d)"
pids=()

cleanup() {
  for pid in "${pids[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$work"
}
trap cleanup EXIT

python3 site/serve.py "$PAGE_PORT" site/page & pids+=($!)
python3 site/serve.py "$FRAME_PORT" site/frame & pids+=($!)
sleep 1

# Two names for one loopback: same origin is not enough, because chromium splits
# renderers by *site*, and it is the split that makes this the captcha's case.
"$CHROME" --headless=new --no-sandbox --disable-dev-shm-usage \
  --user-data-dir="$work/profile" \
  --remote-debugging-port="$DEBUG_PORT" \
  --host-resolver-rules="MAP a.test 127.0.0.1, MAP b.test 127.0.0.1" \
  --window-size=1280,800 \
  "http://a.test:$PAGE_PORT/index.html" >"$work/chrome.log" 2>&1 & pids+=($!)

for _ in $(seq 30); do
  if curl -sf --max-time 1 "http://127.0.0.1:$DEBUG_PORT/json/list" >/dev/null; then
    break
  fi
  sleep 0.5
done

# The page announces the frame's position in its own title once it has loaded,
# so a title that has not turned into one yet means there is nothing to click at.
for _ in $(seq 30); do
  targets="$(curl -s "http://127.0.0.1:$DEBUG_PORT/json/list")"
  if echo "$targets" | grep -q '"title": "rect:'; then
    break
  fi
  sleep 0.5
done

echo "$targets" | python3 -c "
import json, sys
for t in json.load(sys.stdin):
    if t['type'] in ('page', 'iframe'):
        print('  target', t['type'], t['url'][:60])
"

# The frame's own coordinates plus its offset in the page: the point handed to
# the client is in the page's space, and the browser is what translates it.
read -r x y <<<"$(echo "$targets" | python3 -c "
import json, sys
title = [t['title'] for t in json.load(sys.stdin) if t['type'] == 'page'][0]
left, top, width, height = [int(v) for v in title.split(':')[1].split(',')]
print(left + 2 + 130, top + 2 + 50)
")"

echo "== clicking $x,$y — inside the frame, in the page's coordinates"
"$DOTNET" build -c Release -v q --nologo >/dev/null
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" bin/Release/net6.0/cdpharness.dll "$DEBUG_PORT" "$x" "$y"

hits="$(curl -s "http://127.0.0.1:$FRAME_PORT/hits")"
echo "== the frame reports: ${hits:-nothing}"

case "$hits" in
  *trusted=true*)
    echo "PASS — a real click landed inside the cross-site frame"
    ;;
  *)
    echo "FAIL — the frame saw nothing the browser considered real" >&2
    exit 1
    ;;
esac
