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

So the stub had a cheap prerequisite, and `build-9d856d1` is the build that asked
it. **The Q80 answered on 2026-08-27 for `res/`, and the answer was no** — see *The
exec mapping is refused, and where that leaves it* below.

**The other four locations are still unanswered.** Two builds have now been spent
on the ladder rather than on the question: `build-85d0e4e` was killed on its first
rung, and `build-3368aea` stopped between the `open` and the header read on
2026-09-01 — see *The ladder also has to survive the questions nobody suspected*.
So the "if every location refuses, close issue 17" branch is **not** reached, and
saying it is would be a guess: only `res/` has ever answered. Every rung is now
ledgered and every call has a watchdog, `tools/probeladder/run.sh` holds the walk to
converging off-device, and **`build-e2914be` is the build that decides it**. What
each answer means was said on the issue before it shipped, so neither reading is a
reversal later:

- `own native : bin/ maps executable...` (or `lib/`, or `data/`) — there is a place
  in the package this set will run a file of ours from, and the stub is worth
  building.
- `own native : REFUSED in res/, bin/, lib/, data/` — with `DID NOT RETURN` counting
  as a refusal, because a mapping this firmware will not finish answering for is no
  use to a stub loaded during start-up. There is then nowhere left to put one, and
  the honest thing to say is that this TV cannot run Overscan.

Anything else — a report that still says `(not asked)`, or one that names no
locations — is a third build spent on the ladder rather than the question, and worth
reading as a bug here before it is read as an answer from the set.

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

Packaging facts worth not re-deriving, because getting one object into all three
package directories from one committed file took three attempts. `Overscan5/res/`
needs no `.csproj` change at all and lands at `res/`. A `<None>` item with
`CopyToOutputDirectory` reaches `bin/`, because that tpk directory *is* the build
output. `lib/` takes a `TizenTpkUserIncludeFiles` item with `TizenTpkSubDir`, which
is the packaging task's own supported way to place a file somewhere other than
where it sits in the project — `<Link>` on a `TizenTpkFiles` item is ignored and
fails the build with MSB3024, because the task copies by `%(TizenTpkSubPath)` and
that metadata only comes from its own globs.

**All three copies are covered by `signature1.xml` and `author-signature.xml`** —
verified on the published `build-a7b7030` asset, byte-identical to the committed
object in every case, with the new location labels present in the shipped
`Overscan.dll` to prove the asset was the build it claimed to be. The CI log cannot
stand in for that check: `TizenTpkFiles` prints an item's own identity, so the `lib/`
copy logs as `res/libovprobe.so` and `bin/` content is never listed — a log from a
build with none of this in it looks identical. That
matters because reporters re-sign with their own certificate: a file outside the
signature fails their install rather than ours.

### The exec mapping is refused, and where that leaves it

The Q80's report on `build-9d856d1` (2026-08-27) ran the whole probe:

```
ours   : e_machine=40 e_flags=0x5000200 float=soft
engine : e_machine=40 e_flags=0x5000200 float=soft
mmap PROT_READ            : ok
mmap PROT_READ|PROT_EXEC  : EPERM (operation not permitted)
dlopen: refused — .../res/libovprobe.so: failed to map segment from shared object
```

So the ABI is not the problem — those two lines are identical, which is the reason
they are printed together — and the refusal is exactly the SFD `mmap_file` shape
predicted above: an unsigned ELF of ours will not map executable. `dlopen` then fails
downstream of the same thing, which is why its message is about mapping a segment
rather than about permission.

**What is left is not a fourth idea, it is the same measurement made where it has
not been made.** The one file of ours that *did* map executable on that set is the
app's own assembly, and it differed from the probe in two ways at once — it is a PE
rather than an ELF, and it is in `bin/` rather than `res/`. Only the format was named.
`build-<next>` ships the same object in **both** directories and asks about it in
three forms per location, plus a control:

| Reading | What a refusal there means |
| --- | --- |
| anonymous `PROT_EXEC` page | If refused, the kernel refuses executable memory outright and none of this is about us. The runtime's JIT does this every launch, so it is known safe — and it is no use to a shim either way, because `DT_NEEDED` resolves against the loader's link map and only `dlopen` writes to that. |
| `res/` ELF | The baseline, already answered: refused. |
| `bin/` ELF, by its ordinary `/opt/usr/apps/.../bin/` path | The directory the assembly that mapped executable lives in. A refusal here says the difference was the file format, not the directory. |
| `bin/` ELF again, by the assembly's own path | TizenFX does not expose `bin/`, so managed code reaches it only through `Assembly.Location` — which on Tizen is `/proc/self/fd/<n>/bin/...`, the exact path form of the reading that came back `yes`. Without both, a `yes` cannot be attributed to the directory rather than the path form. |
| `lib/` ELF | The third and last package directory, and the conventional home for a native library, in case the installer labels it differently. |
| `data/` ELF, copied and `chmod 0755` | The only mount we choose rather than accept, and the SFD hook is documented as inspecting unsigned ELF on *writable* mounts. |
| each of those re-asked through `/proc/self/fd/<n>` | Same inode, same mount, different path form. If only this form succeeds, the policy is keyed on the path and a package can move. |

Labels and mounts for all three copies are read **after** every mapping question, not
beside each one: they only explain *why*, and `getxattr` is the call this set has not
returned from twice. The sequence itself was validated against a real `.so` on the dev
box first — anonymous exec, both path forms, the copy, the `chmod`, the `dlopen` and
the two header offsets — so a refusal on the TV cannot be our flags.

**If every location refuses, that is the end of the stub and the end of #17.** Not the
end of one attempt at it: with anonymous memory allowed and every file location
refused, the gate is on files this app supplies, and there is nowhere left to put one.
Say so, and close #17 — that branch was written into the issue reply in advance, twice,
so it does not read as a reversal. The only variable left outside the app is which
`libchromium-impl.so` build the firmware ships, which is worth one question about a
pending software update and no more than that.

**The stub itself, if a location ever does allow it, is still Patrick's call.**

The judgment is the point. The
stub grants nothing — it cannot give this app a privilege it does not have, and if
the engine genuinely uses that library the result is a browser that fails later
instead of sooner, which may be worse than failing now. Against that: it is a
compatibility shim of exactly the kind an app ships when a platform library is
absent, and it is the only remaining path to that TV running this browser at all.
Do not build it speculatively, and do not ship it without asking.

### The ladder has to survive its own questions

`build-85d0e4e` shipped that five-location table and came back having answered one
row of it. The Q80's trail ends here:

```
18:32:45    res/ mmap PROT_READ|PROT_EXEC: EPERM (operation not permitted)
18:32:45    probe: mmap PROT_READ|PROT_EXEC via /proc/self/fd res/
```

— and nothing after. `bin/`, `bin/ (assembly path)`, `lib/` and `data/` were never
asked, and the report header still read `own native : (not asked)` on a run where
the probe plainly ran, because `Summary` is only set once the whole walk returns.
A second launch in the same report ends one line earlier, at the plain exec mmap.
So the `/proc/self/fd` retry — the call that build added — is what ends the launch,
and on this set an executable mapping is refused with a signal rather than an
errno some of the time.

That is the fourth time a probe has stood in front of the thing it was meant to
explain (#13, #17, #17, now #17 again), and the first time it stood in front of
*itself*. The comment on `NativeProbe.Run` predicted the risk and the ordering did
not survive it: one fatal rung eats the four behind it, every launch, forever.

**`NativeProbe.Ledger` is the fix, and it is `Breadcrumbs`' own trade carried one
step further.** Each of the three calls that can kill — the exec mmap, the
`/proc/self/fd` retry, the `dlopen` — writes its own name to
`data/probe-ledger.txt` before it is made and its answer to the same file after. A
launch that never comes back leaves a name with no answer; the next launch reads
that as `KILLED THE PROCESS`, records it, skips it and carries on to the next rung.
So every launch makes at least one step of progress even if every step is fatal,
the ladder converges, and the instruction to a reporter is the simplest one there
has ever been on this issue: open it again until the report stops saying `(not
asked)`.

Three things that are easy to get wrong here and are already right:

- **The replayed answer comes back bare.** `Succeeded` tests the end of the string,
  so decorating a resumed `ok` with "(asked on an earlier launch)" would silently
  turn every replayed success into a failure. The note goes on the trail instead.
- **The file is stamped with a version.** A ledger from another build is deleted
  rather than parsed, or the first run of a new ladder would report the old one's
  answers. Bump `LedgerVersion` whenever a step changes its name or its meaning —
  the rename to per-rung names took it to `ledger 2`.
- **A killed step is a verdict, not a gap.** `Location.KilledUs` reaches the summary
  as "asking `res/` ended the launch", which is a *harder* refusal than `EPERM` and
  worth naming as one. A set that kills the asker is not a set a stub loads on.

The `Launches` count in the verdict is there for the same reason: a number above one
says the set chose to die rather than answer, and that is a finding rather than an
accident of the reporter's evening.

### The ladder also has to survive the questions nobody suspected

`build-3368aea` shipped that ledger and the Q80 stopped the walk again on
2026-09-01, two rungs earlier than anything the ledger covered:

```
12:59:05    probe: copy libovprobe.so to data/
12:59:05    engine : e_machine=40 e_flags=0x5000200 float=soft
12:59:05    probe: open res/ /opt/usr/apps/org.apps2samsung.overscan/res/libovprobe.so
```

— and nothing after, for the 98 seconds until the next launch. The next trace line
would have been `probe: read header of res/`, so the walk stopped inside `open`, the
header `read`, or the plain `PROT_READ` mmap: the three rungs left unledgered
*because nothing had ever refused them*, and therefore the three with nothing on
disk to resume from. The report read `own native : (not asked)` for the second
build running, on a launch whose trail plainly showed the probe walking.

Two things were wrong, and each of them alone was enough:

- **Only the rungs predicted to be dangerous were ledgered.** That prediction has
  now been wrong twice, in both directions: the rung expected to be fatal answered
  `EPERM` politely, and one of the three nobody instrumented is what stopped the
  walk. **Every call in the walk is ledgered now** — the `open`, the header read,
  the readable mapping, the engine's own header, the copy into `data/`, and the
  `mount` and `getxattr` reads after the verdict. A rung "known safe" is a rung with
  no evidence behind it; a ledger line costs one write.
- **A call that never returns is not a call that killed the launch, and the ledger
  could only ever learn from the second.** The probe runs on a background thread
  (`SmackWall.InvestigateInBackground`), so a call stuck in the kernel leaves the
  app alive, the walk parked, and the report saying nothing — which is
  indistinguishable from a walk that never started. This set does exactly that:
  `getxattr` has hung it twice, and the copy into `data/` proves a plain read of
  `res/` is not refused, which makes a hang the better reading of that trail than a
  kill. **Every call goes out under a watchdog now** (`Ledger.Watched`, five
  seconds), and a miss is recorded as `DID NOT RETURN` *in the same launch*, so a
  set that hangs on every rung still finishes the ladder without anybody opening
  the app again.

The header follows from the same reading. `Summary` was only written when the whole
walk returned, so a page loaded a second after launch — which both reports were, and
which is a reasonable thing for a reporter to do — showed `(not asked)` no matter
what was already on the books. It is now written from the ledger before the first
question and again after every location, and `NativeProbe.Dump` prints the ledger
above this launch's trail. The half of the probe that outlives a launch is the half
worth showing first.

**`tools/probeladder/run.sh` holds all of that off-device.** It compiles the
shipping `src/common/NativeProbe.cs` against stand-ins for the three platform types
it touches and walks it over a fixture: a rung that hangs (a FIFO with no writer,
which is an `open` that blocks for as long as the probe is willing to wait), a rung
abandoned by a previous launch, a ledger from another build, and a clean run twice
over. The `hang` scenario is the one that earns it — against `build-3368aea`'s file
it does not fail, it never returns, which is precisely what the Q80 did with
somebody's evening. Two builds is too many to spend discovering that a ladder does
not climb.

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

**Video was going out through a hardware plane — and that turned out not to be
the problem.** A TV decodes video on hardware and shows it on an overlay plane,
punching a transparent hole through the page where the picture belongs. That is
right for the set's own browser, which owns the screen; it looked wrong for an app
whose window DALi composites itself, given there are only a couple of those planes
in the whole set and a reel feed asks for one per video as it scrolls. So
`WebView.VideoHoleEnabled` was defaulted to false — the in-page path — with key `5`
to toggle it.

The reporter's answer killed the theory. His reels stopped crashing in that build
and came back **black**; pressing `5` for the overlay is what made them play. What
actually fixed the crash is the change sitting next to it: with private browsing
on, an endless feed piles its storage up in RAM until the low-memory killer
arrives, and *that* fits "reels close the app, other video is fine" — an infinite
scroll — far better than a plane shortage does. The default is the engine's own
again, and the toggle stays for the set that wants the other one.

**Applying that setting to a WebView that has not loaded a page kills the view.**
`ApplyVideoPath` used to be called during start-up, straight after the constructor.
On a Tizen 10 set a stored overlay applied there gave a view that then never began
a load at all — no `PageLoadStarted`, no `PageLoadError`, an empty url, a black
screen on every launch, surviving an app restart *and* a TV restart because the
preference is on disk. The same property set on a live view works, which is how
that reporter's reels played in the first place. A stored preference is therefore
read at start-up and applied from `OnTick` once a page has been through, and an
install that has never pressed `5` is never told anything at all.

`CheckSomethingLoaded` exists because of how nearly invisible that was. Every other
failure leaves a line: a load that fails raises `PageLoadError`, a page that kills
the process ends the trail on its address, a launch that dies never gets that far.
A view that silently declines to navigate leaves nothing — the app is alive, the
menu opens, `:8081` answers, and the screen is black. It was readable only because
the *absence* of "load started" happened to fit in a log with room for it. Six
seconds after a load is asked for with none begun, the trail says so by name, video
goes back in the page, and the same load is asked for again; a second failure says
the view is not starting loads at all rather than retrying in silence.

The session fix is confirmed from a TV; the video theory was not, and was wrong.
What made both answerable is that the NUI build had no `Breadcrumbs` at all, so a page that killed the process
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

### A timer nobody was holding, and everything that stopped with it

`OnCreate` armed the 150ms heartbeat as a bare local:

```csharp
var timer = new Timer(150);
timer.Tick += (s, e) => OnTick();
timer.Start();
```

A NUI `Timer` with no live reference is collected, and this file says so itself —
`NuiLater` keeps a list for exactly that reason, and the ElmSharp build has always
kept its own in `_tick`. So the heartbeat ran until the first garbage collection,
which a page load reliably causes, and then stopped. Nothing announced it. What
stopped with it was everything `OnTick` is responsible for:

- the blank-view watchdog, so a dead view was only noticed when it happened to be
  dead *before* the first collection;
- `NoteMemory`, so the memory slope that separates a crash from an eviction stops
  being written down exactly when pages start loading — which is when it matters;
- the deferred video path, so a stored preference was never handed over at all;
- the chrome auto-hide.

Issue #20's fourth report is what this looks like from a TV, and it is worth
knowing how to read because none of it says "timer":

- the trail's `memory:` lines stop the moment loads begin and never come back —
  46 seconds of a browsing session with no heartbeat in it;
- the report shows `memory : 149 MB resident, peak 127 MB`. A peak below the
  resident size is impossible while something is updating the peak;
- `video : in page` in the report with `_videoPathPending` still set, and no
  `video path:` line anywhere in the run — the preference read at start-up and
  never applied. This is also what made the trail *look* self-contradictory: the
  toggle logged the opposite of what the store held, which is only possible if the
  deferred apply never ran.

The rule this leaves behind: on NUI, a timer, like anything else with a native
object behind it, has to be held by something that outlives the method that made
it.

### The blank-view recovery has to change something

`CheckSomethingLoaded` recovered by calling `ApplyVideoPath(false)` — a fixed
value, on the theory that a stored overlay is what kills a fresh view. Issue #20's
fourth report killed the theory and the recovery in one go:

- one launch was dead with the **overlay** path in force (the menu toggle in that
  run logged `in page`, which it can only do from overlay), and another was dead
  with **in page** in force. A cause present in both directions is not the cause.
- because the recovery applied a fixed `false` rather than the *other* path, in
  every trail we have it re-applied the value already in force. It was a no-op,
  and the "second attempt failed too" verdict that followed was measuring nothing.

It is a ladder now, each rung named on the trail before it is tried and the rung
that worked kept in the report as `blank view:`:

1. **rebuild the WebView** — the view is a managed object over an engine that
   outlives it, so if the view alone is broken this is the entire fix;
2. **clear the stored session and rebuild again** — `WebCookieManager.ClearCookies`
   plus `WebContext.ClearCache`/`DeleteAllWebStorage`/`DeleteAllWebDatabase`/
   `DeleteAllApplicationCache`. This is the reinstall a reporter would otherwise be
   asked for, without the reinstall: it costs the sign-ins and nothing else, since
   favourites and history are ours and in files the engine has never heard of;
3. **say the view is not starting loads at all**, because nothing this app owns is
   left to try.

The order is the diagnosis. A view that comes back at rung 1 was never reading
anything on disk; one that comes back at rung 2 was, and the profile is the
suspect the video path used to be; one that reaches rung 3 is a set where a
sideloaded NUI WebView cannot be made to navigate, and that is the answer rather
than another build.

**That last sentence turned out to be wrong, and issue #53 is the set that
proved it** — it reached rung 3 on every launch and then loaded a typed address
at the first ask. The ladder had changed the view and the profile and never the
*page*, and the page was the fault. There is a rung in front of the others now
for exactly that case; the next section has it.

### The start screen was eating itself

Issue #20's reporter came back (as #53) with the black screen at every launch
that the section above had written down as "did not recur, not confirmed fixed".
Two reports, four days apart, identical: the ladder ran to the end — new view,
cleared session, new view — and declared the view dead. The first of those
trails also had this in it, fifty seconds after the verdict:

```
23:00:42  the web view is not starting loads at all
23:01:32  navigate: https://tv4h.weebly.com
23:01:33  load started: https://tv4h.weebly.com/
```

The view was fine. What never began, six times over two launches, was the
**start screen** — `LoadHtmlString`, the page this app generates — and every
rung of the ladder had asked for the same one again.

The reason is in a healthy trail from four days earlier, once you know to look.
The NUI WebView has no base-URL overload; a page loaded from a string is
reported by the engine as a `data:` URL carrying the whole page percent-encoded,
and that is what `PageLoadStarted` logs for it. `Store.RecordVisit` only knew
the ElmSharp build's marker, `https://overscan.start/`. So on NUI **every start
screen was recorded as a visit**, titled "Overscan", and the next start screen
put it in a tile — the trail shows `href='data:text/html;charset=utf-8,%253C…'`,
a start screen inside a start screen, double-encoded. That one measured 12,119
characters against 3,507 for the same page with an empty history. Eight recent
tiles, each a previous start screen that itself holds the eight before it, with
`%` becoming `%25` at every level: roughly ×2.3 per launch. From 3.5 KB it
passes Chromium's URL ceiling — `url::kMaxURLChars`, 2 MB, past which a `GURL`
is simply invalid and the navigation is dropped without a start or an error —
on about the ninth start screen. Nine launches is "after some time or days",
which is exactly what the issue said.

Everything else on those reports now reads as the same fault:

- rung 2 clearing the profile did nothing because history is *ours*, in a file
  the engine has never heard of — the one thing the ladder was designed to
  preserve was the one thing that was wrong;
- the reinstall that "fixed" it the first time wiped `history.tsv`;
- resident memory jumping from 75 MB to 377 MB with nothing loading fits a
  multi-megabyte string being built, escaped and refused (an inference from the
  trail, not a measurement);
- the ElmSharp build never had it, because `LoadHtml(html, BaseUrl)` makes the
  engine report the marker and the guard matched.

The fix is in `Store`, for all six packages: `IsGenerated` knows both shapes
(the marker and any `data:` URL), `RecordVisit` and `ToggleFavourite` refuse
them, and `Init` drops any that an earlier build let into either file and
writes the file back clean — which is what turns that set's black screen back
into a browser without a reinstall, and says how many it dropped on the log.
`tools/startpage/run.sh` compiles the shipping `Store.cs` and `HomePage.cs` and
holds them to it; against the previous `Store.cs` its twelve-launch check ends
on a 21-million-character page.

Two things changed on the NUI side as well. **The ladder has a rung in front of
the others for the start screen**: when the load that never began is the
generated page, the first thing changed is the page — `ShowHome` builds it bare,
no tiles, while a recovery is in progress — because the tiles are the only part
of it that differs from one launch to the next. And the report carries a
`start page:` line with the page's size, since nothing on it said how large the
page the engine was refusing had got. The last rung no longer says the view is
not starting loads "at all"; it says *this* load, which is all it knows.

The rule this leaves behind is about the ladder, not the store: **a recovery
that retries the same request on a different substrate has not changed the one
thing the request itself could be wrong about.** Rebuilding the view answered
"is the view broken" three times; nobody had asked "is the page".

### The reels death is not an eviction, and it leaves no line at all

`build-c0cd5ab` is the first build whose heartbeat survived to the end of a run,
and the trail issue #20 sent back from it settles one thing and exposes another.
The run that died ends like this:

```
15:24:37  memory: 87 MB resident (peak 91)
15:24:42  memory: 89 MB resident (peak 91)
15:24:47  memory: 89 MB resident (peak 91)
15:24:52  memory: 64 MB resident (peak 91)
```

**The low-memory killer is not what takes this app away.** An eviction is a slope
and there is no slope: flat around 90 MB for the whole session, with the last
reading *down* twenty-five megabytes rather than up. Two builds' worth of theories
about the video plane and about a memory-only profile are now both dead, and so is
the memory one that replaced them.

What the trail does not contain is just as important. `NuiProgram` writes
`app loop returned` on a clean exit and `FATAL in Main` on an exception out of
`Application.Run`, and **neither is there**. So the loop did not return and did
not throw: something ended the process without unwinding it, five seconds after
the app was in perfect health.

Three things can do that, they need completely different fixes, and none of them
left a mark. That is what `NuiDeathWatch`, the lifecycle overrides and
`NuiMediaWatch` are for — every one of them exists to make one of these three
answers visible on the next trail:

1. **The platform closed us.** Tizen asks with SIGTERM
   (`PosixSignalRegistration`, held in a static list for the same reason the
   heartbeat is held in a field), and closes an app it has already paused without
   asking. `OnPause`/`OnResume`/`OnTerminate` and the window's
   `FocusChanged`/`VisibilityChanged` all reach the trail now. This answer is more
   plausible than it looks: a window that stops being visible makes the engine give
   up its graphics resources, which is exactly a twenty-five megabyte drop, and a
   backgrounded app is one the platform may then terminate. From the sofa, "I
   pressed something and Overscan vanished" and "Overscan crashed" are the same
   sentence.
2. **Our own code threw where `NuiProgram` cannot see it.** A managed exception
   raised inside a native callback — a timer tick, an engine event — does not come
   back out through `Application.Run`. It goes to `AppDomain.UnhandledException`,
   which is hooked, and then the process dies.
3. **A hard native crash inside the engine.** Nothing fires, and that absence is
   the answer by elimination — SIGSEGV cannot be watched from managed code without
   taking the runtime's own handler off it, which is not worth doing to learn
   something the other two lines already tell us. What *can* be had is whatever the
   engine printed on its way down: `NativeStdErr.StartSession` holds the
   stdout/stderr redirection open for the whole run instead of one call, so
   chromium's render process — a child, which inherits the descriptors — writes its
   own last words into a file `Breadcrumbs.Init` moves aside for the next launch to
   read. The warning in that class about not capturing across a fork is what makes
   this work rather than what forbids it.

The memory interval went from five seconds to two at the same time, because five
is what cost the resolution here: the reading before the death had already dropped,
so whatever happened had happened by then and there was nothing in between.

#### Counting the decoders, over the console

`NuiMediaWatch` puts a census of the page's `<video>` elements on the trail every
two seconds — how many exist, how many are decoding, their dimensions,
`readyState`, dropped frames and any `MediaError` — plus a line for a media
`error` or `stalled` event. A TV has a small fixed number of hardware decoders and
one video plane, and a feed that mounts a fresh `<video>` per reel and leaves the
last few running is the shape of thing that exhausts them. If that is what happens
here, the count in the last line before the death is the finding, and capping it is
the fix.

**The channel is the page's console, not `EvaluateJavaScript`.** NUI keeps a
single pending result handler per view, so anything polling with a callback steals
the answer from whatever the cursor or the frame-click path is in the middle of
asking. A `console.log` travels the other way, arrives through
`WebView.ConsoleMessageReceived`, and cannot collide with anything. The same hook
carries the engine's own console errors, which is where a media pipeline says it
has failed — bounded at 24 distinct lines per page, because Instagram alone
produces a steady stream of them and the trail's value is that its last lines are
readable.

Two things were wanted here and are not available at API 9: `FullscreenEntered`
and `FullscreenExited` are internal callbacks on NUI's `WebView` rather than
events, and `NUIApplication.MemoryLow` is internal too — the low-memory warning
used instead is `CoreApplication.LowMemory`, which is public and says the same
thing.

### It is the engine's decoder, and it is the third one that kills it

> **Superseded in part — read *It is not the count* below before acting on this.**
> The segfault is real and it is the engine's, and that half stands. The *reason*
> recorded here — that the set runs out of decoders at three — does not: a later
> trail shows twenty-three of them in nine minutes on another path without a
> wobble. What follows is kept because `NuiVideoCap` was built on it and the
> reasoning is still the record of how the crash was found.

`build-85d0e4e` is the build that answered it. Of the three candidates above it is
the third — a hard native crash inside the engine — and the stderr session added
for exactly this case is what caught it. The previous run's native output ends:

```
16:43:30  GstOmxUhdVideoDec ... omxuhdvideodec0   (+ gst_buffer_map_range assertion)
16:43:48  GstOmxUhdVideoDec ... omxuhdvideodec1   (+ gst_buffer_map_range assertion)
16:43:59  GstOmxUhdVideoDec ... omxuhdvideodec2   (+ gst_buffer_map_range assertion)
          DotNET onSigsegv called on org.apps2samsung.overscan / render_thread-o(4690)
```

Neither `app loop returned` nor `FATAL in Main`, no SIGTERM, no `OnPause`, no
`unhandled managed exception` — the two lifecycle answers stayed silent and the
segfault landed in chromium's **render thread**. So nothing this app is allowed to
do can catch it, and nothing this app did caused it.

**What it can do is not walk into it.** Three facts line up:

- **One `GstOmxUhdVideoDec` per reel, and none of them go away.** The decoders are
  numbered `0`, `1`, `2` over twenty-nine seconds, and the process dies as the third
  is allocated. A TV has very few UHD decoder instances; three concurrent is over
  the line on this set.
- **`playing=1 of 16`.** Sixteen `<video>` elements mounted, one decoding. So it is
  not concurrent playback that exhausts it — a *paused* reel keeps the decoder it
  was given. This is precisely the shape `NuiMediaWatch` was built to look for, and
  the count is the finding.
- **The decoder is a UHD one for a `360x360` video.** Nothing to be done about that
  from here; it is the engine's own pipeline choice. It only makes the ceiling
  lower.

`NuiVideoCap` takes those decoders back: every two seconds, any `<video>` more than
one screen outside the viewport, paused, and with `readyState > 0` has its source
removed and `load()` called. **Pausing is not enough — all sixteen were already
paused.** Removing the source is the only thing that makes the engine drop the
pipeline.

Four decisions in it that should not be re-derived:

- **Nothing is released that cannot be put back.** An ordinary URL is remembered on
  the element and restored when it scrolls near again. A `blob:` or `srcObject`
  source belongs to a `MediaSource` the page built and may have revoked, so taking
  it away is permanent — they are counted as `held` and left alone. See *Releasing a
  source we cannot restore* below: shipping the other way round cost the reporter his
  one working configuration.
- **An element fed by `<source>` children is left alone.** `load()` would pick the
  same child straight back up, so there is nothing to gain and a working video to
  lose.
- **A far-offscreen video that is still *playing* is left alone.** Its audio is
  probably what the viewer is listening to.
- **`removeAttribute('src')` and then `load()`, never `src = ''`.** An empty string
  resolves against the document and sends the element off to fetch the page itself.

Elements it has released are flagged `__ovsReleased`, and `NuiMediaWatch`'s distress
handler skips them: an element we just took the source from is *expected* to
complain, and reporting that would spend the 24-line error budget on our own doing
and bury the pipeline failures the budget exists for.

**`tools/videocap/run.sh` is where this is checked.** It is the first script in the
repo that changes the page rather than reading it, and every way it can be wrong
arrives from a TV as "the video is black now" — indistinguishable from the engine's
own failures. So the sweep is asked against desktop chromium first, on a real
`<video>` in a real layout engine, with the script lifted out of `NuiVideoCap.cs`
rather than copied, for the same reason `tools/cdpharness` compiles the shipping
file.

**This does not fix the segfault and the reply on the issue says so.** It keeps the
app from reaching the allocation that trips it, which is the only lever on this side
of the wall. If a reel feed still gets to three decoders, the trail will now say how
many were released on the way.

#### Releasing a source we cannot restore

`build-3368aea` shipped the cap releasing `blob:` sources too, on the reasoning that
a crash is worse than a guess and the feed would re-source the element itself. It
does not. The report came back:

```
video     : hardware overlay  (key 5)
media     : playing=1 of 9 — 0x0 rs0
video cap : released 9 (9 blob), restored 0
```

Nine reels, every one of them MSE-backed, every one released, none restorable — and
the *visible* video sitting at `0x0 rs0`, which is a blank screen. Overlay was the
one configuration that played reels on that set, and this turned it into "plays 1 or
2 reels, then blank". A net loss: the crash was still there and now the working path
was gone too.

**So the rule is the one the name already implies: release only what can be put
back.** `blob:` and `srcObject` are counted as `held` and never touched. The
consequence is worth stating plainly rather than discovering again — **on an MSE feed
this class does nothing at all**, Instagram reels included, and the decoder
exhaustion behind issue #20 is untouched by it. The `held` count is what says so from
the next report.

Two things this cost that are worth keeping:

- **A mitigation that changes the page is not free, and the report has to be able to
  price it.** The `video cap :` line is what made this diagnosable in one round trip
  instead of an argument about whether the build made things worse.
- **`tools/videocap/run.sh` had every case right and still shipped this**, because
  the case it did not have was "the source is one we cannot restore, so do not
  release it" — that was a *decision*, tested faithfully, and wrong. The harness now
  asserts the opposite and would fail the old behaviour. A test that encodes the
  decision cannot catch the decision.

There is no known way to make the engine release an MSE-backed decoder without
destroying the element. If one is found, it goes here.

### It is not the count — it is which sink the path uses

`build-e78c0bc`'s reports are the first carrying the engine's own native output from
**two video paths in the same evening**, and putting them side by side takes the
decoder-budget theory above apart.

The in-page run scrolled reels for nine minutes and built **twenty-three** decoders:

```
16:59:29  omxtzuhdvideodec0
17:00:01  omxtzuhdvideodec3      <- default mode was already dead by here
...
17:05:04  omxtzuhdvideodec22
```

One per reel, created and torn down as he scrolled, and it never crashed. So the set
is **not** out of decoders at three or four, a paused reel does **not** permanently
keep what it was given, and *"three concurrent is over the line on this set"* — the
finding recorded above from `build-85d0e4e` — is wrong. It was a fair reading of one
trail that only had one path in it. What the number 3 or 4 actually marks is how many
times that particular path had rolled its dice.

The three video settings take three genuinely different native routes:

| key `5` setting | `VideoHoleEnabled` | decoder | video sink | result |
| --- | --- | --- | --- | --- |
| hardware overlay | `true` | — | overlay plane, no rectangle | **plays** |
| in page | `false` | `omxtzuhdvideodec` | `directvideosink` + render rect | black, stable |
| engine default | never set | `omxuhdvideodec` | **`fakesink`** | segfaults on the 4th |

Two things follow, and both are worth more than the cap ever was.

**The untouched engine is the only configuration that crashes.** Its video goes to a
`fakesink` — decoded correctly and thrown away — with `GstOmxUhdVideoDec has no
property named 'tbm-buffer-type'` in front of it, which is the engine asking its own
decoder for a buffer type it does not implement. That path was never going to put a
picture on screen whatever we did about decoders. Leaving `VideoHoleEnabled` alone
was meant to be the modest choice, an engine default rather than an opinion about
every set; it turns out to select a broken pipeline. So the NUI build now sets the
property **always**, defaulting to overlay, and a fresh install no longer lands in
the one configuration that dies. `VideoPathLine` still distinguishes *our* default
from a chosen one, because a report has to say which it is looking at.

**In-page mode fails on one assertion, and always the same one.** Eighty-eight
attempts in nine minutes, no successes:

```
gst_video_overlay_set_render_rectangle:
  assertion '(width == -1 && height == -1) || (width > 0 && height > 0)' failed
```

So "in page" on this engine is not compositing frames into the page's texture at all
— it is a hole punch like the overlay, positioned per element instead of over the
whole window, and the rectangle it is handed has a width or height of zero or less.
The video decodes perfectly throughout (`rs4`, correct dimensions, audio playing) and
is drawn into a rectangle of no size. **That is the black screen** — a geometry
handoff, not a decode failure. It is also why overlay works: overlay passes no
rectangle at all, which is the `-1, -1` whole-screen branch, the one branch that
satisfies that assertion. Overlay is not succeeding at what in-page fails; it is
skipping it.

#### Why the obvious fix was not shipped with the finding

The guess writes itself: Instagram's reel carousel is transformed, clipped and
scroll-snapped, so flatten the video's box and the rectangle comes back. It was not
shipped, and the reason is the count. **Eighty-eight failures, zero successes, across
reels of four different intrinsic sizes.** A cause in the page's own layout would
succeed *sometimes*. A cause in the hosting fails exactly this way — always,
everywhere, whatever the page does — and NUI composites the web view as a texture,
with no native window of its own for the engine to ask about, which is precisely the
shape that would make every per-element rectangle collapse while the whole-window one
still works.

So `NuiVideoRect` measures instead: what box the page thinks the playing video has,
its intrinsic size, its computed visibility, how many ancestors carry a transform,
a clip or a non-visible overflow, how many of them are themselves zero-sized, and
the dpr and visual-viewport mapping the engine would have to scale through. One line
on the trail and one `video rect :` line in the report, and it decides the question
outright:

- **A good box — `340x600`, `tf 0`, no zero parents — while the sink is still
  refused.** The rectangle is lost between the page and the sink, inside the engine's
  own hosting. Nothing on this side of the wall reaches it: in-page video is finished
  on that set and hardware overlay is what Overscan has to offer it.
- **A genuinely zero or collapsed box.** Flattening it is worth the next build, and
  now there is a measurement saying which ancestor to flatten.

`tools/videorect/run.sh` holds it to being read-only, which is the whole point of it.
The page marks every call the probe must not make — `pause`, `load`, `play`, a
changed `src`, a changed DOM — and the run fails if any of them happens. This issue
has already had one build that changed a page on a guess and cost the reporter his
one working configuration; a probe sent to explain a black screen must not be able to
cause one.

#### The box is fine, so in-page video is finished on that set

`build-5490157`'s in-page report is the answer, and it is the first of the two
readings above:

```
video rect: box 666,16 588x1048 | off 588x1048 | client 588x1048
            | intrinsic 540x960 | vis ok
            | tf 0 clip 0 ovf 3 zeroparents 0 | dpr 1 vv 0,0 1920x1080 scale 1
```

A 588×1048 box at 666,16 — a sensible portrait reel, centred on a 1920×1080 screen.
`getBoundingClientRect`, `offsetWidth/Height` and `clientWidth/Height` all agree, so
it is not a fractional rectangle rounding to nothing. Nothing is transformed, nothing
is clipped, no ancestor is itself zero-sized, the device pixel ratio is 1 and the
visual viewport maps 1:1. Six samples across the run, at four different intrinsic
sizes — 540×960, 720×720, 1080×1080, 1080×1920 — and every one of them is healthy.
The `ovf 3` is Instagram's own scroll containers and is the only thing the page does
that the flattening theory would have reached for; it does not collapse anything.

**So the page hands the engine a good rectangle and the sink is still refused.** The
geometry is lost between the two, inside NUI's own hosting of the web view — which is
exactly the shape predicted above, and for the reason predicted: NUI composites the
engine as a texture, with no native window for it to place an overlay against, so
every *per-element* rectangle collapses while the *whole-window* one keeps working.
Nothing in managed code sits on that path. **In-page video is finished on the 2025
sets, and hardware overlay is what Overscan has to offer them.**

Two things follow that are worth as much as the answer:

- **The flattening build was right not to ship.** It would have rewritten Instagram's
  layout to fix a rectangle that was never wrong, on a set whose one working
  configuration a page-changing build had already broken once.
- **The key `5` menu keeps in-page anyway.** One set is one set: the reasoning says
  every NUI host does this, but the measurement covers a single Tizen 10 TV, and the
  cost of leaving a broken option in a menu that already defaults to overlay is a
  reporter pressing `5` twice. Removing it would spend the finding on a set we have
  not heard from.

`NuiVideoRect` stays in the build. Its question is answered for this set, but it is
read-only, it costs a line every few seconds, and it is what a second 2025 set would
have to report before any of the above is claimed of anything but this one TV.

#### There is no second lever, and this is the check that says so

"Finished" is a strong word for a conclusion drawn from a mechanism rather than a
measurement, so before it went out on the issue the managed surface was enumerated
rather than remembered. `Samsung.Tizen.Ref` 9.0.104, reflected over
`Tizen.NUI.BaseComponents.WebView`, `Tizen.NUI.WebSettings` and the `Tizen.NUI.Interop`
layer under both:

- **`VideoHoleEnabled` (bool) is the entire video-path API.** There is no
  render-rectangle setter, no surface or window handle to hand the view, and nothing
  geometry-related beyond `ScrollPosition`, `ScrollSize` and `ContentSize`. The interop
  layer exposes nothing the public class hides — no `Window`, `Surface`, `Geometry` or
  `Rect` entry point exists to P/Invoke at, so the trick used for DALi's binder and the
  engine's loader has nothing to reach for here.
- **The engine's own escape hatch exists, and it is the wrong kind of switch.**
  `WebSettings.EnableExtraFeature(string, bool)` is chromium-efl's
  `ewk_settings_extra_feature_set`, it is public (if `EditorBrowsable(Never)`), and this
  app has never called it. It looked like the one unexplored door. It is not one:
  the implementation is a fixed table matched by `strcasecmp`, and the table is browser
  *UI* toggles — `longpress,enable`, `link,magnifier`, `detect,contents`, `web,login`,
  `doubletap,enable`, `zoom,enable`, `openpanel,enable`, `allow,restrictedurl`,
  `urlbar,hide`. Nothing about video, graphics, compositing or geometry, and an unknown
  name is silently dropped by `EINA_SAFETY_ON_NULL_RETURN`. The video path is not
  configured through that door at all — it has its own dedicated API, which is the one
  we already set.

That table is read from the Chromium 40 tree (`crosswalk-project/chromium-efl`), and
the set runs Chromium 130, so Samsung will have added rows to it. What does not change
is the *shape*: booleans on `Ewk_Settings`, in a family that has never held a video
concern, next to a video-hole API that has its own function. Probing the current
firmware's table is a legitimate thing to do one day — write a name, read it back, and
the ones that stick are the ones that build knows — but it is a capability inventory,
not a route to the render rectangle, and it does not justify an install of its own.

So the answer to *is there anything left to try* is no, and it is no on an enumeration
rather than on a feeling. If that is ever revisited, revisit it with a second 2025 set
reporting the same `video rect :` line, not with another switch.

#### `holding` counted sweeps, not videos

The same trail carries a smaller thing that had been quietly spoiling every report
before it. `NuiVideoCap`'s line read `held 342` on a page with eight `<video>`
elements, climbing by four every two seconds, and that number went out on issue #20
twice as though it meant elements — `it should now say held 9 or similar` was written
about a count of three blob reels seen three times.

`released` and `restored` are running totals, correctly: each is a thing that happens
once to one element. `held` was incremented in the same place while counting a
*condition* — this element has no restorable source — which is true of the same
element on every single sweep forever. Worse than the wrong number: a total that
climbs on every sweep can never equal the previous line, so the

```js
if (line !== lastReport) { lastReport = line; report(line); }
```

deduplication under it never once fired on a reel feed. Better than two hundred
identical `video cap:` breadcrumbs in a seven-minute trail, on the one file the whole
diagnostic design depends on being readable when a process dies without saying why.

It is reset at the top of each sweep and the line now reads `holding N`. Two checks
in `tools/videocap/run.sh` hold it there — that the count does not grow while the
page does not change, and that a sweep over an unchanged page writes nothing at all —
and both fail against the old code.

### What is left on the 2025 sets

Issue #20's reporter is on a Tizen 10 set running the NUI package, and is the one
person testing that half of this app in anger. As of 2026-09-04, after his
eleventh report — issue #53, the black screen at every launch, which he had
already sent once on #20 on 2026-08-30 and which went unanswered for four days —
the state is:

- **The session not surviving a restart — fixed.** Shipped in `build-9d856d1`.
  Nothing pending.
- **The heartbeat — fixed, and the report proves it.** `memory:` lines every five
  seconds from start-up to the last breath of the run, where before they stopped at
  the first page load. Everything `OnTick` owns is running again.
- **Reels crashing the app — it is the engine, and it is the *engine-default*
  video path specifically.** The segfault lands in chromium's render thread, so it
  cannot be caught from managed code and it was never ours. What changed with
  `build-e78c0bc` is which configuration owns it: the untouched engine sends video
  to a `fakesink` and dies on the fourth decoder, while the in-page path built
  twenty-three decoders in nine minutes without one. So it is not a decoder budget
  and capping the count would not have helped. See *It is not the count — it is
  which sink the path uses* above. **Fixed as far as a fresh install is concerned:**
  `VideoHoleEnabled` is now always set, defaulting to overlay, so nobody lands in
  the crashing configuration by default. Shipped in `build-5490157`.
  **`NuiVideoCap` remains a no-op on this feed** and `build-3368aea` proved twice
  over why it must stay one — see *Releasing a source we cannot restore* above.
- **Reels playing black in the in-page path — answered, and the answer is that it
  is not reachable from here. Closed; nothing pending.** `build-5490157` reported
  `box 666,16 588x1048 | off 588x1048 | client 588x1048 | vis ok | tf 0 clip 0
  zeroparents 0`, six times, across four different intrinsic sizes. The page hands
  the engine a healthy rectangle and the sink is refused anyway, so the geometry is
  lost inside NUI's hosting of the web view, where no managed code reaches. **In-page
  video is finished on the 2025 sets and hardware overlay is what Overscan offers
  them.** The flattening build that was held back would have rewritten a page to fix
  a box that was never wrong. `5` keeps the in-page option regardless — one set is
  one set. See *The box is fine, so in-page video is finished on that set* above.
- **The settings being wiped was reinstalls, not a bug.** Every build had to be
  reinstalled because TizenBrew's installer rejected the author certificate; he is
  on the Apps2Samsung installer now. So a `0 settings` header is not evidence of
  anything, and `5` being lost each time is expected.
- **In-app video shows a black screen; overlay plays reels.** Consistent with what
  is settled below — the overlay path is the only one that gives him a picture — and
  now explained rather than merely observed. `build-3368aea`'s regression, which had
  turned overlay itself into a blank screen after one or two reels, is confirmed
  fixed by his report on `build-e78c0bc`: `released 0, restored 0`. (The `held 249`
  on that same line meant nothing — see *`holding` counted sweeps, not videos*
  above. `released 0` was always the half that carried the confirmation.)
- **Reels no longer end the app at all, on the setting he uses.** `build-5490157`'s
  overlay run scrolled reels for seven minutes and ended `OnPause` then `OnTerminate
  — closing normally`: he closed it. No `SIGTERM`, no unhandled exception, no trail
  that simply stops. The one thing the run does show is
  `PLATFORM SAYS MEMORY IS LOW: HardWarning` four times, each recovering to `None`
  within a second while resident memory sat at 60–76 MB — so the set is under
  pressure from something that is not us, and it did not act on it. Worth knowing
  the next time an eviction is suspected; not worth a build on its own.
- **The trail was drowning itself.** That same run wrote better than two hundred
  `video cap:` breadcrumbs, one every two seconds, because a miscounted total could
  never match the previous line and so was never deduplicated. Fixed; the count now
  means elements. This mattered more than a cosmetic tidy: the trail is the only
  evidence a death leaves, and it was two-thirds noise on the runs it exists for.
- **A black screen at every launch — found, and fixed in the build cut from the
  start-screen change (see *The start screen was eating itself* above).** It was
  never the view: the start screen was recording itself into history on every
  launch and outgrew Chromium's 2 MB URL ceiling on about the ninth. The fix
  heals his existing `history.tsv` on first launch, so no reinstall. **Waiting
  on:** his report from that build showing `start page:` at a few KB and a
  `store: dropped N generated page(s)` line, and the start screen opening. If it
  still does not open with the store healed, `blank view:` now says whether a
  *bare* start screen loaded, which separates the page from the view for good.
- **The captcha — answered: it works.** His 2026-08-30 comment: "captcha works
  now tried on instagram, spotify site". That is the thing #20 is named for.
- **The address bar still shows when scrolling with the channel rocker and when
  unmuting** — same comment. Open, small, not started: `build-5490157` took the
  bar off pointer keys, and the rocker and the volume/mute keys are evidently not
  in that set.
- **"A frame at the top" — it is our own address bar, and he has now said it is in
  the way.** It was shown on *every* key down, cursor moves included, each repeat
  re-arming its own four-second idle timer, so it was on screen the whole time
  anyone was using the browser. Pointer keys no longer show it; an open menu now
  counts as busy so the bar cannot time out underneath one. Shipped in
  `build-5490157`.
- **The pointer skipping small targets — fixed.** The smallest step was 2% of the
  viewport, 38px across on a 1080p set, which can straddle Instagram's mute button
  or the ✕ on a reel whatever it starts from. It is 0.7% now, about 13px, with
  faster acceleration so crossing the screen still takes about eight repeats.
  Shipped in `build-5490157`.
- **The captcha itself — the thing the issue is named for — may already be
  solved, and nobody has said so either way.** `build-5490157`'s in-page trail has
  him on `instagram.com/auth_platform/recaptcha/`, clicking ten times into a
  cross-origin `IFRAME` at coordinates that walk a grid and finish at 973,605, and
  then loading `instagram.com/accounts/onetap/` seven seconds later — the page
  Instagram shows *after* a successful login. That is what solving an image
  challenge looks like from out here. **Waiting on:** him saying plainly whether the
  captcha passed. It has never been asked directly, because until now there was
  always a crash in front of it.
  The `frame saw : trusted=none frame=none` on the same report is **not** evidence
  against it, and the witness's own docstring is what says so: a click that lands
  inside an out-of-process frame is invisible to the parent document by design, and
  Chromium's site isolation need not fire `focusin` on the frame element either. So
  `none/none` covers both "it never arrived" and "it arrived and went where it was
  meant to". The witness discriminates for the *native touch* feed it was written
  for; for the CDP path it is inconclusive, and it should not be read as a failure
  again. `tools/cdpharness/run.sh` is the direct demonstration: it clicks into a
  genuinely cross-site frame and the **frame itself** reports back
  `trusted=true&x=130&y=50` — a real click, delivered — on a run where the parent
  page has no way to know it. That is the same shape as the TV's `none/none`.
- **Scrolling with the channel rocker — asked for, and it already existed.**
  Documented; the general problem is #38.
- **Ad blocking — accepted in principle, not started. Proposal in #37.** The NUI
  WebView can refuse a request before it goes out (`RequestIntercepted`,
  `WebHttpRequestInterceptor`, `WebContext` are all in `Samsung.Tizen.Ref` 9.0.104),
  so this is real blocking on the 2025 sets and structurally impossible on the four
  `Tizen.WebView` packages, which have no such hook. Two constraints are decided and
  should not be relitigated: it ships as the **only** new thing in its build, because
  the reporter is the sole tester of that half of the app and a mixed build makes
  "slower" or "site broken" unattributable; and the handler timing goes in the
  diagnostics report from the first version, because the check sits in the path of
  every request on TV hardware.

Five things about that set are settled and should not be re-derived: **key `5` is
his, not ours** — the engine's overlay path is the only one that gives him a
picture, so a report of black or silent video is the in-page path failing and not a
regression — **the video path has nothing to do with a view that will not
navigate**, which cost a build to learn, **the reels death is not the app growing
too large**, which cost two, **it is not a decoder budget either**, which cost
a whole class that does nothing on the feed it was written for, and **a start
screen that will not load is not a view that will not load** — his view has
navigated at the first ask on every trail where he typed an address, and the
ladder reaching its last rung is not evidence that the set cannot run Overscan.

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
