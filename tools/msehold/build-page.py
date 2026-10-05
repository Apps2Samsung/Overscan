#!/usr/bin/env python3
"""Builds the test page around the one-video hold as it is actually shipped.

The script is lifted out of src/nui/NuiMseHold.cs, with the one C# interpolation
substituted the way the compiler would. Keeping a copy here instead would test the
copy.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "src", "nui", "NuiMseHold.cs")


def script():
    text = open(SOURCE, encoding="utf-8").read()
    body = re.search(r'return @"\n(.*?)\n";\s*\n\s*\}', text, re.S)
    if not body:
        sys.exit("could not find the verbatim script in NuiMseHold.cs")

    js = body.group(1)
    js = js.replace('" + Prefix + @"', "__ovs hold: ")
    return js.replace('""', '"')


# Every element here is a real <video> with a real MediaSource. "opened" means
# chromium fired sourceopen on it, which only happens once the element has been
# given the source. Element A plays (muted, no data, so `paused` is false and
# stays false) and is the "another video is playing" of every held case.
PAGE = """<!doctype html><html><head><meta charset=utf-8></head><body>
<div id=out>running</div>
<script>
var logged = [];
console.log = (function(inner){
  return function(line){ logged.push(String(line)); try{ inner.apply(console, arguments); }catch(e){} };
})(console.log);

var fails = [];
function fail(m){ fails.push('FAIL ' + m); }
var info = [];

__SCRIPT__

function video(id){
  var v = document.createElement('video');
  v.id = id; v.muted = true;
  document.body.appendChild(v);
  return v;
}
function source(){
  var ms = new MediaSource();
  var r = { ms: ms, url: URL.createObjectURL(ms), opened: false };
  ms.addEventListener('sourceopen', function(){ r.opened = true; });
  return r;
}
function has(re){ return hold.some(function(l){ return re.test(l); }); }
var hold = [];
function lines(){
  hold = logged.filter(function(l){ return l.indexOf('__ovs hold: ') === 0; })
               .map(function(l){ return l.substring(12); });
}

/* A: the reel on screen. */
var a = video('a'), A = source();
a.src = A.url;
var pa = a.play();
if (!pa || typeof pa.then !== 'function') { fail('play() no longer returns a promise'); }
if (a.paused) { fail('the harness could not get element a playing (autoplay policy?)'); }

/* B: the next reel, readied while A plays. Held. */
var b = video('b'), B = source();
b.src = B.url;
if (b.src !== B.url) { fail('held element does not read its src back: ' + b.src); }
if (b.getAttribute('src') !== B.url) { fail('held element does not read its src attribute back'); }
if (b.currentSrc !== B.url) { fail('held element does not read its currentSrc back: ' + JSON.stringify(b.currentSrc)); }

/* Q: held while A plays and never played by the page, so that at phase 3, with
   nothing playing any more, it is the held element the gap rule keys on. */
var q = video('q'), Q = source();
q.src = Q.url;

/* C: autoplay. The engine starts this one itself, so it must not be held. */
var c = video('c'), C = source();
c.autoplay = true;
c.src = C.url;

/* E: held, then the page takes the source away. Dropped; must never open. */
var e = video('e'), E = source();
e.src = E.url;
e.removeAttribute('src');

/* F: held, and the page revokes the URL a tick later, which with nothing held
   finds the engine's lookup already done and works. Under the hold the lookup has
   not happened yet, so the revoke has to wait for it. */
var f = video('f'), F = source();
f.src = F.url;
setTimeout(function(){ URL.revokeObjectURL(F.url); }, 100);

/* G: set through setAttribute rather than the property. Held the same way. */
var g = video('g'), G = source();
g.setAttribute('src', G.url);

/* H: a new source on a held element replaces the hold; only the new one may open. */
var h = video('h'), H1 = source(), H2 = source();
h.src = H1.url;
h.src = H2.url;

/* S: a srcObject (a MediaStream here; a worker's MediaSource arrives the same
   way). Held: no loadstart until play(). */
var s = video('s'), stream = null, sLoad = 0;
try { stream = document.createElement('canvas').captureStream(); } catch (err) {}
if (stream) {
  s.addEventListener('loadstart', function(){ sLoad++; });
  s.srcObject = stream;
  if (s.srcObject !== stream) { fail('held element does not read its srcObject back'); }
}

var phase2 = {}, phase3 = {};

setTimeout(function(){
  /* Phase 2: nothing has been played but A. */
  phase2.A = A.opened; phase2.B = B.opened; phase2.C = C.opened; phase2.E = E.opened;
  phase2.F = F.opened; phase2.G = G.opened; phase2.H1 = H1.opened; phase2.H2 = H2.opened;
  phase2.sLoad = sLoad;

  /* The releases. */
  b.play(); f.play(); g.play(); h.play(); e.play();
  if (stream) { s.play(); }
}, 3000);

setTimeout(function(){
  /* Phase 3: the releases have had time to open. */
  phase3.B = B.opened; phase3.E = E.opened; phase3.F = F.opened; phase3.G = G.opened;
  phase3.H1 = H1.opened; phase3.H2 = H2.opened; phase3.sLoad = sLoad;

  a.pause(); b.pause(); f.pause(); g.pause(); h.pause(); e.pause(); if (stream) { s.pause(); }

  /* R: the gap. Nothing plays, but Q is still held, so a source set now is the
     reel after the one about to play (TikTok's tick between pausing a reel and
     playing the live stream, 2026-10-05) and must be held too. */
  phase3.Qopened = Q.opened;
  var r = video('r'), R = source();
  r.src = R.url;
  phase3.Rearly = R.opened;
  window.R = R;
  /* The page then takes Q's source away and plays R: R opens, and once R is
     paused again nothing is playing and nothing is held. */
  q.removeAttribute('src');
  r.play();
  r.pause();
  window.Q = Q;

  /* D: with nothing else playing and nothing held, a source set on a paused
     element is untouched. */
  var d = video('d'), D = source();
  d.src = D.url;
  window.D = D;

  /* Control, for information only: does chromium itself survive a revoke right
     after the set, with nothing held? Says whether F's case is one real players
     can be in. */
  var k = video('k'), K = source();
  k.src = K.url;
  URL.revokeObjectURL(K.url);
  window.K = K;
  /* And the shape F stands for, with nothing held: this one must open, or F is
     holding the script to something chromium itself cannot do. */
  var l = video('l'), L = source();
  l.src = L.url;
  setTimeout(function(){ URL.revokeObjectURL(L.url); }, 100);
  window.L = L;
}, 6000);

function check(){
  lines();

  if (!phase2.A) { fail('element a never opened: the harness is not driving a real MediaSource'); }
  if (phase2.B) { fail('b opened while a was playing: not held'); }
  if (!has(/^hold v\\d blob while v\\d plays$/)) { fail('hold not reported'); }
  if (!phase2.C) { fail('autoplay element c was held'); }
  if (phase2.E) { fail('e opened although its source was removed'); }
  if (!has(/^drop v\\d after \\d+\\.\\ds \\(src removed\\)$/)) { fail('drop on src removed not reported'); }
  if (phase2.F || phase2.G || phase2.H1 || phase2.H2) { fail('a held element opened early: F ' + phase2.F + ' G ' + phase2.G + ' H1 ' + phase2.H1 + ' H2 ' + phase2.H2); }
  if (!has(/^drop v\\d after \\d+\\.\\ds \\(src set again\\)$/)) { fail('replacing a held source not reported as a drop'); }
  if (stream && phase2.sLoad !== 0) { fail('srcObject element started loading while held'); }

  if (!phase3.B) { fail('b did not open after play()'); }
  if (!has(/^release v\\d after \\d+\\.\\ds \\(play\\)$/)) { fail('release not reported'); }
  if (phase3.E) { fail('e opened after play() although its source had been dropped'); }
  if (!phase3.F) { fail('f did not open after play(): the revoke of its held URL was not deferred past the lookup'); }
  if (!phase3.G) { fail('g (setAttribute) did not open after play()'); }
  if (phase3.H1) { fail('the replaced source on h opened'); }
  if (!phase3.H2) { fail('the replacing source on h did not open after play()'); }
  if (stream && phase3.sLoad < 1) { fail('srcObject element did not start loading after play()'); }
  if (has(/FAILED/)) { fail('a release failed'); }

  if (phase3.Qopened) { fail('q opened although the page never played it'); }
  if (phase3.Rearly) { fail('r opened at once: a source set while another element is held was not held'); }
  if (!has(/^hold v\\d blob while v\\d is held$/)) { fail('the gap hold not reported with its reason'); }
  if (!window.R.opened) { fail('r did not open after play()'); }
  if (window.Q.opened) { fail('q opened after its source was removed'); }
  if (!window.D.opened) { fail('d was held with nothing playing and nothing held'); }
  if (!window.L.opened) { fail('with nothing held, a source revoked a tick after the set did not open: F tests the wrong thing'); }
  info.push('control: chromium opens a source revoked right after the set, nothing held: ' + (window.K.opened ? 'yes' : 'no'));

  document.getElementById('out').innerHTML =
    '<div id=RESULTS>RESULTS\\n' + (fails.length ? fails.join('\\n') : 'ok') +
    '\\n--- ' + info.join('\\n') +
    '\\n--- lines\\n' + hold.join('\\n').replace(/</g, '&lt;') + '\\n</div>';
}

setTimeout(check, 9500);
</script></body></html>
"""


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: build-page.py <out.html>")

    open(sys.argv[1], "w", encoding="utf-8").write(
        PAGE.replace("__SCRIPT__", script()))


if __name__ == "__main__":
    main()
