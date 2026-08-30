#!/usr/bin/env python3
"""Builds the test page around the sweep script as it is actually shipped.

The script is lifted out of src/nui/NuiVideoCap.cs, with the two C# interpolations
substituted the way the compiler would. Keeping a copy here instead would test the
copy.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "src", "nui", "NuiVideoCap.cs")


def script():
    text = open(SOURCE, encoding="utf-8").read()
    body = re.search(r'return @"\n(.*?)\n";\s*\n\s*\}', text, re.S)
    if not body:
        sys.exit("could not find the verbatim script in NuiVideoCap.cs")

    js = body.group(1)
    js = js.replace('" + ScreensAway + @"', screens_away(text))
    js = js.replace('" + Prefix + @"', "__ovs video: ")
    return js.replace('""', '"')


def screens_away(text):
    found = re.search(r'ScreensAway\s*=\s*"([^"]+)"', text)
    return found.group(1) if found else sys.exit("no ScreensAway constant")


# Each video is a case the sweep has to get right. readyState and paused are
# stubbed because a real decodable file is not what is under test — the geometry,
# the release and the restore are.
PAGE = """<!doctype html><html><head><meta charset=utf-8>
<style>body{margin:0}.v{display:block;width:200px;height:400px;background:#333}</style>
</head><body>
<div id=out>running</div>
<div id=stack></div>
<script>
var H = window.innerHeight;
function mk(id, top, opts){
  var v=document.createElement('video');
  v.className='v'; v.id=id;
  v.style.position='absolute'; v.style.top=top+'px';
  if(opts.src) v.setAttribute('src',opts.src);
  if(opts.sourceChild){ var s=document.createElement('source'); s.setAttribute('src','http://x/a.mp4'); v.appendChild(s); }
  Object.defineProperty(v,'readyState',{get:function(){return opts.readyState;}});
  Object.defineProperty(v,'paused',{get:function(){return opts.paused;}});
  Object.defineProperty(v,'currentSrc',{get:function(){return '';}});
  /* A getter, not a real MediaStream: constructing one and assigning it hangs
     headless chrome indefinitely, and the sweep only ever reads this for truth. */
  if(opts.srcObject){ Object.defineProperty(v,'srcObject',{get:function(){return {stub:1};}}); }
  v.pause=function(){}; v.load=function(){ v.__loads=(v.__loads||0)+1; };
  document.getElementById('stack').appendChild(v);
  return v;
}
document.getElementById('stack').style.position='relative';
document.getElementById('stack').style.height=(H*12)+'px';

var inview  = mk('inview',   10,    {src:'http://a/1.mp4',     readyState:4, paused:false});
var farUrl  = mk('farUrl',   H*4,   {src:'http://a/2.mp4',     readyState:2, paused:true});
var farBlob = mk('farBlob',  H*5,   {src:'blob:http://a/xyz',  readyState:2, paused:true});
var farIdle = mk('farIdle',  H*6,   {src:'http://a/3.mp4',     readyState:0, paused:true});
var farKids = mk('farKids',  H*7,   {sourceChild:true,         readyState:2, paused:true});
var farPlay = mk('farPlay',  H*8,   {src:'http://a/4.mp4',     readyState:2, paused:false});
var farObj  = mk('farObj',   H*9,   {srcObject:true,           readyState:2, paused:true});
var near    = mk('near',     H*1.5, {src:'http://a/5.mp4',     readyState:2, paused:true});

var log=[];
console.log = function(m){ log.push(String(m)); };

__SCRIPT__

var results=[];
function check(name, cond){ results.push((cond?'PASS':'FAIL')+'  '+name); }

setTimeout(function(){
  check('in-view video untouched',         inview.getAttribute('src')==='http://a/1.mp4');
  check('video under one screen untouched',near.getAttribute('src')==='http://a/5.mp4');
  check('far url released',                farUrl.getAttribute('src')===null);
  check('far url remembered',              farUrl.__ovsSrc==='http://a/2.mp4');
  check('far url flagged for the census',  farUrl.__ovsReleased===true);
  check('far url reloaded once',           farUrl.__loads===1);
  check('far blob left alone',             farBlob.getAttribute('src')==='blob:http://a/xyz');
  check('far blob not flagged',            farBlob.__ovsReleased!==true);
  check('far blob not remembered',         !farBlob.__ovsSrc);
  check('far srcObject left alone',        farObj.__ovsReleased!==true);
  check('holding nothing left alone',      farIdle.getAttribute('src')==='http://a/3.mp4');
  check('<source> children left alone',    farKids.__ovsReleased!==true);
  check('still playing left alone',        farPlay.getAttribute('src')==='http://a/4.mp4');
  check('reported to the console',         log.length>0);
  check('counted 1 released, 2 holding',  /released 1, restored 0, holding 2 \\(no restorable source\\)/.test(log[log.length-1]||''));
  /* The bug this guards: `holding` counted the same untouchable elements again on
     every sweep, so it climbed forever and the line could never match the previous
     one — which meant the deduplication below it never fired and a reel session
     buried its own trail under two hundred identical breadcrumbs. Nothing about the
     page changes between these two checks, so the sweep in between must say nothing
     at all. */
  farUrl.style.top = (window.scrollY + 10) + 'px';

  setTimeout(function(){
    check('restored when it comes back',   farUrl.getAttribute('src')==='http://a/2.mp4');
    check('restore clears the flag',       farUrl.__ovsReleased===false);
    check('restore clears the memo',       !farUrl.__ovsSrc);
    check('counted the restore',           /restored 1/.test(log[log.length-1]||''));
    check('holding did not accumulate',    /holding 2 \\(/.test(log[log.length-1]||''));

    /* Now nothing moves for two more sweeps. Both must be silent: that is the half
       of the fix the count alone does not prove, and the half a reel session feels. */
    var quiet = log.length;
    setTimeout(function(){
      check('idle sweeps say nothing',     log.length === quiet);
      document.getElementById('out').textContent = 'RESULTS\\n' + results.join('\\n');
    }, 4500);
  }, 2500);
}, 2500);
</script></body></html>"""

open(sys.argv[1], "w", encoding="utf-8").write(PAGE.replace("__SCRIPT__", script()))
