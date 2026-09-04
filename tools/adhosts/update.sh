#!/usr/bin/env bash
# Refreshes src/nui/adhosts.txt, the ad and tracker host list compiled into the
# NUI package (issue #37).
#
#   tools/adhosts/update.sh
#
# The source is Peter Lowe's blocklist (https://pgl.yoyo.org/adservers/), chosen
# over the larger unified lists for one reason: size. It is about 3,500 hosts and
# 58 KB as plain text, which the tpk's own zip takes down to about 22 KB. A
# 100,000-host list would be larger than the rest of the package put together,
# and this app ships to sets where every kilobyte is read off a USB stick.
#
# The list is committed rather than fetched at build time: CI must not depend on a
# third party being up, and a build has to be reproducible from the repo alone.
# Run this when the list is stale, look at the diff, commit it.
#
# Only the host column is kept, lower-cased, de-duplicated and sorted, with a
# header naming the source and the date. AdHosts.cs skips blank lines and lines
# starting with '#'.
set -euo pipefail
cd "$(dirname "$0")/../.."

url='https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext'
out=src/nui/adhosts.txt
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

curl -sSfL "$url" -o "$tmp"
hosts="$(awk '$1 == "127.0.0.1" || $1 == "0.0.0.0" { print tolower($2) }' "$tmp" | grep -E '^[a-z0-9.-]+\.[a-z0-9-]+$' | sort -u)"
count="$(printf '%s\n' "$hosts" | wc -l | tr -d ' ')"

{
  echo "# Ad and tracker hosts refused by the NUI build (issue #37)."
  echo "# Source: Peter Lowe's blocklist, https://pgl.yoyo.org/adservers/"
  echo "# Fetched: $(date -u +%Y-%m-%d) by tools/adhosts/update.sh, $count hosts."
  echo "# Matched by whole host or any parent domain; see src/nui/AdHosts.cs."
  printf '%s\n' "$hosts"
} > "$out"

echo "adhosts: wrote $count hosts to $out"
