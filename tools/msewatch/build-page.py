#!/usr/bin/env python3
"""Builds the test page around the MSE probe as it is actually shipped.

The script is lifted out of src/nui/NuiMseWatch.cs, with the one C# interpolation
substituted the way the compiler would. Keeping a copy here instead would test the
copy.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "src", "nui", "NuiMseWatch.cs")


def script():
    text = open(SOURCE, encoding="utf-8").read()
    body = re.search(r'return @"\n(.*?)\n";\s*\n\s*\}', text, re.S)
    if not body:
        sys.exit("could not find the verbatim script in NuiMseWatch.cs")

    js = body.group(1)
    js = js.replace('" + Prefix + @"', "__ovs mse: ")
    return js.replace('""', '"')


# A real MediaSource on a real <video>, fed segments chromium's MP4 parser will
# accept without a picture: an ftyp, then `free` boxes it skips. The probe reads the
# sample entry it finds inside the free box the way it would read one inside a moov,
# so the codec and size it reports are the ones written here. The stall half uses a
# stub element, because a frozen playhead on a decoding video is not something a
# headless chrome can be made to produce on demand.
PAGE = """<!doctype html><html><head><meta charset=utf-8></head><body>
<div id=out>running</div>
<video id=real></video>
<script>
var logged = [];
console.log = (function(inner){
  return function(line){ logged.push(String(line)); try{ inner.apply(console, arguments); }catch(e){} };
})(console.log);

var fails = [];
function fail(m){ fails.push('FAIL ' + m); }

/* What the engine answers before the probe is in, to hold the wrapper to the same. */
var MIMES = ['video/mp4; codecs="avc1.42E01E"', 'video/mp4; codecs="hvc1.1.6.L93.B0"',
             'video/webm; codecs="vp9"', 'audio/mp4; codecs="mp4a.40.2"', 'video/x-nope'];
var before = MIMES.map(function(m){ return MediaSource.isTypeSupported(m); });

__SCRIPT__

var after = MIMES.map(function(m){ return MediaSource.isTypeSupported(m); });
if (before.join() !== after.join()) { fail('isTypeSupported answers changed: ' + before + ' -> ' + after); }

function box(type, payload){
  var b = new Uint8Array(8 + payload.length);
  var n = b.length;
  b[0] = n >>> 24; b[1] = (n >>> 16) & 255; b[2] = (n >>> 8) & 255; b[3] = n & 255;
  for (var i = 0; i < 4; i++) { b[4 + i] = type.charCodeAt(i); }
  b.set(payload, 8);
  return b;
}
function cat(a, b){ var c = new Uint8Array(a.length + b.length); c.set(a); c.set(b, a.length); return c; }
function entry(codec, w, h){
  /* A sample entry: 6 reserved, data ref, 16 bytes of pre-defined/reserved, width, height. */
  var p = new Uint8Array(28);
  p[24] = w >> 8; p[25] = w & 255; p[26] = h >> 8; p[27] = h & 255;
  return box(codec, p);
}
function init(codec, w, h){
  /* `avc1` in the compatible brands, the way TikTok's audio init carries it: the
     probe must not read a sample entry out of the brand list. */
  var ftyp = box('ftyp', new Uint8Array([105,115,111,109, 0,0,2,0, 105,115,111,109, 97,118,99,49]));
  return cat(ftyp, box('free', entry(codec, w, h)));
}

var real = document.getElementById('real');
['pause','play','load'].forEach(function(f){ real[f] = function(){ real.__touched = f; }; });

/* A second element on a plain URL, the shape of the stalls build-c05e9b9 could not
   name: it must be named, its source written as a URL, and left alone. */
var plain = document.createElement('video');
plain.id = 'plain';
plain.preload = 'auto';
['pause','play','load'].forEach(function(f){ plain[f] = function(){ plain.__touched = f; }; });
document.body.appendChild(plain);
plain.src = 'https://example.invalid/video/reel.mp4';

/* An <audio> element on a plain URL: the 2026-10-03 trail's pipeline had an audio
   decoder and no video decoder, and the probe had been listening to <video> only.
   It must be named from the same namer, marked, and left alone. */
var aud = document.createElement('audio');
aud.id = 'aud';
aud.preload = 'auto';
['pause','play','load'].forEach(function(f){ aud[f] = function(){ aud.__touched = f; }; });
document.body.appendChild(aud);
aud.src = 'https://example.invalid/audio/sound.mp3';

/* A <video> that is never put in the document. Its events reach no listener on the
   document and no DOM walk finds it, so the probe hears of it only from the
   prototype: the src set and the load() must each leave a line saying `detached`,
   the setter and load() must pass through, a refused srcObject must throw the
   engine's own error and leave no line, and a stall must still list it. */
var det = document.createElement('video');
det.src = 'https://example.invalid/detached/reel.mp4';
if (det.src !== 'https://example.invalid/detached/reel.mp4') { fail('src setter changed what src reads back: ' + det.src); }
if (det.getAttribute('src') !== 'https://example.invalid/detached/reel.mp4') { fail('src setter did not reach the attribute'); }
det.load();
try {
  det.srcObject = {};
  fail('srcObject accepted a plain object');
} catch (e) {
  if (e.name !== 'TypeError') { fail('srcObject threw ' + e.name + ', not TypeError'); }
}

/* The three starts the 2026-10-05 trail showed the probe could not see. An
   element whose source arrives through setAttribute while it is outside the
   document: the property setter never runs, so the attribute wrap is the one line
   it leaves, and the attribute and the property must both read back. */
var attr = document.createElement('video');
attr.setAttribute('src', 'https://example.invalid/attr/reel.mp4');
if (attr.getAttribute('src') !== 'https://example.invalid/attr/reel.mp4') { fail('setAttribute wrap did not reach the attribute'); }
if (attr.src !== 'https://example.invalid/attr/reel.mp4') { fail('after setAttribute, src does not read back: ' + attr.src); }

/* new Audio(url): the constructor sets the source inside the engine, no setter, no
   setAttribute, nothing in the document. It must still make the engine's own
   element, with and without an argument. */
var ctor = new Audio('https://example.invalid/ctor/sound.mp3');
if (!(ctor instanceof HTMLAudioElement)) { fail('new Audio(url) no longer makes an HTMLAudioElement'); }
if (Audio.prototype !== HTMLAudioElement.prototype) { fail('Audio.prototype changed'); }
if (ctor.src !== 'https://example.invalid/ctor/sound.mp3') { fail('new Audio(url) lost its src: ' + ctor.src); }
var bare = new Audio();
if (!(bare instanceof HTMLAudioElement) || bare.getAttribute('src') !== null) { fail('new Audio() with no argument changed'); }

/* play() on a detached element, the one call even a new Audio has to make. The
   promise is the engine's own and rejects on the engine's terms (no source it
   can load); the wrap must hand it back untouched. */
var pr = det.play();
if (!pr || typeof pr.then !== 'function') { fail('play() no longer returns a promise'); }
pr.catch(function(){});
var pr2 = ctor.play();
if (!pr2 || typeof pr2.then !== 'function') { fail('play() on a new Audio no longer returns a promise'); }
pr2.catch(function(){});

/* Two frames, one ours and one not: the scene line has to tell them apart. */
var same = document.createElement('iframe');
same.src = 'about:blank';
document.body.appendChild(same);
var other = document.createElement('iframe');
other.src = 'https://example.invalid/frame/';
document.body.appendChild(other);

var ms = new MediaSource();
var url = URL.createObjectURL(ms);
if (typeof url !== 'string' || url.indexOf('blob:') !== 0) { fail('createObjectURL returned ' + url); }
real.src = url;

var mseDone = false;
ms.addEventListener('sourceopen', function(){
  try {
    ms.addSourceBuffer('video/x-nope');
    fail('unsupported addSourceBuffer did not throw');
  } catch (e) {
    if (e.name !== 'NotSupportedError') { fail('addSourceBuffer threw ' + e.name + ', not NotSupportedError'); }
  }

  var sb = ms.addSourceBuffer('video/mp4; codecs="avc1.42E01E"');
  if (!(sb instanceof SourceBuffer)) { fail('addSourceBuffer returned ' + sb); }
  if (ms.sourceBuffers.length !== 1) { fail('sourceBuffers has ' + ms.sourceBuffers.length); }

  var steps = [
    function(){ sb.appendBuffer(new Uint8Array(box('free', new Uint8Array(16)))); },
    function(){ sb.appendBuffer(init('avc1', 1280, 720).buffer); },
    function(){
      if (sb.changeType) { sb.changeType('video/mp4; codecs="avc1.4d401f"'); }
      mseDone = true;
    }
  ];

  sb.addEventListener('updateend', function(){
    var next = steps.shift();
    if (next) { try { next(); } catch (e) { fail('step threw ' + e.name + ': ' + e.message); } }
  });
  sb.addEventListener('error', function(){ fail('chromium refused a test segment'); });

  sb.appendBuffer(init('avc1', 640, 360));
  try {
    sb.appendBuffer(new Uint8Array(8));
    fail('append while updating did not throw');
  } catch (e) {
    if (e.name !== 'InvalidStateError') { fail('append while updating threw ' + e.name); }
  }
});

/* The stall: a stub element on the same source, playing, its playhead held still
   for four seconds and then let go. */
var T = 1.5;
var fake = document.createElement('video');
Object.defineProperty(fake, 'paused', {get:function(){ return false; }});
Object.defineProperty(fake, 'ended', {get:function(){ return false; }});
Object.defineProperty(fake, 'readyState', {get:function(){ return 4; }});
Object.defineProperty(fake, 'currentTime', {get:function(){ return T; }, set:function(){ fake.__touched = 'seek'; }});
Object.defineProperty(fake, 'src', {get:function(){ return url; }});
Object.defineProperty(fake, 'currentSrc', {get:function(){ return url; }});
Object.defineProperty(fake, 'duration', {get:function(){ return Infinity; }});
fake.getVideoPlaybackQuality = function(){ return {totalVideoFrames: 40, droppedVideoFrames: 0}; };
['pause','play','load'].forEach(function(f){ fake[f] = function(){ fake.__touched = f; }; });
document.body.appendChild(fake);
/* Metadata the way a live stream reports it — no finite duration. The real MSE
   element above never reaches metadata (its segments are shells), so this is the
   one loadedmetadata the run has, and the probe has to read the duration off it. */
setTimeout(function(){ fake.dispatchEvent(new Event('loadedmetadata')); }, 1500);
setTimeout(function(){ setInterval(function(){ T += 0.25; }, 250); }, 5000);

function has(re){ return mse.some(function(l){ return re.test(l); }); }
var mse;

function check(){
  mse = logged.filter(function(l){ return l.indexOf('__ovs mse: ') === 0; })
              .map(function(l){ return l.substring(11); });

  if (!mseDone) { fail('the MSE sequence did not finish'); }
  if (!has(/^ms1 add REFUSED video\\/x-nope NotSupportedError$/)) { fail('refused add not reported'); }
  if (!has(/^ms1 add video video\\/mp4; codecs="avc1.42E01E"$/)) { fail('add not reported'); }
  if (!has(/^ms1 video init #1 avc1 640x360$/)) { fail('first init not read'); }
  if (!has(/^ms1 video append REFUSED InvalidStateError$/)) { fail('refused append not reported'); }
  if (!has(/^ms1 video init #2 avc1 1280x720 after 3 appends$/)) { fail('second init not counted as a switch'); }
  if (has(/init #3/)) { fail('a media segment was taken for an init'); }
  if (SourceBuffer.prototype.changeType && !has(/^ms1 video changeType video\\/mp4; codecs="avc1.4d401f"$/)) {
    fail('changeType not reported');
  }
  if (!has(/^asks mse video\\/mp4; codecs="hvc1.1.6.L93.B0" -> (yes|no)$/)) { fail('isTypeSupported question not reported'); }
  if (!has(/^stall v\\d ms1 t 1.5 buf .* \\| frames 40 \\| video avc1 1280x720 init 2 last /)) { fail('stall not reported with its feed'); }
  if (!has(/^moving again v\\d after \\d+\\.\\ds \\(on its own\\), frames \\+0, appends \\+0$/)) { fail('end of stall not reported'); }
  if (has(/avc1 0x0/)) { fail('the ftyp brand list was read as a sample entry'); }
  if (!has(/^v\\d loadstart ms1, preload \\w+, (on|off) screen$/)) { fail('MSE element loadstart not reported with its source'); }
  if (!has(/^v\\d loadedmetadata \\d+x\\d+, dur inf, (on|off) screen$/)) { fail('loadedmetadata not reported with the duration'); }
  if (has(/live page/)) { fail('a page that is not /live was called one'); }
  if (!has(/^v\\d loadstart url example\\.invalid\\/video, preload auto, (on|off) screen$/)) { fail('plain-URL element loadstart not reported'); }
  if (!has(/^with v\\d paused rs\\d ns\\d url example\\.invalid\\/video, (on|off) screen|no box, last \\w+ \\d+\\.\\ds ago$/)) { fail('stall does not list the plain-URL element'); }
  if (!has(/^with v\\d paused rs\\d ns\\d ms1, /)) { fail('stall does not list the other MSE element'); }
  if (!has(/^v\\d \\(audio\\) loadstart url example\\.invalid\\/audio, preload auto, (no box|on screen|off screen)$/)) { fail('audio element loadstart not reported'); }
  if (!has(/^v\\d src url example\\.invalid\\/detached, detached$/)) { fail('src on a detached element not reported'); }
  if (!has(/^v\\d load\\(\\) url example\\.invalid\\/detached, detached$/)) { fail('load() on a detached element not reported'); }
  if (has(/srcObject/)) { fail('a refused srcObject left a line'); }
  if (!has(/^with v\\d paused rs\\d ns\\d url example\\.invalid\\/detached, detached, last no event seen$/)) { fail('stall does not list the detached element'); }
  if (!has(/^with v\\d \\(audio\\) paused rs\\d ns\\d url example\\.invalid\\/audio, /)) { fail('stall does not list the audio element'); }
  if (!has(/^v\\d+ setAttribute src url example\\.invalid\\/attr, detached$/)) { fail('setAttribute src on a detached element not reported'); }
  if (!has(/^v\\d+ \\(audio\\) new Audio\\(url example\\.invalid\\/ctor\\), detached$/)) { fail('new Audio(url) not reported'); }
  if (!has(/^v\\d+ \\(audio\\) new Audio\\(\\), detached$/)) { fail('new Audio() not reported'); }
  if (!has(/^v\\d+ play\\(\\) url example\\.invalid\\/detached, detached$/)) { fail('play() on a detached element not reported'); }
  if (!has(/^v\\d+ \\(audio\\) play\\(\\) url example\\.invalid\\/ctor, detached$/)) { fail('play() on a new Audio not reported'); }
  if (mse.filter(function(l){ return /^v\\d+ (\\(audio\\) )?play\\(\\)/.test(l); }).length !== 2) { fail('play() reported other than for the two detached elements: ' + mse.filter(function(l){ return /play\\(\\)/.test(l); })); }
  if (!has(/^with v\\d+ paused rs\\d ns\\d url example\\.invalid\\/attr, detached, /)) { fail('stall does not list the setAttribute element'); }
  if (!has(/^with v\\d+ \\(audio\\) paused rs\\d ns\\d url example\\.invalid\\/ctor, detached, /)) { fail('stall does not list the new Audio element'); }
  if (!has(/^with 1 more not listed$/)) { fail('the seventh other element was not counted as not listed'); }
  if (!has(/^scene: 3 video, 1 audio in the document, 4 detached known, 2 iframes \\(1 not ours\\)$/)) { fail('scene line wrong or missing: ' + mse.filter(function(l){ return /^scene/.test(l); })); }
  if (mse.filter(function(l){ return /^scene/.test(l); }).length !== 1) { fail('scene reported other than once with the one stall'); }
  if (has(/^stall/) && mse.filter(function(l){ return /^stall/.test(l); }).length !== 1) { fail('one stall reported more than once'); }

  [real, fake, plain, aud].forEach(function(v){ if (v.__touched) { fail('probe called ' + v.__touched + ' on #' + (v.id || 'fake')); } });

  document.getElementById('out').innerHTML =
    '<div id=RESULTS>RESULTS\\n' + (fails.length ? fails.join('\\n') : 'ok') +
    '\\n--- lines\\n' + mse.join('\\n').replace(/</g, '&lt;') + '\\n</div>';
}

setTimeout(check, 8000);
</script></body></html>
"""


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: build-page.py <out.html>")

    open(sys.argv[1], "w", encoding="utf-8").write(
        PAGE.replace("__SCRIPT__", script()))


if __name__ == "__main__":
    main()
