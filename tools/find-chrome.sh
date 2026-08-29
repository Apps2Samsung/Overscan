# Finds a desktop chrome for the three harnesses. Sourced, not run:
#
#   . "$(dirname "$0")/../find-chrome.sh"
#
# Sets CHROME, and LD_LIBRARY_PATH with it where that is what makes it start.
# Honours a CHROME already in the environment and does nothing in that case.
#
# **A Windows chrome reached over /mnt/c is not enough for all three.** It is fine
# for the two harnesses that only need `--dump-dom` on a file:// page, which is why
# they found one and worked for months. It cannot run tools/cdpharness at all: that
# one needs the debug port, chrome binds the port to *Windows'* loopback, and WSL
# cannot reach it. `--remote-debugging-address=0.0.0.0` does not help — chrome
# accepts the flag and still says `DevTools listening on ws://127.0.0.1`. So the
# frame-click harness quietly stopped being run on this box, which is the worst way
# for a harness to fail: it does not fail, it is absent.
#
# The fix is a Linux chrome in the checkout owner's home, no root required:
#
#   V=$(curl -s https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions.json \
#       | python3 -c 'import json,sys; print(json.load(sys.stdin)["channels"]["Stable"]["version"])')
#   mkdir -p ~/.local/chrome && cd ~/.local/chrome
#   curl -fLO "https://storage.googleapis.com/chrome-for-testing-public/$V/linux64/chrome-linux64.zip"
#   unzip -q -o chrome-linux64.zip
#
#   # it ships no nss/nspr/alsa, and those are the only ones it is missing
#   cd "$(mktemp -d)" && apt-get download libnspr4 libnss3 libasound2t64
#   for d in *.deb; do dpkg-deb -x "$d" ~/.local/chromelibs; done
#
# Same shape as the openssl11 prefix build.sh feeds the 3.1 SDK, and for the same
# reason: the box has no root and does not need any.

_ovs_local_chrome="$HOME/.local/chrome/chrome-linux64/chrome"
_ovs_local_libs="$HOME/.local/chromelibs/usr/lib/x86_64-linux-gnu"

if [ -z "${CHROME:-}" ]; then
  for _ovs_candidate in \
      "$_ovs_local_chrome" \
      google-chrome google-chrome-stable chromium chromium-browser \
      "/mnt/c/Program Files/Google/Chrome/Application/chrome.exe" \
      "/mnt/c/Program Files (x86)/Google/Chrome/Application/chrome.exe"; do
    if command -v "$_ovs_candidate" >/dev/null 2>&1 || [ -x "$_ovs_candidate" ]; then
      CHROME="$_ovs_candidate"
      break
    fi
  done
fi

# Only the unpacked Chrome for Testing needs the prefix; a packaged chrome has its
# own libraries and handing it these would be a good way to break it.
if [ "${CHROME:-}" = "$_ovs_local_chrome" ] && [ -d "$_ovs_local_libs" ]; then
  export LD_LIBRARY_PATH="$_ovs_local_libs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi

unset _ovs_candidate _ovs_local_chrome _ovs_local_libs

if [ -z "${CHROME:-}" ]; then
  echo "no chrome found — set CHROME=/path/to/chrome, or install one the way" >&2
  echo "tools/find-chrome.sh describes at the top." >&2
  exit 2
fi
