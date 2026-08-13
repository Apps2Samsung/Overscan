# Overscan

A sideloadable web browser for Samsung Tizen TVs, built as a `.tpk`.

*Overscan*: the picture area a TV pushes past the visible edge — which is roughly
what this does to the web on a television.

The TV's built-in browser sends a Samsung TV user agent, so a lot of sites serve a
stripped "smart TV" or legacy-mobile layout. This app wraps the same Chromium
engine in an app we control, so it can:

* force a **desktop user agent** (the layout problem),
* keep **JavaScript on**,
* drive a **D-pad cursor** so pages built for a mouse are usable from the remote.

Requested in [Apps2Samsung/tizen-community-packages#24](https://github.com/Apps2Samsung/tizen-community-packages/issues/24).

## What a .tpk can and cannot do here

| Goal | Status |
| --- | --- |
| Override the user agent | Real API: `Tizen.WebView.WebView.UserAgent` (since API 4) |
| Keep JavaScript enabled | Real API: `Settings.JavaScriptEnabled` |
| D-pad pointer | Has to be built by hand — see below |
| Ship a *newer* Chromium | **Not possible.** The web view is the platform engine |

The last row is the honest limitation: a `.tpk` gets a `WebView` bound to the
firmware's own chromium-efl, the very same engine the built-in browser uses
(TV 5.0 = M63, 5.5 = M69, 6.0 = M76, 6.5 = M85, 7.0 = M94, 8.0 = M108,
**10.0 = M130, measured**). This app fixes *how the TV identifies itself*, not how
old the engine is.

That measurement reframes the caveat by platform: on an old set the engine really
is ancient and some sites will break regardless. On Tizen 10 the engine is
Chromium 130 — genuinely current — so there the UA *was* the entire problem, which
is exactly what the issue reported.

Note the UA shape, because it silently broke the version detection: newer Samsung
TVs carry **no `Chrome/` token** at all —
`…(KHTML, like Gecko) 130.0.6723.116/10.0 TV Safari/537.36`.

Because of that, the default UA preset claims a desktop Chrome whose version
**matches the engine actually running** (parsed out of the TV's own UA at startup)
instead of a much newer one — a desktop layout without lying about capabilities.
Cycling to a spoofed-newer UA is one keypress away for comparison.

### Why `Tizen.WebView` and not the NUI WebView

Nearly all the documentation points at `Tizen.NUI.BaseComponents.WebView`. That
class is `since_tizen 9` — in TizenFX API 8 and earlier it lives under
`src/Tizen.NUI/src/internal/`, i.e. it is not callable from an app. `Tizen.WebView`
(ElmSharp + ewk) has been public since API 4, so one source tree builds for TV 5.0
through 8.0+.

The trade-off runs the other way at the top end: `Tizen.WebView` is deprecated at
API 10 and **removed at API 12**. So a future 9.0+-only package would have to be
written against NUI WebView instead. Between the two, nothing covers the whole
range — 5.0-8.0 is `Tizen.WebView`, 9.0+ is NUI.

### D-pad cursor design

Chromium-EFL exposes no way to inject synthetic mouse input from an app (the NUI
binding has only `FeedMouseWheel`). So the cursor lives in the page: `src/PageScript.cs`
injects an overlay element plus `move/click/scroll` helpers, and the native side
sends viewport *fractions* so it never needs to know the page's CSS size, zoom or
DPR. Clicks are dispatched `mousedown`/`mouseup`/`click` on `elementFromPoint`,
which still runs an element's default action (links and buttons work). Scrolling
walks up to the innermost scrollable ancestor first, so SPAs that scroll a `div`
respond.

An Evas-overlay cursor is implemented too (key `2` toggles) because it is not yet
verified which of the two composites correctly above the web view on a real TV.

## Layout

```
src/                  all C# — shared verbatim by both packages
Overscan/       net6.0-tizen8.0 package (TV 8.0+, .NET 6)
Overscan5/      tizen50 package (TV 5.0+ armv7, classic Tizen.NET.Sdk)
```

Shared source means everything must stay inside the TizenFX **API 5** surface of
`Tizen.WebView` (so: no `ScrollPosition`, `LoadProgress`, `EvalAsync`, or
`Settings.ScriptsCanOpenWindows` — those are API 6+).

## Backlog

* **Port the on-screen key grid to the ElmSharp build.** That build still uses an
  ElmSharp `Entry` for the address bar, which depends on the TV raising its own
  IME — unverified on a real set, and known to vary. The NUI build already has
  `NuiKeyboard`, a D-pad grid that needs no IME and can also type into *page*
  fields (in-page search boxes). If the 5.0/5.5 test shows the IME does not
  appear, or page fields stay untypable, this is the fix. `PageScript.type`/
  `submit` are already shared, so only the grid UI needs an ElmSharp twin.
* No favourites/history, and no download or file-picker handling.
* The Evas-overlay cursor in the ElmSharp build is still unverified on hardware;
  the page-drawn cursor is the one proven to work (on NUI).

## Remote controls

| Key | Action |
| --- | --- |
| D-pad | move the cursor (accelerates while held) |
| OK | click whatever is under the cursor |
| Back | close overlay → page back → exit |
| Channel ▲/▼ | page up / page down |
| `0` / Menu / Info | focus the address bar |
| `1` | cycle user-agent preset and reload |
| `2` | toggle cursor drawing (page overlay ⇄ Evas overlay) |
| `3` | diagnostics overlay |
| `4` | hand the keys to the page (for typing in web forms) |

## Build

```bash
./build.sh            # both packages -> dist/
./build.sh tizen8     # net6.0-tizen8.0 only
./build.sh tizen5     # tizen50 only
```

`build.sh` documents the toolchain: two userspace SDKs (`~/.dotnet-local` with the
`tizen` workload for tizen8, `~/.dotnet-31` for tizen50), plus an extracted
OpenSSL 1.1 that .NET Core 3.1 needs and Ubuntu 24.04 does not ship.
`.github/workflows/build-tpk.yml` does the same on runners (ubuntu-22.04 for the
3.1 job).

Both emit a default-signed `.tpk`. A retail TV rejects that signature; re-sign
with a DUID-bound developer certificate (Apps2Samsung does this) before
installing.

### Emulator

**There is no separate x86 package, and none is needed.** This app is pure managed
IL — the `.tpk` payload is `bin/Overscan.dll` and the icon, no native
binaries — so the same package runs on x86 and ARM. (Contrast `tailscale-tizen`,
which had to be rebuilt per arch because it bundled a native Go daemon.)

Use `dist/Overscan-tizen8.tpk` on the Tizen 9.0 TV emulator: `api-version`
8.0 is ≤ the emulator's platform, which is the direction that has to hold. Sign
with the emulator profile rather than a DUID cert:

```
tizen package -t tpk -s Emul9 -- Overscan-tizen8.tpk
sdb install Overscan-tizen8.tpk
```

Emulator notes:

* **The VS Code plugin's "start emulator" does not work on this machine, and the
  VM needs two extra QEMU flags to boot at all.** `vm_launch.conf` asks for
  `-enable-whpx`, but the emulator wrapper drops it while assembling the command
  line — `check-hax.exe` fails here because Hyper-V/WSL2 owns VT-x, and HAXM
  seems to be the only accelerator the wrapper accepts. The VM then runs under
  software emulation with a plain `qemu64` CPU, and the Tizen 10.0 guest's glibc
  hits an instruction that CPU lacks, panicking the moment init starts:
  `traps: init[1] trap invalid opcode … in ld-linux-x86-64.so.2` →
  `Kernel panic - not syncing: Attempted to kill init!`. Launching the same conf
  with `-q -enable-whpx -cpu Skylake-Client` appended boots it properly. Both
  flags were added together, so either alone might suffice.
* This 10.0 emulator reports **`intershell_support:disabled`** — same lockdown as
  a retail TV, so there is no `sdb shell`, no `dlogutil` and no `app_launcher`.
  Install over `sdb`, but launch from the emulator's own Apps list, and read the
  on-screen diagnostics (key `3`). This is the case the overlay was built for.
* **Disable Fast Boot** (or cold-boot). It restores a snapshot on restart and
  silently reverts your install.
* Unsigned packages are rejected with `[118, -12]`; an `api-version` above the
  platform gives `[118, -4]`.
* The emulator has a real mouse and real arrow keys, so hover and click can be
  cross-checked against the injected cursor.
* There is a report of an app-created `Tizen.WebView` **crashing on the TV
  emulator** in a .NET project with nothing in the log. Startup is written to
  survive that: `Chromium.Initialize()` and the view creation are guarded, and a
  failure renders the reason on screen (key `3` for the log) instead of
  disappearing. If it fails on the emulator, that is not evidence it fails on a
  real TV — the emulator is the weaker signal here.
* `Chromium.Shutdown()` is deliberately never called: it is reported to hang the
  app on exit (TizenFX issue 3274).

## Retail Tizen 5.0 needs a partner certificate

On a 2019 retail set (UE55RU7020) the web engine is present but unreachable to an
ordinary app. `Chromium.Initialize()` fails, and `dlopen` explains why:

```
/usr/lib/libchromium-ewk.so : libmarlin.so.0: cannot open shared object file: Operation not permitted
```

The engine links Marlin DRM (the firmware carries `libcapi-drm-marlin`,
`playready`, `widevinecdm`, `verimatrix`, `libappdrm`), and an author-signed app
may not open it — EPERM on a file that exists. Signing **partner-level** with
`developer.samsung.com/privilege/drmplay` + `drminfo` declared lifts it: `dlopen`
succeeds, `Chromium.Initialize()` returns refcount=1, and a page loads.

Two consequences baked into the code:

* `NativeEngine.Preload()` dlopens `/usr/lib/libchromium-ewk.so` with
  `RTLD_GLOBAL` before `Chromium.Initialize()`. Plain P/Invoke-by-name never
  resolves it on 5.0 — the loader mangles the name to `liblibchromium-ewk.so.so`.
* The DRM privileges are declared **only** in `Overscan5`. Adding a
  partner privilege to the other manifests would make non-partner-signed installs
  (emulator, DUID) fail. **The tizen5 package must be partner-signed.**

## Status

**Working on the Tizen 10.0 TV emulator (NUI build):** DuckDuckGo renders in its
desktop layout with the status bar and the injected D-pad cursor on screen. So on
9.0+ the premise holds — UA override, JavaScript and the page-drawn cursor all
function against a real page.

Not yet verified on real hardware (5.0 RU7020, 8.0 tester set), which is where the
`Tizen.WebView` builds matter.

Two silent-failure traps are worth remembering, since both look identical to "the
icon does nothing":

1. `RollForward=LatestMajor` is mandatory. Without it `runtimeconfig.json` pins
   `Microsoft.NETCore.App 6.0.0` with minor-only roll-forward, so the app starts
   only on Tizen 8.0; on 9.0/10.0 the launcher finds no framework and the process
   dies before drawing anything.
2. A `try/catch` *inside* a method that references a missing assembly never fires
   — the load failure is raised while the CLR prepares that method, so the call
   site has to be guarded too. This is what hid the missing `Tizen.WebView`.

The first thing a device test has to answer is whether an app-created
`Tizen.WebView` renders at all on a retail TV: the engine is definitely present
(the built-in browser uses it), but a third-party app instantiating it is
unproven, and the TV profile is not what these ElmSharp/ewk bindings are
primarily tested against. The diagnostics overlay (key `3`) reports the engine UA,
the forced UA, and what `navigator.userAgent` / viewport the page actually sees,
because a retail TV with intershell disabled gives no `dlogutil`.

If the web view comes up, the next unknowns in order are: whether the Evas cursor
overlay composites above it, whether `KeyGrabEx` gets us the Back/Channel keys,
and whether the address-bar `Entry` raises the TV's on-screen keyboard (if not,
we ship our own key grid).

