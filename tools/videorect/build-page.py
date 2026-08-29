#!/usr/bin/env python3
"""Builds the test page around the geometry probe as it is actually shipped.

The script is lifted out of src/nui/NuiVideoRect.cs, with the one C# interpolation
substituted the way the compiler would. Keeping a copy here instead would test the
copy.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "src", "nui", "NuiVideoRect.cs")


def script():
    text = open(SOURCE, encoding="utf-8").read()
    body = re.search(r'return @"\n(.*?)\n";\s*\n\s*\}', text, re.S)
    if not body:
        sys.exit("could not find the verbatim script in NuiVideoRect.cs")

    js = body.group(1)
    js = js.replace('" + Prefix + @"', "__ovs rect: ")
    return js.replace('""', '"')


# The reel shape, as far as geometry is concerned: a video inside a transformed,
# clipped, overflow-hidden stack, which is what Instagram's carousel is and what
# the flatten-the-box theory says would collapse the engine's rectangle. paused and
# readyState are stubbed because a decodable file is not what is under test.
PAGE = """<!doctype html><html><head><meta charset=utf-8>
<style>
 body{margin:0}
 .frame{position:absolute;overflow:hidden;transform:translateZ(0)}
 video{display:block;width:340px;height:600px;background:#333}
</style>
</head><body>
<div id=out>running</div>
<div id=stack style="position:relative;height:4000px"></div>
<script>
var logged = [];
console.log = (function(inner){
  return function(line){ logged.push(String(line)); try{ inner.apply(console, arguments); }catch(e){} };
})(console.log);

function mk(id, top, opts){
  opts = opts || {};
  var frame = document.createElement('div');
  frame.className = 'frame';
  frame.style.top = top + 'px';
  frame.style.left = '40px';
  frame.style.width = '340px';
  frame.style.height = '600px';

  var v = document.createElement('video');
  v.id = id;
  v.setAttribute('src', 'http://example.invalid/' + id + '.mp4');
  if (opts.hide) { v.style.display = 'none'; }
  if (opts.flat) { v.style.width = '0px'; v.style.height = '0px'; }
  Object.defineProperty(v, 'readyState', {get:function(){ return opts.readyState === undefined ? 4 : opts.readyState; }});
  Object.defineProperty(v, 'paused', {get:function(){ return !!opts.paused; }});
  Object.defineProperty(v, 'ended', {get:function(){ return false; }});
  Object.defineProperty(v, 'videoWidth', {get:function(){ return 720; }});
  Object.defineProperty(v, 'videoHeight', {get:function(){ return 1280; }});

  /* Anything the probe must not do leaves a mark here. */
  v.pause = function(){ v.__touched = 'pause'; };
  v.load  = function(){ v.__touched = 'load'; };
  v.play  = function(){ v.__touched = 'play'; };

  frame.appendChild(v);
  document.getElementById('stack').appendChild(frame);
  return v;
}

/* Middle of the screen, playing — the one the probe should pick. */
var mid  = mk('mid',  Math.round(window.innerHeight/2) - 300, {});
/* Playing but far down the page — a distractor the probe must not prefer. */
var far  = mk('far',  2600, {});
/* On screen but paused — not a subject at all. */
var idle = mk('idle', 20, {paused:true});

var before = {
  src: mid.getAttribute('src'),
  html: document.getElementById('stack').innerHTML.length,
  count: document.getElementsByTagName('video').length
};

__SCRIPT__

function check(){
  var fails = [];
  var lines = logged.filter(function(l){ return l.indexOf('__ovs rect: ') === 0; });

  if (!lines.length) { fails.push('FAIL no line reported'); }

  var line = lines[lines.length - 1] || '';

  /* It has to be about the video in the middle, at that video's real box. */
  var box = line.match(/box (-?\\d+),(-?\\d+) (\\d+)x(\\d+)/);
  if (!box) {
    fails.push('FAIL no box in: ' + line);
  } else {
    var r = mid.getBoundingClientRect();
    if (box[3] !== String(Math.round(r.width)) || box[4] !== String(Math.round(r.height))) {
      fails.push('FAIL box ' + box[3] + 'x' + box[4] + ' but mid is ' +
                 Math.round(r.width) + 'x' + Math.round(r.height));
    }
    if (box[3] === '0' || box[4] === '0') { fails.push('FAIL reported a zero box for a laid-out video'); }
  }

  /* The ancestry census is the half that would rule the page in or out, so it has
     to actually count. This page gives every video one transformed, overflow-hidden
     frame. */
  var anc = line.match(/tf (\\d+) clip (\\d+) ovf (\\d+) zeroparents (\\d+)/);
  if (!anc) {
    fails.push('FAIL no ancestry in: ' + line);
  } else {
    if (Number(anc[1]) < 1) { fails.push('FAIL transform not counted (tf ' + anc[1] + ')'); }
    if (Number(anc[3]) < 1) { fails.push('FAIL overflow not counted (ovf ' + anc[3] + ')'); }
  }

  if (line.indexOf('intrinsic 720x1280') < 0) { fails.push('FAIL intrinsic size missing: ' + line); }
  if (line.indexOf('vis ok') < 0) { fails.push('FAIL visibility misread: ' + line); }
  if (line.indexOf('dpr ') < 0) { fails.push('FAIL mapping missing: ' + line); }

  /* The whole point of this build: it reads and reports and does nothing else. */
  ['mid','far','idle'].forEach(function(id){
    var v = document.getElementById(id);
    if (v.__touched) { fails.push('FAIL probe called ' + v.__touched + '() on #' + id); }
  });
  if (mid.getAttribute('src') !== before.src) { fails.push('FAIL src changed'); }
  if (document.getElementsByTagName('video').length !== before.count) { fails.push('FAIL video count changed'); }
  if (document.getElementById('stack').innerHTML.length !== before.html) { fails.push('FAIL DOM changed'); }

  /* A paused video is not a subject; if the probe picked #idle it would say so by
     reporting a box at the top of the page. */
  if (box && Math.abs(Number(box[2]) - 20) < 4) { fails.push('FAIL picked the paused video'); }

  document.getElementById('out').innerHTML =
    '<div id=RESULTS>RESULTS\\n' + (fails.length ? fails.join('\\n') : 'ok — ' + line) + '\\n</div>';
}

setTimeout(check, 6000);
</script></body></html>
"""


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: build-page.py <out.html>")

    open(sys.argv[1], "w", encoding="utf-8").write(
        PAGE.replace("__SCRIPT__", script()))


if __name__ == "__main__":
    main()
