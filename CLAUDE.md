# Working on Overscan

A sideloadable web browser for Samsung TVs, in C# on Tizen. Everything here is
the stuff a session needs that the code does not say for itself.

`docs/INTERNALS.md` is the long-form record — every wall this app has hit and what
was behind it. **Read the section that covers what you are about to touch**, and
add to it when a change teaches something new. It is the reason the same dead end
has not been walked twice.

## Six packages, one `src/`

| Package | TFM | Engine | Sets |
| --- | --- | --- | --- |
| `Overscan4` | `tizen40` | `Tizen.WebView` | 2018, Tizen 4.0 |
| `Overscan5` | `tizen50` | `Tizen.WebView` | 2019–2020, Tizen 5.0–5.5 |
| `Overscan6` | `tizen60` | `Tizen.WebView` | 2021–2023, Tizen 6.0–7.0 |
| `Overscan` | `net6.0-tizen8.0` | `Tizen.WebView` | 2024, Tizen 8.0 |
| `OverscanNui` | `net6.0-tizen8.0` | NUI `WebView` | 2025+, Tizen 9.0–10.0 |
| `OverscanProbe` | `tizen50` | none — diagnostics | 5.0+ |

Sources are shared and selected per package by `<Compile Include>`:

- `src/common` — everything both engines use: the injected `PageScript`, storage,
  keyboards, the diagnostics trail, the engine and permission probes.
- `src/elm` — the ewk/ElmSharp builds (the four `Tizen.WebView` packages).
- `src/nui` — the NUI build. Nothing here is compiled into the others.
- `src/probe` — the diagnostics harness only.

A change in `src/common` lands in all six. A change in `src/nui` cannot break the
ewk packages, which is often the difference between a safe change and a risky one.

## Build before you push

Both toolchains are on the dev box, so there is no excuse for finding out from CI:

```sh
./build.sh all        # all five browser packages -> dist/
./build.sh nui        # just the 2025+ package
```

`~/.dotnet-local` (.NET 6 + tizen workload) builds the `net6.0-tizen8.0` targets;
`~/.dotnet-31` (.NET Core 3.1) builds the `tizenXX` ones, because a .NET 5+ SDK
rejects those TFMs outright. The 3.1 SDK needs OpenSSL 1.1, which is kept under
`~/.local/openssl11` and fed in by `build.sh`. The header of that script has the
detail.

`OverscanProbe` is not in `build.sh` (CI builds it). To check it compiles:

```sh
DOTNET_ROOT="$HOME/.dotnet-31" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  OPENSSL_CONF=/dev/null \
  LD_LIBRARY_PATH="$HOME/.local/openssl11/usr/lib/x86_64-linux-gnu" \
  "$HOME/.dotnet-31/dotnet" build OverscanProbe/OverscanProbe.csproj -c Debug
```

### The frame-click harness

Any change to the NUI cross-origin frame click — `NuiInspectorInput`,
`NuiInspector`, or the `FRAME:` half of `PageScript` — is exercised against
desktop chromium first:

```sh
tools/cdpharness/run.sh          # CHROME=/path/to/chrome if it is not on PATH
```

It compiles the shipping file itself and clicks into a genuinely cross-site
`<iframe>`, which is the captcha's shape. It found three bugs in the first ten
minutes of existing, each of which would have been indistinguishable, from a TV,
from "the engine ignores the click".

### The native probe library

`Overscan5/res/libovprobe.so` is the only native binary this repo ships: a tiny ARM
shared object that exists so issue #17's set can be asked whether this app may load
native code of its own at all. It is **committed**, because CI has no ARM toolchain.

Rebuild it only if `tools/elfprobe/ovprobe.s` changes:

```sh
apt-get download binutils-arm-linux-gnueabihf          # no root needed
dpkg-deb -x binutils-arm-linux-gnueabihf_*.deb /some/where
XTOOL=/some/where tools/elfprobe/build.sh
```

That script prints the ELF header and dynamic section at the end — check it still
says `DYN`, `ARM`, `SONAME libovprobe.so` and no `NEEDED`. A dependency on anything
would make a refusal to load ambiguous, which defeats the whole measurement.

## Releasing

```sh
gh workflow run build.yml --ref main -f release=true
```

Publishes a `build-<sha7>` tag with all six `.tpk` assets, default-signed. Issue
replies quote that tag. Reporters re-sign with their own partner certificate, so
signing is not in our path.

## Working an issue

Nobody debugging this app has the TV in the room. A build takes a sign, an
install and a reply from somebody being generous with their evening, so the
round trip is the scarce resource and the whole diagnostic design is built
around spending it well.

- **The trail is the evidence.** `Breadcrumbs` writes each line to disk on its
  own and names a native call *before* making it, so when the process dies the
  last line is the call that killed it. The previous run's trail is set aside at
  start-up — the app that dies has no "afterwards" in which to read anything back.
- **`http://<TV-IP>:8081`** serves the diagnostics report (`DiagServer`), and key
  `3` shows a shorter version on the TV. Ask for the *whole* page: the
  `previous run` block is where a launch actually ended, and the `this run` block
  only shows how far the current start-up had got when the page was loaded.
- **Whatever is most likely not to survive the firmware goes last.** A probe in
  front of the thing it is explaining has cost this project a build three times
  now — issues #13, #17 and #17 again. If a diagnostic can kill the process, it
  runs after the failure it is investigating, never before it.
- **One question per build**, and say plainly which answer means "this TV cannot
  run Overscan". Guessing costs somebody else an install.
- **Anything left open is written down in `docs/INTERNALS.md`, not carried in a
  head.** What is waiting on a reporter, what the next report decides, and any
  decision that is Patrick's rather than a session's. Two sections hold the current
  state and are the first thing to read when picking this up: *What is left on the
  Q80* (issue #17) and *What is left on the 2025 sets* (issue #20). Update them in
  the same change that ships the build they describe.
- **Every reply on an issue quotes a release tag, and every open question names what
  each possible answer decides** — including the answer that means "this set cannot
  run Overscan". Saying that in advance is what stops it reading as a reversal
  later.
- **Posting a comment on an issue is asked first.** Opening a PR, merging it and
  cutting a release are not — those were confirmed standing.

## Conventions

- Comments explain **why**, and are worth real length when the reason is not
  guessable from the code — the P/Invokes into DALi's binder and the engine's
  loader are the examples to match. Nothing restates what the line already says.
- Old C# on purpose: these targets are .NET Standard 2.0-era. No file-scoped
  namespaces, no `record`, no nullable annotations.
- **Everything that reaches past the managed surface is best-effort.** A failure
  is recorded by name and the app carries on — a browser that cannot click into a
  frame is still a browser.
- Commits and PRs carry no tool attribution.
- Checkout gotcha: on a `/mnt/c` Windows checkout every tracked file reports as
  modified. It is CRLF churn against `.gitattributes`, not work —
  `git diff --ignore-all-space` comes back empty. Working from a clone in a Linux
  path avoids it entirely.
