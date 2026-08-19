# Internals

Everything that would clutter the README: why the project is shaped the way it is,
what the platform does that you would not expect, and how to debug a TV that
cannot be logged.

## Source layout

```
src/common/     engine-agnostic: user agents, injected page script, diagnostics, URL helpers
src/elm/        ElmSharp + Tizen.WebView (ewk) UI  — TV 5.0-8.0
src/nui/        NUI + NUI WebView UI               — TV 9.0+
src/probe/      diagnostic ladder harness (its own package)

Overscan4/      tizen40,         manifest api-version 4.0, ewk   (oldest possible)
Overscan/       net6.0-tizen8.0, manifest api-version 8.0, ewk
Overscan5/      tizen50,         manifest api-version 5.0, ewk   (classic Tizen.NET.Sdk)
Overscan6/      tizen60,         manifest api-version 6.0, ewk   (2021-2023 sets)
OverscanNui/    net6.0-tizen8.0, manifest api-version 9.0, NUI WebView
OverscanProbe/  tizen50,         diagnostics
```

`src/common` and `src/elm` are compiled into every ewk package, so they must stay
inside the TizenFX **API 4** surface — the floor moved down when `Overscan4` was
added. Off limits: `ScrollPosition`, `ScrollBy`, `Scale`/`SetScale`,
`LoadProgress`, `EvalAsync` and `Settings.ScriptsCanOpenWindows` (API 6+), and
anything else introduced after API 4. Check a member against the API4 branch of
TizenFX before using it.

## Why one package per platform range

A TV installs a package only when its manifest `api-version` is **at or below** the
TV's platform version; above it the install is refused with `[118, -4]`. So a build
declaring 8.0 cannot serve a 2021-2023 set, however compatible the code is — hence
separate 4.0, 5.0, 6.0 and 8.0 manifests rather than one package with the newest
api-version.

The runtime is the other half of it: 6.0/7.0 sets predate .NET 6, so those targets
have to be classic `tizenXX` builds on the .NET Core 3.1 SDK. Only 8.0+ can take
`net6.0-tizen8.0`.

## The floor: Tizen 4.0

Samsung added .NET to TVs with the **2018** range (Tizen 4.0), so 2017 and earlier
sets cannot run a `.tpk` at all — there is no runtime for it to start in. TizenFX
agrees from the other direction: its branches begin at **API 4**. `Overscan4`
therefore targets the oldest platform that can exist, and the only thing a 2017 set
could ever have is a `.wgt` UA-overriding launcher without its own chrome (see
issue #11).

## Two web-view bindings, neither covering the whole range

Nearly all documentation points at `Tizen.NUI.BaseComponents.WebView`. It is
`since_tizen 9`: in TizenFX API 8 and earlier it sits under
`src/Tizen.NUI/src/internal/`, so an app cannot call it. `Tizen.WebView` (ElmSharp
+ chromium-ewk) has been public since API 4.

The trade-off inverts at the top: `Tizen.WebView` is deprecated at API 10 and
**gone by API 12**. Tizen 10.0 already ships no `Tizen.WebView.dll` at all — the
ewk build dies there with `FileNotFoundException: Could not load file or assembly
'Tizen.WebView, Version=4.0.0.0'`.

So: 5.0–8.0 is ewk, 9.0+ is NUI, and there is no single binding for both.

## Engine versions

The web view is bound to the firmware's own chromium-efl — the same engine the
built-in browser uses. Samsung's published mapping, plus one measurement:

| TV platform | Chromium |
| --- | --- |
| 3.0 | M47 — no .NET runtime, cannot run Overscan |
| 4.0 | M56 |
| 5.0 | M63 |
| 5.5 | M69 |
| 6.0 | M76 |
| 6.5 | M85 |
| 7.0 | M94 |
| 8.0 | M108 |
| 10.0 | **M130** (measured on the emulator) |

### The user-agent shapes

Three different forms, and getting this wrong is not harmless — claiming a modern
Chrome to a site while running M63 invites JavaScript the engine cannot parse.
`UserAgents.ChromeVersionOf` handles all three:

```
Chrome/108.0.5359.1                                   desktop + older TVs
(KHTML, like Gecko) 130.0.6723.116/10.0 TV Safari…    Tizen 10: no Chrome/ token
(KHTML, like Gecko) Version/5.0 TV Safari/537.36      Tizen 5.0: no version at all
```

For the third form the milestone is derived from the `Tizen x.y` token via the
table above, taking the highest entry at or below the platform.

## Retail Tizen 5.0: the DRM permission wall

On a 2019 retail set (UE55RU7020) the engine is present but unreachable to an
author-signed app. `Chromium.Initialize()` throws `DllNotFoundException`, and
`dlopen` explains why:

```
/usr/lib/libchromium-ewk.so : libmarlin.so.0: cannot open shared object file: Operation not permitted
```

`EPERM` on a file that exists — the firmware carries the whole DRM stack
(`libcapi-drm-marlin`, `playready`, `widevinecdm`, `verimatrix`, `libappdrm`), the
web engine links Marlin, and an ordinary app may not open it. Non-existent paths in
the same scan returned `ENOENT`, so the loader is distinguishing the two.

**Signing partner-level with the DRM privileges declared lifts it.** With
`developer.samsung.com/privilege/drmplay` (public) and `drminfo` (partner) in the
manifest and a partner certificate: `dlopen` succeeds, `Chromium.Initialize()`
returns refcount=1, and pages load.

Two consequences in the code:

- **`NativeEngine.Preload()`** dlopens `/usr/lib/libchromium-ewk.so` with
  `RTLD_GLOBAL` *before* `Chromium.Initialize()`. Plain P/Invoke-by-name never
  resolves it on 5.0 — the loader mangles the name to `liblibchromium-ewk.so.so`
  and gives up. Because the loader matches already-loaded sonames first, the
  preload makes the later P/Invoke bind.
- **The DRM privileges live only in `Overscan5`'s manifest.** A partner privilege
  in the other manifests would make their non-partner installs (emulator, DUID)
  fail outright.

NUI would not rescue 5.0 either: `libdali-web-engine-chromium-plugin.so` is in the
same directory and depends on the same chain.

## Two silent failures that look identical

Both present as "the icon does nothing" — no window, no error, no trace:

1. **Missing `RollForward`.** Without `<RollForward>LatestMajor</RollForward>`,
   `runtimeconfig.json` pins `Microsoft.NETCore.App 6.0.0` with minor-only
   roll-forward, so the app starts only where the platform runtime is 6.0.x — i.e.
   Tizen 8.0. On 9.0/10.0 the launcher finds no framework and the process dies
   before drawing anything.
2. **A `try/catch` inside the method that references a missing assembly never
   fires.** The load failure is raised while the CLR *prepares* that method, so it
   escapes past the handler inside it. The call site has to be guarded too — this
   is what hid the missing `Tizen.WebView` on Tizen 10.

## Debugging a TV that cannot be logged

Retail sets (and the Tizen 10 emulator) report `intershell_support:disabled`:
`sdb shell` returns "closed", `dlog` hangs and returns nothing, and `sdb pull`
answers `You cannot pull files from this path` for crash directories. So
observability is built into the app:

- **On-screen overlay** (key `3`) — engine UA, forced UA, what the page reports,
  view geometry, and a log tail.
- **`DiagServer`** — plain HTTP on `:8081`, started as the first statement in
  `Main`, before anything that can fail. Read it over the LAN
  (`http://<tv-ip>:8081`) or via `sdb forward tcp:8081 tcp:8081`. On a fatal error
  `Main` sleeps forever so the report stays readable.
  *The report may only read cached strings* — touching a live DALi or EFL object
  from the server thread hangs the request.
- **`Breadcrumbs`** — append-only file, one open/append/close per line, so a native
  crash that kills the process before the socket binds still identifies itself: the
  next launch reports where the last one died.
- **`OverscanProbe`** — a separate package that walks startup one call at a time
  (`Elementary.Initialize` → window → widgets → `Chromium.Initialize` → web view →
  UA → `LoadUrl`), plus filesystem and `dlopen` reconnaissance. This is what found
  the DRM wall.

## The D-pad pointer

chromium-efl exposes no way for an app to inject synthetic mouse input (the NUI
binding has only `FeedMouseWheel`), so the pointer lives inside the page.
`PageScript` injects an overlay element and `move`/`click`/`scroll`/`type` helpers;
the native side sends viewport **fractions**, so it never needs to know the page's
CSS size, zoom or DPR.

Details that matter:

- Clicks dispatch `mousedown`/`mouseup`/`click` on `elementFromPoint`. A dispatched
  click still runs an element's default action, so links and buttons work — no
  `isTrusted` problem in practice.
- `elementFromPoint` returns the *topmost* node, which on icon buttons is a
  decorative `<svg>`. Clicks climb to the nearest interactive ancestor
  (`closest('a,button,[role=button],…')`), otherwise icon toolbars are dead.
- Scrolling walks to the innermost scrollable ancestor before falling back to the
  document, so SPAs that scroll a `div` respond.
- Text fields are **never focused**: focusing one makes the TV raise its IME, which
  swallows the remote. A clicked field is outlined and remembered instead, and the
  on-screen grid writes into it.
- Typing sets values **through the native value setter**
  (`Object.getOwnPropertyDescriptor(proto,'value').set`) and dispatches
  `input`/`change`, otherwise React-style frameworks ignore it. Enter is sent as
  real key events, falling back to `form.requestSubmit()`.

### Why our own keyboard rather than the platform IME

An ElmSharp `Entry` takes focus at startup on a real TV, the platform IME appears
over the page, and it eats every remote key — the pointer could not be moved at
all. A grid we draw and drive ourselves never involves the IME, works identically
on every set, and can type into *page* fields, which the IME cannot.

Keys we act on set `EvasEventFlag.OnHold` (EFL's "consumed" marker), otherwise the
page scrolls behind the keyboard while the D-pad moves between letters.

### Keyboard layouts

`KeyboardLayouts` holds the four grids — QWERTY, AZERTY, QWERTZ, ABCDEF — and the
remembered choice (`keyboardLayout` in `settings.tsv`), shared by both keyboards so
the ElmSharp and NUI builds behave the same. The `layout` key at the end of the
action row cycles them and wears the current layout's name.

All four grids are deliberately the same shape (rows of 10, 10, 10, 10, 11): the
keyboards build their cells once in the constructor and only swap the labels when
the layout changes, so a grid with different row lengths would leave cells pointing
at keys that are no longer there. Whatever letters a layout leaves over, the row is
padded out with `-`, `_`, `?`, `=` so every key exists in every layout.

## Emulator notes

- **The TV emulator needs a modern `-cpu` model or it panics.** With the default
  `qemu64` the guest's glibc hits an instruction the CPU lacks and the kernel
  panics the moment init starts (`Attempted to kill init! exitcode=0x00000004`,
  i.e. SIGILL; filesystems mount cleanly first, so the image is fine). Launch the
  VM's `vm_launch.conf` directly with `-q -cpu Skylake-Client` appended — that
  alone fixed both a 10.0 and a 5.5 image.
- **HAXM cannot work alongside Hyper-V/WSL2.** The 10.0 emulator's wrapper drops
  the `-enable-whpx` its own config asks for, and the 5.5 build has no WHPX support
  at all and exits rather than falling back. Passing `-enable-whpx` explicitly
  works on 10.0; 5.5 has to run under software emulation (slow: ~5 minutes to boot).
- `[118, -12]` on install means the signature; `[118, -4]` means the manifest's
  `api-version` is above the platform.
- **Disable Fast Boot**, or a restart restores a snapshot and silently reverts your
  install.
- No separate x86 package is needed: the payload is pure managed IL, so one package
  runs on x86 and ARM.
- `Chromium.Shutdown()` is deliberately never called — it is reported to hang the
  app on exit (TizenFX issue 3274).

## Open problem: stretched rendering on 5.0

The page reports `innerWidth` 1962–2389 and `innerHeight` ~561 at DPR 1 while the
view is 1920×1012 — so ewk lays out at a size of its own choosing and paints it
into the view, non-uniformly (~0.98 across, ~1.8 down). It is visible as vertically
stretched text.

On 5.0 there is no API lever: `WebView.SetScale` and the zoom settings are API 6+,
and API 5's `Settings` exposes only JavaScript, image loading, encoding and font
size. Key `6` toggles the only available approach — injecting
`<meta name="viewport" content="width=<view px>, initial-scale=1">` — and the
diagnostics print `view geom` (what Evas gave the view) next to `page metr`
(`inner`/`client`/`outer`/`screen`/`dpr`) so the two can be compared.
