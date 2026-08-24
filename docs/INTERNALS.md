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

## `ewk_init()` returning 0 is a failure, not a number

`Chromium.Initialize()` is `ewk_init()`, which returns the engine's **reference
count**: 1 after a successful first call, and **0** from every one of its own
error paths. The code used to log the number and carry on.

That is the whole of issues #13 and #14. On a Q80 (5.5) and on a 2018 set the
report read

```
Chromium initialized, refcount=0
WebView created
```

and then nothing — the icon disappeared about 15 seconds later. A `WebView` built
on an engine that never came up wedges start-up, `OnCreate` never returns, and the
launcher kills the app: an app that installs, launches and vanishes with no error
anywhere. On the RU7020, where it works, the same line reads `refcount=1`.

So a zero now fails the start-up deliberately and draws the failure screen. Before
giving up it retries once with `ewk_set_arguments` called first — TizenFX has no
binding for it, so it is `dlsym`'d off the handle `NativeEngine.Preload` already
holds. Two reasons that is worth a try rather than superstition: every ewk sample
sets the argument vector before `ewk_init`, and for a .NET app `argv[0]` is the
shared `dotnet-launcher` rather than anything app-shaped. The engine leaves its
count at zero after a failed init, so calling `ewk_init` again genuinely re-runs
it instead of just incrementing.

**What is not the cause:** the DRM wall above. `dlopen` of
`libchromium-ewk.so` succeeds with `RTLD_NOW` on both reporting sets, which means
`libmarlin.so.0` resolved — the partner certificate did its job. The engine is
present, permitted, and still refuses to start in an app process on that firmware.

### There are exactly nine things it can be

The retry was not enough — issue #17 (Q80T, 5.5) came back with two zeroes and no
reason. But `ewk_init` is short, and reading it
([`ewk_main.cc`](https://git.tizen.org/cgit/platform/framework/web/chromium-efl/plain/tizen_src/ewk/efl_integration/public/ewk_main.cc?h=tizen))
bounds the problem completely. Every `return 0` is a `goto` out of one of nine
EFL library inits, in this order:

```
eina_init  →  eina_log_domain_register("ewebview-blink")  →  evas_init  →
ecore_init  →  ecore_evas_init  →  ecore_imf_init  →
ecore_wl2_init (or ecore_wl_init / ecore_x_init)  →  edje_init  →  eldbus_init
```

Nothing chromium-specific runs before the return: `_ewk_init_web_engine()` is an
empty function. So "ewk_init returned 0" *always* means one of those nine said no,
and two additions turn it into a name:

- **`EflSubsystems`** walks the same nine, in the same order, through
  `dlopen`/`dlsym`, just before `Chromium.Initialize()`. This is safe and cannot
  mask the fault: EFL inits are reference-counted, so one that is already up (and
  `elm_init` brought most of them up long ago) returns its incremented count, while
  one whose underlying init genuinely fails returns 0 for us for the same reason it
  is about to return 0 for the engine. The matching shutdowns are deliberately not
  called — an extra reference is inert, and a shutdown ladder is one more thing
  that could kill the process before the report is readable.
- **`NativeStdErr`** points fds 1 and 2 at a file for the duration of the call and
  reads it back. Each of those nine failures logs its reason first —
  `ERR("could not init ecore_imf.")` and friends — through EINA_LOG, which writes
  to stderr, which on a TV goes to a dlog nobody can read. A file rather than a
  pipe: a pipe's 64 KiB buffer would deadlock the process if a *successful*
  chromium start-up ever out-logged it, and a file cannot block.

Both land in the failure screen and in `:8081`, so the next report on an affected
set says which library refused instead of leaving a bare `refcount=0` to interpret.

### It was none of the nine — it is the implementation library

That build came back from the Q80 with all nine up:

```
efl subsys : all up (9 checked)
  eina_init               : ok (refcount 15)
  eina_log_domain_register: ok (domain 66)
  evas_init               : ok (refcount 3)
  ecore_init              : ok (refcount 18)
  ecore_evas_init         : ok (refcount 2)
  ecore_imf_init          : ok (refcount 2)
  ecore_wl2_init          : ok (refcount 3)
  edje_init               : ok (refcount 2)
  eldbus_init             : ok (refcount 1)
```

…and `ewk_init` still returned 0, twice. So the ladder above is not the whole of
it. The captured output is what named the real step:

```
engine said: -rw-r--r-- 1 root root 48767918 2026-03-31 16:47
             /usr/share/chromium-efl/lib/libchromium-impl.so
```

**Nothing in this repository prints that line.** It is a busybox `ls -l`, and it
arrived in our capture file because `NativeStdErr` redirects fds 1 and 2 and a
`system()` child inherits them. The engine ran it.

`libchromium-ewk.so` is a shim; the 48 MB `libchromium-impl.so` beside it is
chromium. The shim `dlopen`s the implementation, and on the failure path it lists
the file — to record that the library it could not load is nevertheless present.
The `dlerror()` next to that `ls` goes through chromium's logging, i.e. dlog, i.e.
nowhere we can read it. That single stray line is the whole diagnosis: the nine EFL
inits pass, the implementation `dlopen` fails, `ewk_init` returns 0.

**`ChromiumImpl`** does that `dlopen` itself, from the same point in start-up,
where `dlerror()` is a string we can put on a screen. It is also the most plausible
fix available from the app side:

- The likeliest reason a library that exists will not load is a **dependency the
  loader cannot find**. The built-in browser is launched with the engine's own
  directory on `LD_LIBRARY_PATH`; a `dotnet-launcher` process is not. Setting the
  variable now would achieve nothing — the loader reads it once, at process start —
  but an absolute-path `dlopen` ignores the search path entirely.
- So `Preload()` reads the missing soname back out of `dlerror()`
  (`libfoo.so.1: cannot open shared object file: No such file or directory`), finds
  that file under `/usr/share/chromium-efl/lib` and the usual library directories,
  opens it `RTLD_GLOBAL`, and retries — one dependency per round. Once a library is
  in the process under its soname, the shim's own `dlopen` a moment later matches it
  and succeeds.
- A message ending in **`Operation not permitted`** instead of `No such file` is not
  chased: that is the SMACK wall from issue #13, and no amount of searching moves
  it. It is reported as the answer.

`Explain()` gathers the rest — `LD_LIBRARY_PATH`, whether the file is readable by
this process at all (`ls` needs only the directory; `dlopen` needs the file, so
"lists but will not open" is a SMACK label we may not touch), a directory listing,
and an `RTLD_LAZY` retry. That last one runs **only after both `ewk_init` attempts
have already failed**: a library that loads lazily and is missing a symbol faults
when the symbol is first called, so leaving one in the process ahead of a *working*
init would trade a clean refusal for a crash. After the failure it costs nothing,
and it separates "unresolved symbol" (fails `RTLD_NOW`, loads `RTLD_LAZY`) from
"the file will not open".

If the implementation loads for us and `ewk_init` still returns 0, that eliminates
the theory outright — which is worth as much as confirming it.

### `ELM_ACCEL` has to be set before the window exists

`libchromium-ewk.so` has a library constructor whose entire body is
`setenv("ELM_ACCEL", "hw", 1)`, with the engine's own comment saying it must
happen *"before creating elm_window"* because the port has no software rendering
path. `NativeEngine.Preload()` — the `dlopen` that runs that constructor — used to
sit inside `TryStartEngine()`, i.e. after `BuildUi()` had already created the
window. It now runs as the first native call in `Main`, before
`Elementary.Initialize()`. `Preload` is idempotent, so the call in `TryStartEngine`
still stands for the paths that reach it first.

ElmSharp's own `Window.CreateHandle` calls `elm_config_accel_preference_set("3d")`
before `elm_win_add`, which is why this probably was not the cause on its own —
but the ordering the engine asks for costs nothing to honour.

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

  **Tell reporters to type `http://` explicitly.** The server answers in cleartext
  and has no TLS at all, so a browser that upgrades a bare `<tv-ip>:8081` to
  `https://` — iOS Safari and Chrome do this by default — gets a plaintext reply to
  a TLS handshake and shows `ERR_SSL_PROTOCOL_ERROR`. That looks like a TV fault
  and is not one; it is also a different failure from `ERR_CONNECTION_FAILED`,
  which really does mean nothing is listening yet. The failure screen now spells
  out "http, not https" for this reason.
- **`Breadcrumbs`** — append-only file, one open/append/close per line, so a native
  crash that kills the process before the socket binds still identifies itself: the
  next launch reports where the last one died. The **browser** drops these too now,
  not only the probe: #13 and #14 both produced a log that stopped at `WebView
  created` with five uninstrumented calls after it, so no report could say which one
  took the app down. Every step of start-up has its own line, and the report prints
  the previous run's trail above this one's.

  Two things the report itself got wrong, found by those issues:

  - It read `_cursor.Visual` with no null check, so calling it during start-up threw
    a `NullReferenceException` and replaced the evidence with a stack trace. The
    provider runs on the DiagServer thread while the main thread may be anywhere in
    `OnCreate`, so **every field it touches must be null-checked**.
  - `_web == null` was rendered as `engine: FAILED TO START`, which is also what a
    view that simply has not been built yet looks like from that thread. It now says
    *still starting* unless a failure was actually recorded.

  The log's ring buffer was 12 lines, which an instrumented start-up overflows — the
  beginning of a failed launch is the interesting part, so it holds 60 now and the
  on-screen overlay asks for the tail that fits its box.
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

### The one thing a script cannot click: another origin's frame

`elementFromPoint` stops at an `<iframe>`, and a click dispatched on the frame
element does nothing inside it. So a captcha, an embedded sign-in or a payment
widget is simply dead to the injected pointer — issue #15, on Instagram's captcha.

Two halves to it:

- **Same-origin frames are entered from the script.** `descend()` hit-tests inside
  `contentDocument` with coordinates translated by the frame's bounding box, and
  loops, because a widget is often a frame inside a frame. `fire()` therefore takes
  explicit coordinates: an event dispatched inside a frame has to report the
  position in *that* frame's space.
- **A cross-origin frame is unreachable from any script we can run**, so the click
  has to be a real one. EFL does expose an input path an app may drive:
  `evas_event_feed_mouse_move`/`_down`/`_up` push an event through the canvas's own
  hit-testing, the ewk view receives it exactly as it receives the TV's pointer, and
  chromium routes it into whichever frame is under the point. The events are
  trusted, so a captcha accepts them. `EvasObject.Handle` is public in API 4, so
  `evas_object_evas_get` on it gives the canvas; `NativeMouse` does the rest, and
  every part of it is best-effort.

The script reports `FRAME:<tag>` back over the bridge and the native side follows up
with the real click, rather than the native path being used for everything. That is
deliberate: **a real click on a text field focuses it, and a focused field raises
the TV's IME**, which then swallows the remote — the exact failure the in-page
pointer exists to avoid. NUI has no canvas to feed, so on 9.0+ those frames stay
unclickable and the app says so.

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

Three things change what is showing: the layout, `shift` and `sym`. Only the layout
is remembered — coming back to a keyboard stuck on punctuation would be baffling.
Shift releases itself after one letter, as on a phone: one capital is what a name
or a password rule usually wants, and leaving it latched turns the rest of the word
into shouting.

Every grid is deliberately the same shape (rows of 10, 10, 10, 10, 14): the
keyboards build their cells once in the constructor and only swap the labels when
the grid changes, so a grid with different row lengths would leave cells pointing
at keys that are no longer there. That is also why the shifted variants and the
symbol page are built to the same rows-of-ten shape rather than being packed
tighter. Whatever letters a layout leaves over, the row is padded out with `-`,
`_`, `?`, `=` so every key exists in every layout.

The action row grew from 11 keys to 14 (`@`, `shift`, `sym`, `start`), which is why
a key is 104px wide rather than 116 — fourteen of the old ones overflow a 1080p
panel. Rows are centred rather than left-aligned, or a 10-key letter row leaves a
ragged hole beside a 14-key action row.

`@` sits on the action row and not on the symbol page: signing in to anything needs
it constantly, and having to find a second page for it was the substance of
issue #15.

### Where the browser opens

`startupUrl` in `settings.tsv`, set by the keyboard's `start` key: type an address,
press `start` instead of `GO`, and that is what the app loads at launch. Pressing
`start` with an empty entry clears it and the generated start screen comes back.

It lives on the keyboard rather than on a remote key because every digit was
already taken, and because the thing being saved is exactly what you have just
typed. `Store.Init` therefore has to run **before** the keyboard is constructed —
`KeyboardLayouts` resolves its remembered layout the first time it is touched, and
both builds used to initialise the store afterwards, silently discarding the user's
layout choice.

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
