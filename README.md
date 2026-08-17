<img src="docs/img/overscan.png" alt="" width="104" align="right">

# Overscan

A web browser you can sideload onto a Samsung TV — the one the TV should have
shipped with.

*Overscan* is the picture area a TV pushes past the visible edge, which is roughly
what this does to the web on a television.

The built-in Samsung browser tells websites it's a TV, so sites hand it a stripped
down "smart TV" page instead of the real one. Overscan wraps the TV's own web
engine in an app that behaves like a desktop browser:

- **Sites load their normal desktop version**, because the browser identifies
  itself as desktop Chrome.
- **JavaScript stays on**, so pages aren't broken or half-rendered.
- **The remote works like a mouse** — the D-pad moves a pointer, OK clicks.
- **Favourites and history**, on a start screen built for a TV.
- **You can type** with an on-screen keyboard driven by the D-pad, into the
  address bar *and* into search boxes on the page. QWERTY, AZERTY, QWERTZ or
  alphabetical, whichever you're quickest on.

Requested in [tizen-community-packages#24](https://github.com/Apps2Samsung/tizen-community-packages/issues/24).

## Will it work on my TV?

| Your TV | Download | Notes |
| --- | --- | --- |
| 2018 (Tizen 4.0) | `Overscan-tizen4.tpk` | The oldest TVs that can run this. Chromium 56, so expect real limits. Untested. |
| 2019–2020 (Tizen 5.0–5.5) | `Overscan-tizen5.tpk` | Needs a **partner certificate** to sign — see below. Old engine, so expect some sites to misbehave. |
| 2021–2024 (Tizen 6.0–8.0) | `Overscan-tizen8.tpk` | Not yet tested on hardware. |
| 2025+ (Tizen 9.0–10.0) | `Overscan-nui.tpk` | Tested on the Tizen 10 emulator. Modern engine — sites generally just work. |

**2017 and older sets (Tizen 3.0, 2.x) cannot run Overscan.** Samsung only added
.NET to TVs with the 2018 range, so there is no runtime for the app to start in —
this is a platform limit, not something a build can work around.

Grab the packages from the [latest release](https://github.com/Apps2Samsung/Overscan/releases).
Not sure which platform your set is? `sdb capability` prints `platform_version`.

## Installing

Sideloading a TV app means signing the package with your own certificate and
pushing it over the network. If you've done this before, nothing here is unusual;
if you haven't, [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) does
the signing and installing for you.

1. Put the TV in Developer Mode and note its IP address.
2. Sign the `.tpk` with your certificate. The packages in releases are
   default-signed, which no TV accepts.
3. Install it, and launch Overscan from the TV's Apps list.

**On a 2019/2020 set the package must be signed with a partner-level
certificate.** That isn't a preference — the TV's web engine is bolted to its DRM
stack, and without a partner certificate the app is not allowed to load it, so the
browser simply won't start. Newer sets don't have this restriction.

## Using the remote

| Key | What it does |
| --- | --- |
| **D-pad** | Move the pointer (speeds up while held) |
| **OK** | Click. On a text box, opens the keyboard for it |
| **Back** | Close overlay → back a page → exit |
| **CH ▲ / ▼** | Page up / page down |
| **0** | Address bar |
| **1** | Switch how the browser identifies itself, and reload |
| **2** | Pointer style |
| **3** | Diagnostics (what the page thinks it's talking to) |
| **4** | Give the keys to the page, for its own navigation |
| **5** | Type into the text box you last clicked |
| **6** | Page-fit correction (for stretched rendering) |
| **7** | Show/hide the key hints |
| **8** | Keep this page in favourites (press again to remove) |
| **9** | Back to the start screen |
| **Info** | Images on/off — the biggest speed-up on an old set |

The last key on the keyboard's bottom row switches its layout — **QWERTY**
(the default), then AZERTY, QWERTZ and the alphabetical grid. It shows the layout
you're on, and the choice is remembered.

## Does it load pages faster?

No — and on a slow TV it can be slower. Overscan uses the TV's own web engine, so
the rendering speed is exactly the same; what changes is *which version of a site*
you get, and the desktop version is usually the heaviest one. If YouTube takes two
minutes in the built-in browser, the desktop YouTube will not be quicker.

What does help on an older set:

- **Press `Info` to turn images off.** The engine can't be made faster, but it can
  be given far less to do, and this is the biggest single win.
- **Press `1` to pick the mobile version** of a site. Mobile pages are much lighter
  than desktop ones and often render better on an old engine.
- **Use a site's TV interface where one exists** — `youtube.com/tv` is built for
  exactly this hardware and is dramatically faster than the desktop site.

## Known limits

- **The engine is the TV's, not ours.** An app can't ship its own Chromium, so on
  a 2019 set you're browsing with a 2017 engine (Chromium 63) no matter what.
  Overscan fixes how the TV *identifies* itself; it can't make an old engine
  understand a modern site. On a 2025 set the engine is Chromium 130 and this
  caveat mostly disappears.
- **Rendering is stretched on Tizen 5.0** — the engine lays pages out at one size
  and paints them at another. Key `6` toggles a correction; this is
  [an open bug](https://github.com/Apps2Samsung/Overscan/issues).
- **No downloads or file uploads** yet.
- The TV's own on-screen keyboard can still flash up briefly on sites that focus
  their search box as soon as they load.

## Building it yourself

```bash
./build.sh          # all packages -> dist/
./build.sh tizen5   # just the 2019/2020 one
```

You'll need the .NET 6 SDK with the `tizen` workload, plus the .NET Core 3.1 SDK
for the older package. CI builds all four on every push — see
[`.github/workflows/build.yml`](.github/workflows/build.yml).

## How it works, briefly

Overscan is a .NET app that hosts the TV's own web engine and adds the things a TV
browser needs: a pointer driven from the remote, an on-screen keyboard, and a
user-agent override. The pointer and typing are implemented as JavaScript injected
into each page, because the TV's engine gives an app no way to synthesise mouse
input.

Because the web-view API changed twice across TV generations, there are three
builds from one shared source tree rather than one universal package.

The full story — API archaeology, the DRM permission wall, the diagnostics built
for TVs that can't be logged, emulator setup — is in
[`docs/INTERNALS.md`](docs/INTERNALS.md).

## Licence

MIT — see [LICENSE](LICENSE).
