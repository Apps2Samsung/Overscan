#!/usr/bin/env python3
"""Builds the test page around the page script as it is actually shipped.

The script is lifted out of src/common/PageScript.cs, with the bridge name
substituted the way PageScript.Install does and the verbatim-string quotes
undoubled the way the compiler does. Keeping a copy here instead would test the
copy. OVS_SOURCE points it at another revision of the file, which is how the
harness is shown to catch the fault it was written for.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.environ.get(
    "OVS_SOURCE", os.path.join(HERE, "..", "..", "src", "common", "PageScript.cs"))


def script():
    text = open(SOURCE, encoding="utf-8").read()
    body = re.search(r'private const string Raw = @"\n(.*?)\n";\n', text, re.S)
    if not body:
        sys.exit("could not find the verbatim page script in PageScript.cs")
    return body.group(1).replace('""', '"').replace("__BRIDGE__", "sbnative")


# The checks run in the order the app makes the calls, and every one of them is a
# thing the app has relied on the arrow doing. `visibilitychange` cannot be made
# to fire for real from inside a headless page, so it is dispatched: the listener
# does not look at the event, only at the fact of it.
PAGE = """<!doctype html><html><head><meta charset=utf-8>
<style>body{margin:0;height:2000px}</style>
</head><body>
<div id=out>running</div>
<p><a href="#a" id=link>a link</a></p>
<script>
var results = [];
function check(ok, what) { results.push((ok ? 'ok   ' : 'FAIL ') + what); }
function arrow() { return document.getElementById('__ovs_cursor'); }
function shown() { var a = arrow(); return !!a && a.parentNode && a.style.display === 'block'; }
function hidden() { var a = arrow(); return !!a && a.style.display === 'none'; }
function visibility() { document.dispatchEvent(new Event('visibilitychange')); }
</script>
<script>
__SCRIPT__
</script>
<script type="text/plain" id="shipped">__SCRIPT__</script>
<script>
try {
  // A fresh page: the script puts the arrow up. This is what a page-drawn
  // pointer relies on, and what every load looked like before the app drew one.
  check(shown(), 'a fresh install shows the arrow');

  // The app is drawing the pointer: it hides the arrow, and then everything the
  // page does on its own must leave it hidden. Issue #91 is the third line.
  window.__ovs.hide();
  check(hidden(), 'hide() takes the arrow off');
  visibility();
  check(hidden(), 'a visibilitychange does not bring it back');
  window.__ovs.install();
  check(hidden(), 'nor does a re-install');
  window.__ovs.move(0.25, 0.25);
  check(hidden(), 'nor does a move');
  check(String(window.__ovs.linkAt()).length >= 0, 'the rest of the script still answers while hidden');

  // The arrow is wiped by a single-page navigation and re-installed: still off.
  arrow().parentNode.removeChild(arrow());
  visibility();
  check(hidden() && arrow().parentNode, 'put back into the DOM after a wipe, and still off');

  // The app switches to the page's arrow: show() is the way back, and it holds.
  window.__ovs.show();
  check(shown(), 'show() brings it back');
  visibility();
  check(shown(), 'and it stays through a visibilitychange');
  window.__ovs.install();
  check(shown(), 'and through a re-install');

  // Once more round, so hidden state is not one-shot.
  window.__ovs.hide();
  visibility();
  check(hidden(), 'hidden again, and a second visibilitychange still leaves it off');

  // The whole script re-evaluated on an installed page (what a re-install from
  // the app is) does not undo a hide either.
  eval(document.getElementById('shipped').textContent);
  check(hidden(), 're-running the shipped script over an installed page keeps it off');
} catch (e) {
  check(false, 'threw: ' + e);
}
var out = document.getElementById('out');
out.textContent = 'RESULTS\\n' + results.join('\\n') + '\\n';
</script>
</body></html>
"""

if __name__ == "__main__":
    js = script()
    page = PAGE.replace("__SCRIPT__", js)
    with open(sys.argv[1], "w", encoding="utf-8") as f:
        f.write(page)
