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
  if (has(/^stall/) && mse.filter(function(l){ return /^stall/.test(l); }).length !== 1) { fail('one stall reported more than once'); }

  [real, fake, plain].forEach(function(v){ if (v.__touched) { fail('probe called ' + v.__touched + ' on #' + (v.id || 'fake')); } });

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
