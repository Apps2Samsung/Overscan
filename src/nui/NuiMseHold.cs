using System;

namespace Overscan
{
    /// <summary>
    /// Keeps the reel that is not on screen from opening a hardware pipeline while
    /// another one plays, by holding its source back until the page asks it to play.
    ///
    /// Issue #100, the `build-dccb5ad` report: every TikTok reel freezes two to four
    /// seconds in, with the whole reel buffered and the playhead still, and six out
    /// of six freezes have the same thing right in front of them. TikTok readies the
    /// next reel in its second <c>&lt;video&gt;</c> while the current one plays: a new
    /// MediaSource, <c>loadstart</c>, the init segments, <c>loadedmetadata</c>, and the
    /// engine opens a second <c>omxtzuhdvideodec</c> for it. One to three seconds after
    /// that <c>loadedmetadata</c> the reel on screen stops. The newcomer is
    /// <c>paused rs4</c> the whole time: it does not have to play to take the decoder,
    /// it only has to be readied. The set has one hardware decoder and one overlay for
    /// the two of them, and the last pipeline opened owns it. YouTube, one element,
    /// never freezes; pause/play on the frozen reel opens a fresh pipeline and that one
    /// plays. See *Two videos, one decoder* in docs/INTERNALS.md.
    ///
    /// So the second pipeline must not open while the first one is playing. The thing
    /// that opens it is the page attaching a MediaSource to the element (<c>src = blob:</c>
    /// or <c>srcObject</c>); nothing else — not <c>addSourceBuffer</c>, not an append —
    /// can happen before that, because a MediaSource only opens once it is attached.
    /// So this holds the attach back. While another <c>&lt;video&gt;</c> on the page is
    /// playing, a <c>blob:</c> or <c>srcObject</c> source set on a paused element is
    /// remembered instead of applied; the element reports the source back as if it were
    /// set (TikTok's own preloader checks it); and the moment the page calls
    /// <c>play()</c> on that element the source is applied for real and the play goes
    /// through. The page's sequence after that is its own: <c>sourceopen</c>, its
    /// appends, <c>loadedmetadata</c>, playing. TikTok has every segment in hand by
    /// then, so the cost is the time to decode a first frame, not a download.
    ///
    /// The holds are precise about *when*: only while another video is playing, which
    /// is the one state in which a second pipeline is harmful. An element that is
    /// readied while nothing plays (the first reel, any single-player site) is untouched.
    /// An element with the <c>autoplay</c> attribute is untouched, because the engine
    /// starts that one without the page ever calling <c>play()</c>, and a held source
    /// would then never be released. A new source set on a held element replaces the
    /// hold; removing the source drops it; and <c>URL.revokeObjectURL</c> on a held URL
    /// is deferred until right after the release so the page's own order is kept.
    ///
    /// Pausing and playing the frozen reel automatically was the other fix. It was set
    /// aside because it recovers after a freeze the viewer has already seen, on every
    /// reel; this is meant to stop the freeze from happening.
    ///
    /// **This changes how a page loads video**, which no other script we inject does,
    /// so it has a switch — *One video at a time* in the menu, on by default — and
    /// <c>tools/msehold/run.sh</c> drives it against desktop chromium's real
    /// MediaSource before it ships. The question for the TV is whether TikTok's player
    /// tolerates its preload waiting; the trail's <c>hold:</c> lines, read beside the
    /// probe's <c>mse:</c> lines, answer it: <c>hold v2</c> then <c>release v2 (play)</c>
    /// then <c>v2 playing</c> is the fix working, a <c>release</c> never followed by
    /// <c>playing</c> is TikTok waiting on something the held element cannot give it.
    ///
    /// NUI-only: the ewk packages run on sets where video is not this problem, and
    /// have no console channel for the lines.
    /// </summary>
    internal static class NuiMseHold
    {
        /// <summary>What its console lines start with, kept apart from the probe's.</summary>
        public const string Prefix = "__ovs hold: ";

        /// <summary>Whether the script goes into pages at all. The menu row flips it.</summary>
        public static bool Enabled = true;

        /// <summary>Sources held back this page, released into a play, and dropped unplayed.</summary>
        public static int Held { get; private set; }
        public static int Released { get; private set; }
        public static int Dropped { get; private set; }

        /// <summary>The last line, for the report.</summary>
        public static string Last { get; private set; } = "(nothing held yet)";

        /// <summary>Forgets the previous page's counts.</summary>
        public static void Reset()
        {
            Held = 0;
            Released = 0;
            Dropped = 0;
            Last = "(nothing held yet)";
        }

        /// <summary>The report's line.</summary>
        public static string Summary()
        {
            if (!Enabled)
            {
                return "off (menu) — videos preload freely";
            }

            return "on | held " + Held + ", released " + Released + ", dropped " + Dropped + " | last: " + Last;
        }

        /// <summary>Records one of its console lines. Called by <see cref="NuiMediaWatch"/>.</summary>
        public static void Note(string line)
        {
            if (line.StartsWith("hold ", StringComparison.Ordinal))
            {
                Held++;
            }
            else if (line.StartsWith("release ", StringComparison.Ordinal))
            {
                Released++;
            }
            else if (line.StartsWith("drop ", StringComparison.Ordinal))
            {
                Dropped++;
            }

            Last = line;
            Breadcrumbs.DropToTrail("hold: " + line);
        }

        /// <summary>
        /// The script, idempotent so it can be re-injected on every load. The overrides
        /// go on the prototypes once per window; a second injection would wrap the
        /// wrappers.
        ///
        /// Written without double quotes so it can sit in a verbatim string unchanged;
        /// tools/msehold/build-page.py lifts it out of this file as the compiler sees it.
        /// </summary>
        public static string Script()
        {
            return @"
(function(){
  var NS = '__ovsMseHold';
  if (window[NS]) { return; }
  window[NS] = true;

  var ME = window.HTMLMediaElement, EL = window.Element;
  if (!ME || !EL || typeof WeakMap !== 'function') { return; }
  var srcDesc = Object.getOwnPropertyDescriptor(ME.prototype, 'src');
  var objDesc = Object.getOwnPropertyDescriptor(ME.prototype, 'srcObject');
  if (!srcDesc || !srcDesc.set || !srcDesc.get) { return; }

  var BUDGET = 160, spent = 0;
  function report(line) {
    if (spent >= BUDGET) { return; }
    spent++;
    try {
      console.log('" + Prefix + @"' +
                  (spent === BUDGET ? 'budget spent; no more this page' : line));
    } catch (e) {}
  }

  /* The same v1, v2, ... the MSE probe writes, so a `hold v2` and the probe's
     `v2 playing` are about one element. Whichever script runs first makes the namer. */
  var name = window.__ovsVideoName || (window.__ovsVideoName = (function () {
    var m = new WeakMap(), n = 1;
    return function (v) {
      var g = m.get(v);
      if (!g) { g = 'v' + (n++); m.set(v, g); }
      return g;
    };
  })());

  /* video -> {src, obj, wanted, since, revokes}. `src`/`obj` undefined = nothing held.
     `wanted` = the page has asked this element to play in its current load cycle. */
  var state = new WeakMap();
  var holdingUrl = {};   /* held blob URL -> its element's state, for revokeObjectURL */

  function st(v) {
    var s = state.get(v);
    if (!s) { s = { src: undefined, obj: undefined, wanted: false, since: 0, revokes: [] }; state.set(v, s); }
    return s;
  }

  function isHeld(s) { return s.src !== undefined || s.obj !== undefined; }

  function ago(s) { return ((Date.now() - s.since) / 1000).toFixed(1) + 's'; }

  /* Another <video> that is playing: paused is false the moment play() is called,
     whether or not a frame has shown yet, which is exactly when a second pipeline
     starts to matter. */
  function busy(v) {
    try {
      var vs = document.getElementsByTagName('video');
      for (var i = 0; i < vs.length; i++) {
        var o = vs[i];
        if (o !== v && !o.paused && !o.ended) { return o; }
      }
    } catch (e) {}
    return null;
  }

  var revoke = URL.revokeObjectURL;

  function forget(v, s) {
    if (s.src !== undefined) { delete holdingUrl[s.src]; }
    s.src = undefined;
    s.obj = undefined;
  }

  /* The page's revokes, in the page's order, once the source will never be
     applied, or once the engine has looked it up. */
  function flushRevokes(s) {
    var r = s.revokes; s.revokes = [];
    for (var i = 0; i < r.length; i++) { try { revoke.call(URL, r[i]); } catch (e) {} }
  }

  /* After a release the URL must outlive the engine's lookup of it, which happens
     in the same task as the element's `loadstart` and after that event. So the
     revokes go out a task after `loadstart`, or after ten seconds if the load
     never starts. A revoke in the same task as the set breaks a page with nothing
     held too (tools/msehold/run.sh's control case), so no working page does that;
     what the hold must survive is the page revoking in a later tick, which in the
     ordinary order would have found the lookup already done. */
  function revokeAfterLookup(v, s) {
    if (!s.revokes.length) { return; }
    var done = false, timer = 0;
    function go() {
      if (done) { return; }
      done = true;
      clearTimeout(timer);
      v.removeEventListener('loadstart', onStart, true);
      flushRevokes(s);
    }
    function onStart() { setTimeout(go, 0); }
    v.addEventListener('loadstart', onStart, true);
    timer = setTimeout(go, 10000);
  }

  function drop(v, s, why) {
    if (!isHeld(s)) { return; }
    report('drop ' + name(v) + ' after ' + ago(s) + ' (' + why + ')');
    forget(v, s);
    flushRevokes(s);
  }

  function release(v, s, why) {
    if (!isHeld(s)) { return; }
    var src = s.src, obj = s.obj;
    report('release ' + name(v) + ' after ' + ago(s) + ' (' + why + ')');
    forget(v, s);
    try {
      if (src !== undefined) { srcDesc.set.call(v, src); }
      else if (objDesc && objDesc.set) { objDesc.set.call(v, obj); }
    } catch (e) {
      report('release ' + name(v) + ' FAILED ' + (e && e.name));
    }
    revokeAfterLookup(v, s);
  }

  /* Whether this attach is held. A new source always replaces whatever was held:
     it is a new load cycle, and the old one was never going to play. */
  function take(v, value, isSrc) {
    var s = st(v);
    if (isHeld(s)) { drop(v, s, isSrc ? 'src set again' : 'srcObject set again'); }
    var empty = isSrc ? (value === '' ) : !value;
    if (empty) { s.wanted = false; return false; }
    if (isSrc && value.indexOf('blob:') !== 0) { return false; }
    if (s.wanted || v.autoplay) { return false; }
    var o = busy(v);
    if (!o) { return false; }
    if (isSrc) { s.src = value; holdingUrl[value] = s; } else { s.obj = value; }
    s.since = Date.now();
    report('hold ' + name(v) + ' ' + (isSrc ? 'blob' : 'srcObject') + ' while ' + name(o) + ' plays');
    return true;
  }

  Object.defineProperty(ME.prototype, 'src', {
    configurable: true,
    enumerable: srcDesc.enumerable,
    get: function () {
      try { var s = state.get(this); if (s && s.src !== undefined) { return s.src; } } catch (e) {}
      return srcDesc.get.call(this);
    },
    set: function (value) {
      try { if (this.tagName === 'VIDEO' && take(this, String(value), true)) { return; } } catch (e) {}
      srcDesc.set.call(this, value);
    }
  });

  if (objDesc && objDesc.set && objDesc.get) {
    Object.defineProperty(ME.prototype, 'srcObject', {
      configurable: true,
      enumerable: objDesc.enumerable,
      get: function () {
        try { var s = state.get(this); if (s && s.obj !== undefined) { return s.obj; } } catch (e) {}
        return objDesc.get.call(this);
      },
      set: function (value) {
        try { if (this.tagName === 'VIDEO' && take(this, value, false)) { return; } } catch (e) {}
        objDesc.set.call(this, value);
      }
    });
  }

  var setAttr = EL.prototype.setAttribute;
  EL.prototype.setAttribute = function (n, value) {
    try {
      if (this.tagName === 'VIDEO' && String(n).toLowerCase() === 'src' && take(this, String(value), true)) { return; }
    } catch (e) {}
    return setAttr.apply(this, arguments);
  };

  var getAttr = EL.prototype.getAttribute;
  EL.prototype.getAttribute = function (n) {
    try {
      if (this.tagName === 'VIDEO' && String(n).toLowerCase() === 'src') {
        var s = state.get(this);
        if (s && s.src !== undefined) { return s.src; }
      }
    } catch (e) {}
    return getAttr.apply(this, arguments);
  };

  var removeAttr = EL.prototype.removeAttribute;
  EL.prototype.removeAttribute = function (n) {
    try {
      if (this.tagName === 'VIDEO' && String(n).toLowerCase() === 'src') {
        var s = st(this);
        s.wanted = false;
        drop(this, s, 'src removed');
      }
    } catch (e) {}
    return removeAttr.apply(this, arguments);
  };

  /* play() is the release. The source goes on first, so the play that follows is
     the ordinary one on an element that has just been given a source. */
  var play = ME.prototype.play;
  ME.prototype.play = function () {
    try {
      if (this.tagName === 'VIDEO') {
        var s = st(this);
        s.wanted = true;
        release(this, s, 'play');
      }
    } catch (e) {}
    return play.apply(this, arguments);
  };

  /* A play that did not come through play() (the engine's own autoplay, a media
     session) still counts as wanted. `emptied` ends a load cycle: the next source
     this element gets is a new question. Media events do not bubble, but they pass
     a capturing listener on the document. */
  document.addEventListener('play', function (e) {
    try {
      var v = e.target;
      if (v && v.tagName === 'VIDEO') { var s = st(v); s.wanted = true; release(v, s, 'play event'); }
    } catch (_) {}
  }, true);
  document.addEventListener('emptied', function (e) {
    try {
      var v = e.target;
      if (v && v.tagName === 'VIDEO') { st(v).wanted = false; }
    } catch (_) {}
  }, true);

  /* A page that revokes the URL a tick after setting it would otherwise find the
     source gone by the time it is applied. The revoke waits for the release (and
     the engine's lookup, see revokeAfterLookup), or for the drop. */
  URL.revokeObjectURL = function (url) {
    try {
      var s = holdingUrl[String(url)];
      if (s) { s.revokes.push(String(url)); return; }
    } catch (e) {}
    return revoke.apply(this, arguments);
  };
})();
";
        }
    }
}
