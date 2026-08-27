# Internals

Everything that would clutter the README: why the project is shaped the way it is,
what the platform does that you would not expect, and how to debug a TV that
cannot be logged.

## Source layout

```
src/common/     engine-agnostic: user agents, injected page script, diagnostics, URL helpers,
                remote key names and the on-screen menu's model
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

### "Operation not permitted" is three different faults

On the Q80 the implementation *was* found, and refused:

```
engine implementation: REFUSED — libprivileged-service-client.so:
  cannot open shared object file: Operation not permitted
```

That set has already cleared the Marlin wall — `libchromium-ewk.so` preloads, which
it cannot do without the DRM privileges in force — so reading this second EPERM as
"another privilege is missing" is a guess, and an expensive one: there is no
published list of Samsung TV partner privileges to guess from.
`libprivileged-service-client.so` appears in no documentation, no package and no
readable source tree, and a privilege the certificate does not cover makes the
install fail outright, which would break the sets that currently work.

That absence is checked, not assumed. In a stock Tizen 5.0 armv7 rootfs the engine
is a single 35 MB `/usr/lib/libchromium-ewk.so` with no `libchromium-impl.so`
beside it, nothing named `*privileged-service*` anywhere in the tree, and no
`libmarlin` either. Both the implementation split and that dependency are Samsung
retail-firmware additions, which is also why 5.0 sets clear a wall that 5.5 sets do
not.

`dlopen` says the same word for three unrelated faults, and only one of them is a
privilege:

| what actually failed | reads as | fixable by a manifest |
| --- | --- | --- |
| `open()` denied by Smack or by the file's group | **EACCES** on open | **yes** — this is the Marlin shape |
| `open()` denied by the firmware's own gate | **EPERM** on open | no — see below |
| `mmap(PROT_EXEC)` denied — `noexec` mount or an exec-label rule | opens, will not map | no |
| the loader looked somewhere else | opens and maps fine here | no — wrong path, not permission |

Those first two rows were one row until 2026-08-27, reading "EPERM/EACCES" and
"yes, this is the Marlin shape" for both. They are not the same refusal and the
difference is the entire question — see below.

`SmackWall` asks the set which one it is instead of theorising: it locates the
soname, prints our own label from `/proc/self/attr/current` and `CapEff` from
`/proc/self/status`, names the mount the file sits on with its options — where a
`noexec` would be written down — and then, **in this order**, `open()`s the file
for the raw errno, `read()`s twenty bytes to confirm an ELF and name its
`e_machine`, `mmap()`s it `PROT_READ` and then `PROT_READ|PROT_EXEC` to separate
reading from executing, and only afterwards reads `security.SMACK64`,
`SMACK64EXEC`, `SMACK64MMAP` and `SMACK64TRANSMUTE` off it.

The order is not incidental. On the Q80 the process died inside `getxattr` — the
trail ends on that line — and the labels were sequenced *before* the verdict, so it
took the answer with it. Reading and mapping the file is the answer; the labels only
ever explained why. Each label is now traced on its own line too, so a repeat kill
names which of the four did it.

`SmackWall.OwnCode` asks one more thing, before any of the above and where nothing
can eat it: whether this app can `mmap` its own installed assembly `PROT_EXEC`. See
"What is left" below for what turns on that. It then repeats the label line
for `libchromium-ewk.so`, `libchromium-impl.so` and `libmarlin.so.0`, so the report
carries libraries this process *can* open beside the one it cannot.

Both loaders feed it: `ChromiumImpl.Preload` on the implementation's dependencies
and `NativeEngine.Preload` on the engine's own, via `SmackWall.BlockedSoname`,
which is the permission-half counterpart of `ChromiumImpl.MissingSoname`'s
No-such-file half. The verdict lands in the `permission :` header line and the
lines land in the breadcrumb trail, so it survives the set being power-cycled.

### A probe that ran too early, and reported too late

The first version of that probe cost two users a build each, and the way it failed
is worth keeping written down.

It ran inside `ChromiumImpl.Preload`, i.e. *before* `ewk_init` — and on both
reporting sets (#17 on the Q80, #13 on a 2018 set) the process did not come back
out of it. Every trail ended on the same line:

```
19:39:40    eldbus_init             : ok (refcount 1)
```

which is the last line before the probe and two lines before the engine would have
been asked to start. So a diagnostic meant to explain a failure had replaced it with
an earlier one, and the reports were worse than the reports it was written to
improve: the app no longer even reached the thing being diagnosed.

It was also unreadable when it *did* run. `Investigate` collected its findings in a
list and handed them back to the caller, which dropped them into the trail
afterwards — so a call that never returns takes every finding with it. The one
report that got furthest showed exactly three lines and stopped, which says
"somewhere in here" and nothing more.

Two rules came out of it, and they apply to every probe in this app, not just this
one:

- **Nothing diagnostic runs before the thing it diagnoses.** `SmackWall` is started
  from `SmackWall.InvestigateInBackground`, on a background thread, after both
  `ewk_init` attempts have returned 0 and the failure screen is up. A hang costs a
  thread nobody is waiting on; a crash costs a process with nothing left to do.
- **Every line is flushed as it is produced.** `SmackWall.Add` and
  `ChromiumImpl.Note` write through `Breadcrumbs.Drop` on the spot, and each native
  call is announced by `SmackWall.Trace` *before* it is made (`probe: open …`,
  `probe: mmap PROT_READ|PROT_EXEC`, …). If the next launch's trail ends on one of
  those, that call is the one this firmware does not survive — which is the question
  the batched version could not answer.

The riskiest of those calls is the last: asking a kernel with an exec-label policy
to `mmap(PROT_EXEC)` a file it is withholding is precisely what such a policy exists
to refuse, and refusing it by signal rather than by errno is a legal way to do that.
It is traced immediately before, so the trail names it outright either way.

The first of those two rules held up. It also turned out to be unsatisfiable in the
browser on the set it was written for — see the next section.

### `ewk_init` does not return 0 — it does not return, *while we are holding it wrong*

`build-f75dd96` restored the pre-probe depth on the Q80: the trail reached the
engine again. What it also showed is that the whole failure model above was wrong
about one word. Every launch on that set ends on the same line:

```
08:42:38    engine implementation: REFUSED — libprivileged-service-client.so: …
08:42:38    Chromium.Initialize()
```

and nothing after it. `Breadcrumbs.Drop` writes each line with its own
open/append/close precisely so that this is readable, so the last line is where
the process stopped: inside `ewk_init`. It is not returning 0 twice. It is not
returning.

That invalidates two things at once. The retry never happens, the failure screen
never appears — and `SmackWall.InvestigateInBackground`, which the previous section
carefully sequenced *after* both attempts, is unreachable on the one set it was
written for. `permission : (nothing was refused)` in that report does not mean
nothing was refused; it means nothing was ever asked.

Three changes follow from it.

**The engine's own output survives the crash now.** `NativeStdErr` was already
pointing fds 1 and 2 at `<data>/stderr.log` before the call — but it only reads that
file back *after* the call returns, so a call that never returns hands back
`(not captured)` and leaves its output on disk. Where the next launch truncated it,
`FileMode.Create`. Four builds' worth of EINA_LOG lines were written and thrown
away. `Breadcrumbs.Init` now rolls that file aside to `stderr.prev.log` in the same
breath as the trail — the only moment between the two runs — and reads it into
`Breadcrumbs.PreviousStdErr`, which the report prints as *engine stdout/stderr
(previous run)*. It also goes on `DiagServer`'s pre-UI page, which is what a quick
reporter gets and which used to carry three lines and nothing else.

**`Heartbeat` times the call that does not come back.** "Did not return" still
covers two failures needing opposite fixes: a crash inside the engine, or the
launchpad killing us because `ewk_init` is being called from inside the create
callback and Tizen will not wait on that callback forever. A tick a second tells
them apart — a crash leaves no ticks, a watchdog leaves a run of them ending on a
round number. The ticks are trail-only via `Breadcrumbs.DropToTrail`: `DiagLog`
keeps 60 lines, and at one a second the on-screen log would lose exactly the
start-up lines worth reading. The elapsed time is measured in `Stop`, on the calling
side, so a call that returns in 5 ms is not reported as the second the ticking
thread takes to notice.

**And a fourth, added later, which may undo the heading above.** Reading the whole
of #17 back turns up a confound in it. `ChromiumImpl.Preload` — the app's own
`dlopen` of the 48 MB implementation — is the *only* change to the ewk start-up path
between `build-caef7bd`, whose `ewk_init` returned `refcount=0` twice and lived to
draw its failure screen, and every build since, whose trail ends on
`Chromium.Initialize()` with the process gone. One set and one variable is not
proof. It is also the third time this project has put a fragile probe in front of
the thing it was explaining. So the `dlopen` moved to between the two init
attempts: same job — the implementation is in the process before the engine looks
for it — while the first attempt gets an untouched process again. If `refcount=`
comes back on that set, the heading above was about our diagnostic and not about
the TV.

**In the probe, and only in the probe, the permission wall goes first.** The rule
above still holds for the browser: #13 and #17 each lost a build to a probe that
died before the engine was asked to start. But `OverscanProbe` is a separate
package with its own id, built by CI into `Overscan-probe.tpk`, and its whole
purpose is to be expendable — `Step` announces each call before making it, so a
process that dies names its own cause on the next launch. Sequencing anything after
`ewk_init` there is sequencing it after the point of no return. So `4f permission
wall` runs `SmackWall.Investigate` synchronously between the implementation dlopen
and `Chromium.Initialize`, and its verdict is in the trail before the engine gets a
chance to take the process with it.

### Above platform level: what actually refuses that `open()`

The Q80's report on 2026-08-27 answered both instrumented questions. `ewk_init`
returns now (60 ms, and 37 on the retry) rather than swallowing the process, so
moving our 48 MB `dlopen` out from in front of it worked. It still returns 0, the
implementation still will not load, and the verdict line reads

```
open(O_RDONLY): EPERM (operation not permitted)
```

on a file that `stat`s fine at 84484 bytes, from a process labelled
`User::Pkg::org.apps2samsung.overscan` with `CapEff` all zero, on a read-only vdfs
root. Reading that as "the Marlin shape, so the fix is in the manifest" is what
this app believed until now, and it is wrong twice over.

**Smack says EACCES.** Every access decision in Smack comes out of `smk_access()`,
which returns `-EACCES`; that holds in Samsung's own patched Smack too. An **EPERM**
on opening a file that exists is therefore not a Smack denial, and a privilege
configures nothing else that could produce it. `SmackWall` has always printed the
two apart — it just drew the same conclusion from both.

**A privilege could not have granted it even if it were Smack.** `security-manager`
generates an app's Smack rules from `policy/app-rules-template.smack`, which is
fixed and privilege-independent; the app label gets `wx` on `System` and
`System::Privileged` and **no `r`**, plus `rxl` on `System::Shared`, `User::Home`
and its own paths. Ordinary `/usr/lib` is readable because the rootfs carries the
Floor label `_`, which Smack lets anyone read. The only file that turns a privilege
into a Smack rule, `policy/privilege-smack.list`, holds two entries upstream, and
`SmackRules::addFromPrivTemplate` rejects any rule whose subject or object is not a
`~PLACEHOLDER~` — so a privilege **cannot name a literal label** such as
`System::Privileged` at all. What a privilege does grant is a Cynara policy and,
for ten of them, a supplementary Unix group. A group refusal is EACCES as well.

**What is left is Samsung's own.** Their TV kernels carry a proprietary LSM,
`security/sfd`, wired into `security_inode_permission` *ahead of* Smack. Its
SecureContainer hook returns `-EPERM` for any file whose Smack label begins with
`!` unless `current->uepLevel` is at or above the container threshold, and
`uepLevel` is a level byte read at `execve` out of a UEP signature block appended
to the binary by Samsung's internal signing service. It is inherited from the
platform binary that launched us. Nothing in a `.tpk` — no privilege, no author or
distributor certificate, partner or otherwise — writes a UEP signature. The gate is
not a privilege tier: it is **above platform level**, and platform signing would
not reach it either.

That path is an exact match for the symptom and the only one in that kernel that
produces EPERM from `open()` on an existing regular file, but it is a match made
from a 2018 KantS kernel drop and a Tizen 5.0 one, not from the Q80's own firmware.
The one measurement that would confirm it outright is `security.SMACK64` on the
library: a value starting with `!` closes the case. `SmackWall` already asks for it
last, and the Q80's trail ends on exactly that line — `probe: getxattr
security.SMACK64` — with the next entry belonging to the following launch. Either
the call does not return on that set, or the user closed the app in the same second.
We cannot tell which, and there is no second way to read a label from managed code.

**What this closes.** The manifest route for issue #17 is not unlikely, it is
structurally impossible. Do not add privileges to `Overscan5/tizen-manifest.xml`
looking for this one: there is nothing to find, and a privilege the certificate
does not cover fails the install outright on the sets that currently work.

### What is left on the Q80, and the decision nobody should take alone

Both questions this was waiting on were answered by the report on `build-d8563ab`,
and the section above is what came back. Written down here rather than carried in
somebody's head, because the next person to work on it may be a different person at
a different desk.

**Where it stands.** `ewk_init` returns and the trail is legible, so the launchpad
theory is dead and the `Heartbeat` question is closed. The verdict is the EPERM row
— refused above Smack, by a gate no manifest reaches. That leaves exactly one idea,
and it is a judgment call rather than a technical one:

> Ship a stub `libprivileged-service-client.so` in the package's own lib directory
> and `dlopen` it by absolute path with `RTLD_GLOBAL` before the implementation.
> glibc resolves `DT_NEEDED` against the sonames already loaded in the process, so
> the loader would never go to `/usr/lib` for it — the same mechanism
> `NuiNativeTouch` already relies on to pin DALi's binder. With `RTLD_LAZY`, only
> the symbols the engine actually calls would have to resolve.

Two of its three gates are met: the verdict is a read denial rather than a mapping
one, and `e_machine` names what the stub would be built for. A cross toolchain is
obtainable without root the same way the openssl 1.1 shim was (`apt-get download`
plus `dpkg-deb -x`).

**The third gate has not actually been tested, and there is a reason to doubt it.**
`own code: yes` reports that this process can map *its own assembly* `PROT_EXEC` —
but that assembly is a PE file, and the same SFD module only inspects files it
recognises as ELF. For an unsigned ELF on a writable mount its `mmap_file` hook
returns `SF_STATUS_UEP_FILE_NOT_SIGNED`, which in enforce mode is another EPERM.
A stub would be the first native `.so` this repo has ever shipped, so nothing we
have measured says a native library of ours can be mapped executable at all.

So the stub had a cheap prerequisite, and `build-9d856d1` is the build that asks it.
**That is where this stands as of 2026-08-27: shipped, asked on the issue, waiting
on Willou-Gillou.** Nothing else is pending on that set.

`Overscan5/res/libovprobe.so` is a real ARM shared object — one function, no
`DT_NEEDED`, `SONAME libovprobe.so`, built freestanding by `tools/elfprobe/build.sh`
from `ovprobe.s` with nothing but `as` and `ld` out of one downloaded `.deb`. It is
committed rather than built in CI, because CI has no ARM toolchain and the file
changes never. `res/` is where it lives because that is the app's own read-only
directory, the one an app rule grants `rxl` on, and where a real stub would have to
live too.

`NativeProbe` asks the set three things about it, each named on the trail before the
call is made: that it can be read, that a page of it can be mapped
`PROT_READ|PROT_EXEC`, and that the dynamic loader will take it
(`RTLD_LAZY|RTLD_GLOBAL` by absolute path, then `dlsym` for the marker — the same
two flags a stub would need). The verdict lands in the report as `own native :`.

It also prints `e_flags` for our library and for `libchromium-ewk.so` side by side.
ARM writes its float ABI there, and a `dlopen` refused for an ABI mismatch reads
exactly like one refused by policy unless those two numbers are next to each other.
Ours is `0x5000200` (soft), which is what a Tizen armv7 `gnueabi` build carries —
but that is the sort of thing worth measuring rather than believing.

**It runs in front of `SmackWall.Investigate`, on the same thread.** That looks
backwards against the rule about probes going last, and it is deliberate: the last
step of that investigation is the `getxattr` the Q80 has not come back from twice,
so anything queued behind it never runs at all. Both sit after the engine has
already failed, so neither is in front of anything that still matters.

**What the next report decides, and there are only two branches.** The line to read
is `own native :` in the `:8081` report.

- **It maps and loads** — the last gate is met and the stub becomes buildable. It is
  still Patrick's call, not a session's, for the reasons above. `tools/elfprobe`
  already has the toolchain recipe and an assembly file to copy; a stub differs from
  the probe only in its `SONAME` and in being `dlopen`ed by
  `ChromiumImpl.Preload` before the implementation.
- **The mapping is refused** — the same wall covers anything we ship, the stub is
  dead too, and the honest answer is that this TV cannot run Overscan. Say so and
  close #17 rather than asking that reporter for another install. That was written
  into the issue reply in advance, so it will not come as a reversal.

One packaging fact worth not re-deriving: a file in `Overscan5/res/` needs no
`.csproj` change to be packaged, lands at `res/` in the tpk, and **is covered by
`signature1.xml`** — checked against the published `build-9d856d1` asset, where
`res/libovprobe.so` is byte-identical to the committed one. That last part matters
because reporters re-sign with their own certificate: a file outside the signature
would fail their install rather than ours.

**The judgment is the point, and it is Patrick's to make, not a session's.** The
stub grants nothing — it cannot give this app a privilege it does not have, and if
the engine genuinely uses that library the result is a browser that fails later
instead of sooner, which may be worse than failing now. Against that: it is a
compatibility shim of exactly the kind an app ships when a platform library is
absent, and it is the only remaining path to that TV running this browser at all.
Do not build it speculatively, and do not ship it without asking.

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

## Remotes without a numpad

Every function in this browser except the pointer used to sit behind a number key,
and the slim remote that ships with recent sets — The Frame's among them — has no
number keys at all. On one of those the app was a pointer and nothing else: no
address bar, no start screen, not even the diagnostics screen that would have said
so. That was issue #27.

The fix is an on-screen menu listing every action, walked with the D-pad. Three
things about it are deliberate:

- **`RemoteMenu` (in `src/common`) holds the list and the selection; each UI draws
  it.** The two builds do not offer quite the same actions — NUI has no pointer-style
  toggle, ElmSharp has no `Settings` for one — so each supplies its own item array
  rather than the model carrying names for things one of them cannot do.
- **Both builds route the number keys and the menu rows through one `RunAction`
  switch.** Before this there were two copies of every action body, one per input
  path, which is how a shortcut and a menu entry quietly drift apart.
- **The menu opens on a *hold* of OK, detected from key repeats.** Tizen delivers a
  held key as repeated Down events, so a press still clicks the instant it arrives,
  exactly as it always did. Recognising the hold by waiting for the Up event would
  read better and would put every click in this browser at the mercy of a firmware
  delivering one — and the sets in issues #13 and #17 are a standing reminder of how
  much of this platform does not do what it says. Once the menu is up, the rest of
  that hold is dropped: those presses are the button the user has not let go of yet,
  and letting one through would pick the first row of the menu they were still
  opening.

A dedicated **menu**, **tools** or **play/pause** button opens it in one press
instead. Which name such a button sends is undocumented and differs between remote
generations, so `RemoteKeys.MenuKeys` answers to all the plausible ones rather than
the one that happened to work on a set we could test. Transport buttons are kept
separate in `MediaKeys`: they open the menu too, but stop doing so once keys are
routed to the page (key 4), because a browser that swallows Play/Pause during a
video is its own kind of broken.

Anything still unrecognised prints its own name on the remote card in the corner —
not only in the diagnostics log. The user who most needs to report an unknown button
is the one whose remote cannot reach the diagnostics screen.

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

The script reports `FRAME:<tag>@<x>,<y>` back over the bridge — the tag, and the
point in the page's own CSS pixels — and the native side follows up with the real
click, rather than the native path being used for everything. That is
deliberate: **a real click on a text field focuses it, and a focused field raises
the TV's IME**, which then swallows the remote — the exact failure the in-page
pointer exists to avoid.

#### The same thing on NUI, one layer further down

NUI has no Evas canvas to feed — the web view is a DALi actor drawn from a texture —
and at API 9 the managed surface offers nothing in its place: `WebView` has no
`SendTouchEvent`, and `Touch` cannot be constructed with a position. So the 9.0+
package used to answer a captcha with "Can't click inside this frame", which is
issue #20 on a 2025 set.

The path exists one layer down. DALi's own C# binder, `libdali2-csharp-binder.so`,
exports both halves, because TizenFX calls them itself from its internal `Interop`
layer — only the managed wrappers are marked internal:

| Native export | Signature (from `Tizen.NUI.dll` metadata) |
| --- | --- |
| `CSharp_Dali_new_TouchPoint__SWIG_0` | `IntPtr(int deviceId, int state, float x, float y)` |
| `CSharp_Dali_Window_FeedTouch` | `void(HandleRef window, HandleRef point, int timeStamp)` |
| `CSharp_Dali_delete_TouchPoint` | `void(HandleRef point)` |

`NuiNativeTouch` P/Invokes them directly, the same way `NativeEngine` and
`NativeMouse` P/Invoke the EFL sonames. What the platform does next is the whole
point: `DevelWindow::FeedTouchPoint` injects the point into DALi's core, which
hit-tests it by screen position like any other touch, delivers it to the web view
actor, and the toolkit's `WebView::OnTouchEvent` hands it to the engine. Chromium
routes it into whichever frame is under the point, and because it arrived as real
input the event is trusted — the part a captcha checks.

Two details that decide whether it lands:

- **The window's native pointer** lives on `BaseHandle.SwigCPtr`, which is internal
  at API 9 (so is `GetBaseHandleCPtrHandleRef`, its public-facing twin). It is read
  by reflection rather than guessed at: the property is the same object TizenFX
  passes to this very binder, so reading it is exactly as correct as the call it
  feeds, and a rename surfaces as a named failure on the diagnostics screen instead
  of a wrong pointer.
- **The chrome must not be hit first.** DALi delivers a fed touch to the front-most
  *sensitive* actor, and the hints card alone covers a corner big enough to hide a
  captcha. Nothing in this app is ever touched deliberately — it is driven entirely
  by the remote — so the bar, the progress strip, the overlay and the hints card all
  have `Sensitive = false`.

- **The press and the release have to be a frame apart.** The first version fed both
  points back to back, which put them in the same DALi scene event queue and handed
  the engine a press and a release inside one update: a contact that never existed
  for any measurable time, which no gesture recogniser calls a tap. A timestamp gap
  does not help when both events are processed together. So the press goes now and
  the release goes 90 ms later, from a `Tizen.NUI.Timer` (see `NuiLater` — a NUI
  Timer with no live reference can be collected before its first tick and simply
  never fire, which would leave the engine with a contact still down).

`native tap :` on the diagnostics screen says what the last attempt did.

#### The feed is blind, so the page is asked

`fed tap at 705,126` means the call returned. It does not mean anything arrived —
and that is the whole of the second round of #20: the report said exactly that,
beside a captcha that had not moved. From the app's side a feed that lands and a
feed that vanishes are identical, which makes the next step unguessable.

Two facts are observable from inside the page, and only from there:

- **`isTrusted`.** Nothing this app dispatches ever has it — `fire()` builds its
  events in script — so any event the page sees with `isTrusted` set was delivered
  by the platform. That distinguishes "the touch never reached the engine" from "it
  reached the engine".
- **A cross-origin frame taking focus.** Clicking into another origin's frame
  focuses the frame *element* in the parent document, and only a real click does.
  That distinguishes "it reached the page but went somewhere else" from "it went
  into the frame and the frame's own content did not react".

So the script watches for both (capture phase, read-only) and reports them through
`__ovs.native()`; `ClickThroughFrame` clears them with `__ovs.clearNative()` before
the feed and reads them back 400 ms after — three hops later, none of them ours.
The answer appears as `frame saw :` on the diagnostics screen:

| `frame saw :` | what it means |
| --- | --- |
| `trusted=none frame=none` | the touch never reached the engine — the injection point is wrong, not the tap |
| `trusted=… frame=none` | real input arrived and was routed somewhere other than the frame |
| `trusted=… frame=IFRAME` | it went into the frame; what did not react is the frame's own content |

This also fixed something the focus guard was doing. That guard blurs anything the
page focuses, to keep a page's autofocus from raising the TV's IME and swallowing
the remote — and it was blurring frames too, which meant the app undoing the one
thing the frame-click path had just achieved. A frame is not a text field and cannot
raise the IME, so it is now left alone (and its focus recorded, per above).

#### The answer was the first row: nothing arrives

On the set in #20 (UA55M70HAULXL, Tizen 10) the report came back
`trusted=none frame=none`, four taps in a row. What that rules out is most of the
things worth suspecting, because the log also shows the feed working exactly as
designed:

- **Both symbols resolve and both calls return.** A missing export throws
  `EntryPointNotFoundException`, which would have been recorded as
  `feed failed — …`; a null return from the constructor would have said
  `could not build a touch point`. Neither appeared, so the binder has both and the
  window handle read out of `SwigCPtr` was not null.
- **The release really fired.** A press whose release never came would leave
  `_inFlight` set, and the next press would log `previous tap is still in flight`.
  Four consecutive `fed tap` lines with no such warning means the timer ticked and
  the second point went out 90 ms after the first, as intended.
- **Nothing in the scene ate it.** The chrome, progress bar, overlay and hints card
  are all `Sensitive = false` (and a failure to clear it would have been logged);
  the keyboard is `Hide()`n, and an invisible actor is not hit-tested; the web view
  is full-window and sensitive.
- **The witness cannot have missed it.** It listens in the capture phase for any
  `isTrusted` event, and separately for a frame taking focus — which is the parent
  side of a click that went *into* a cross-origin frame, i.e. the one case where no
  trusted event surfaces in the outer document. Both are empty.

So the touch is built correctly, fed correctly, and never reaches the engine. Either
DALi does not hit-test a fed point onto the web view actor on this platform, or the
TV's engine backend drops what `WebView::OnTouchEvent` hands it. From outside those
two are indistinguishable, and neither has a variation left that would not be a
guess at the same layer. **`FeedTouchPoint` is a dead end on the NUI build**, and it
stays in the tree only because it is the thing that produced this evidence.

(For the record, the managed surface at API 9 does expose `FeedTouchPoint` and
`FeedTouchEvent` after all — the P/Invoke was not necessary. It is also not the
problem: same call, same layer, same result.)

#### The way in: the engine's own protocol

One layer remains, and it is *inside* the engine rather than beneath it. Chromium's
DevTools protocol has `Input.dispatchMouseEvent`, which the browser process injects
into the widget's input pipeline: the renderer treats it as real input, so it is
`isTrusted`, and the browser hit-tests it to the right renderer — which is precisely
what makes it reach an out-of-process cross-origin frame. It is the mechanism
Puppeteer and Playwright use for this exact problem, and it needs no privilege the
app does not already have (`internet`, for a socket on loopback).

TizenFX declares `CSharp_Dali_WebView_StartInspectorServer` and its `Stop` twin in
its interop layer without exposing either wrapper in the API 9 surface, so both are
reachable the same way `FeedTouch` was. `NuiInspector` starts one and reports the
port as `inspector  :` on the diagnostics screen.

Both of the unknowns are now answered, by the set in #20: the server starts on a
retail TV with the privileges the app already has (`listening on 7011`), and it is
reachable — `/json/list` fetched from a phone on the same network came back with the
page's `webSocketDebuggerUrl`.

`NuiInspectorInput` is the client. One background thread, because everything in it
blocks and the DALi main loop may not; a `ClientWebSocket` kept open across clicks;
and three messages per click — `mouseMoved`, `mousePressed`, `mouseReleased`, 90 ms
apart, at the point the page reported. Nothing in it touches NUI.

Four things about the shape of it are not obvious, and three of them are things the
server on the other end does:

- **The point is the page's, not the window's.** `Input.dispatchMouseEvent` takes
  CSS pixels in the page's coordinate space, so `__ovs.click()` now returns
  `FRAME:IFRAME@660,124` — the cursor's own position, which it already holds in that
  space. Deriving it from the window would be right until a page is zoomed or the
  viewport is forced (key 6), and then quietly wrong.
- **The inspector's HTTP side answers 1.1 and only 1.1.** A `HTTP/1.0` request gets
  silence and a socket held open, which from a client's side is indistinguishable
  from nothing listening at all.
- **It ignores `Connection: close`.** The response arrives complete and the socket
  then stays up, so reading to the end of the stream reaches a read timeout rather
  than an end — and throws away a perfectly good answer on the way. `Content-Length`
  is what says where the body stops.
- **A socket whose target has gone still reports itself `Open`.** It says otherwise
  only when something is sent down it. So a click on a connection that was already
  open gets one silent retry: drop it, rediscover the target, connect, send again.
  (A page navigation alone does not do this — the page target is the view, not the
  document — but a server stopped and started again does, and from out here the two
  are identical until the send fails.)

The target is chosen by `"type": "page"`. The list also carries `iframe` targets —
the captcha's own renderer is one — and sending the click into the frame's target
would defeat the point: it is the page-level hit test that routes a point to the
right frame.

#### The port is opened for the captcha, not for the evening

The inspector server is unauthenticated and listens on every interface. For as long
as it is up, anything else on the network can drive this browser: read the page,
read its cookies, navigate it somewhere else. On a TV signed in to Instagram that is
not a small thing.

So it is not started at launch. `NuiInspector.Ensure` starts one the first time a
click actually lands on a cross-origin frame, and `NuiInspector.Stop` takes it down
on the next page load — the window it is open for is the captcha and a little
either side of it, rather than every session on every page, the overwhelming
majority of which have no frame in them to click.

#### Proving it without a TV in the room

A build for the TV has to be signed, installed, and reported back on by somebody who
owns the set. `tools/cdpharness` is what keeps that round trip for questions only a
TV can answer: it compiles `src/nui/NuiInspectorInput.cs` itself — the shipping file,
not a copy — and clicks with it into a cross-site `<iframe>` on desktop chromium,
which is the captcha's shape (another site, another renderer, unreachable from any
script in the page). The frame reports what it saw to its own origin, and `run.sh`
fails unless that says `trusted=true`.

Three of the four points above came out of the first ten minutes of running it. From
a TV, all three would have looked like the same thing: the engine ignoring the click.

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

## What the NUI build never asked the engine for

The two builds share `src/common` and nothing else, and everything the ElmSharp
build learned by way of a native call it had to make itself, the NUI build gets
for free from a managed property it never thought to set. Issue #20 found two of
those at once, on a set where the browser otherwise works.

**The session was memory-only.** ewk keeps cookies in RAM unless it is given
somewhere to put them, and the ElmSharp build has said so since its first release
(`Context.GetCookieManager` → `SetPersistentStorage`). NUI hangs the same manager
off the view (`WebView.CookieManager`) and this build simply never asked for one,
so closing Overscan logged you out of everything — a browser that has never been
anywhere, every launch. `WebSettings.PrivateBrowsingEnabled` is now set to false
explicitly alongside it: with it on, local storage is memory-only too, and the
engine's default for it is not documented anywhere readable.

**Video was going out through a hardware plane.** A TV decodes video on hardware
and shows it on an overlay plane, punching a transparent hole through the page
where the picture belongs. That is right for the set's own browser, which owns
the screen; it is wrong for an app whose window DALi composites itself, and there
are only a couple of those planes in the whole set. A reel feed asks for one per
video as it scrolls, which is the shape of "reels close Overscan, other video is
fine". `WebView.VideoHoleEnabled` now defaults to false — the in-page path, where
the engine decodes to a texture like any other pixels — and key `5` toggles it,
because the trade is real (a copy per frame, and some sets may not offer that path
at all) and which way a given TV needs cannot be found out from here.

Neither of those is confirmed from a TV yet. What *is* now possible is finding
out: the NUI build had no `Breadcrumbs` at all, so a page that killed the process
left nothing behind but the user's word for it. It calls `Breadcrumbs.Init` in
`Main` like the ElmSharp build, drops the address it is opening on the way in, and
the report carries the previous run's trail.

`ProcessMemory` writes the resident size to the trail every five seconds, and that
interval is the whole point. An app the low-memory killer takes away and an app
that crashes leave the same silence, and they need opposite fixes — but on the
trail they look nothing alike: a crash ends on whatever call was in flight, an
eviction ends on a memory line much larger than the ones before it. A slope only
exists if something was writing numbers down before anyone knew there was a
problem.

### What is left on the 2025 sets

Issue #20's reporter is on a Tizen 10 set running the NUI package, and is the one
person testing that half of this app in anger. Three things came back after the
captcha was fixed. As of 2026-08-27 the state is:

- **The session not surviving a restart — fixed, and it was a real bug.** Shipped in
  `build-9d856d1`. Nothing pending.
- **Reels crashing the app — a likely cause changed, unconfirmed.** `VideoHoleEnabled`
  now defaults to false for the reasons above, and key `5` puts it back. Nobody here
  has a set that runs this build, so this is the one thing in the app that was
  changed on a theory rather than a measurement. **Waiting on:** the `previous run`
  block from `:8081` after the next crash. The last line of it names the call that
  died, and the `memory:` lines separate a crash from a low-memory eviction — which
  need opposite fixes and which nothing before this build could tell apart. Ask for
  that block, not for a description.
- **"A frame at the top that cannot be clicked" on first launch — not diagnosed.**
  It is not clear whether the reporter means part of the page or part of Overscan
  (the address bar, or the remote card in the corner). Asked on the issue, with key
  `7` as the way to tell those apart. Nothing has been changed for it.

If video comes back black or silent rather than crashing, that is the in-page path
failing on that set and key `5` is the answer — not a regression to chase.

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
