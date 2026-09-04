#!/usr/bin/env bash
# Hands the clip src/nui/AdSilence.cs ships to a real decoder and asks what it is.
#
#   ./run.sh
#
# The clip is forty MPEG-1 Layer III frames this app builds byte by byte, and it
# is served to Spotify's web player in place of an audio ad (issue #37). Every way
# it can be wrong reaches us from a TV as one of two reports, neither of which
# names the clip: "the music stopped playing" if it does not decode, so the player
# waits on an ad that never ends, or "there is a noise where the ad was" if the
# frames decode to something other than silence. A reading of the MPEG spec is not
# evidence for either.
#
# So this compiles the shipping file, writes the bytes it produces into a page,
# and lets chromium's own decoder — the same family as the engine on the set —
# answer. It decodes with OfflineAudioContext rather than playing an <audio>
# element, because headless chrome has no audio device and never advances a media
# clock: a play()-to-ended test there passes by timing out. decodeAudioData needs
# neither, and it hands back the samples, which is what makes "it is silent" a
# measurement rather than an assumption.
#
# Needs the .NET 6 SDK under ~/.dotnet-local and a chrome; tools/find-chrome.sh
# finds one and its header says how to install it if there is none.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet-local/dotnet}"
if [ ! -x "$DOTNET" ]; then
  echo "adsilence: no dotnet at $DOTNET (set DOTNET=...)" >&2
  exit 1
fi

# shellcheck source=../find-chrome.sh
. "../find-chrome.sh"
if [ -z "${CHROME:-}" ]; then
  echo "adsilence: no chrome found; see tools/find-chrome.sh" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp Program.cs "$work/"
cat > "$work/adsilence.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <Nullable>disable</Nullable>
    <RootNamespace>Overscan</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$PWD/../../src/nui/AdHosts.cs" />
    <Compile Include="$PWD/../../src/nui/AdSilence.cs" />
    <EmbeddedResource Include="$PWD/../../src/nui/adhosts.txt" LogicalName="Overscan.adhosts.txt" />
  </ItemGroup>
</Project>
CSPROJ

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
DOTNET_ROOT="$(dirname "$DOTNET")" "$DOTNET" run --project "$work/adsilence.csproj" -c Release \
  --verbosity quiet -- "$work/clip.html"

page="$work/clip.html"
url="file://$page"
case "$CHROME" in
  /mnt/c/*)
    cp "$page" /mnt/c/Windows/Temp/ovs-adsilence-clip.html
    url="file:///C:/Windows/Temp/ovs-adsilence-clip.html"
    ;;
esac

# Under a timeout because a harness that hangs is worse than one that fails: it
# is how tools/cdpharness quietly stopped being run on this box for months.
dom="$(timeout 120 "$CHROME" --headless=new --disable-gpu --no-sandbox \
        --virtual-time-budget=15000 --dump-dom "$url" 2>/dev/null || true)"

result="$(printf '%s' "$dom" | python3 -c \
  'import sys,re; m=re.search(r"<pre id=\"o\">(.*?)</pre>", sys.stdin.read(), re.S); print(m.group(1) if m else "no result")')"

echo
echo "$result"
echo

fail=0
check() { # check <ok?> <what>
  if [ "$1" = "1" ]; then echo "ok   $2"; else echo "FAIL $2"; fail=1; fi
}
field() { printf '%s\n' "$result" | sed -n "s/^$1=//p"; }

case "$result" in
  *"DECODE FAILED"*|*"no result"*|"") check 0 "the decoder accepted the clip";;
  *) check 1 "the decoder accepted the clip";;
esac

# The bytes the decoder was handed, as a cross-check that what this harness ran
# on is the clip the app builds and not a stale page left in the work directory.
[ "$(field bytes)" = "4160" ] && check 1 "4,160 bytes of clip" || check 0 "4,160 bytes of clip, got '$(field bytes)'"

# One channel at 44.1 kHz is what the frame header claims; a decoder that came
# back with anything else would mean the header and the stream disagree.
[ "$(field channels)" = "1" ] && check 1 "one channel" || check 0 "one channel, got '$(field channels)'"
[ "$(field rate)" = "44100" ] && check 1 "44100 Hz" || check 0 "44100 Hz, got '$(field rate)'"

# The duration is the load-bearing number: uBlock's half-second clip left the
# player stuck on the ad (uAssets #18148), so short is a failure, not a rounding.
dur="$(field duration)"
awk -v d="$dur" 'BEGIN { exit !(d >= 1.0 && d <= 1.2) }' \
  && check 1 "a second long: ${dur}s" || check 0 "a second long, got '${dur}'"

# And silent. Zeroed Layer III frames are meant to decode to nothing at all; if
# they decode to anything, it plays over the TV's speakers instead of the ad.
peak="$(field peak)"
[ "$peak" = "0" ] && check 1 "every sample is exactly zero" || check 0 "every sample is exactly zero, peak was '$peak'"

echo
if [ "$fail" = "0" ]; then
  echo "adsilence: all checks passed"
else
  echo "adsilence: FAILED"
fi
exit "$fail"
