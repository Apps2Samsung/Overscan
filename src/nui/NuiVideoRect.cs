using System;

namespace Overscan
{
    /// <summary>
    /// Reports where the page thinks the playing video is, so the next report says
    /// whether the in-page video path's black screen starts in the page or below it.
    ///
    /// `build-e78c0bc`'s trail is the first one carrying the engine's own output from
    /// two video paths at once, and the two do entirely different things:
    ///
    /// <code>
    /// in page   omxtzuhdvideodec0..22   directvideosink   88x set_render_rectangle FAILED
    /// default   omxuhdvideodec0..3      fakesink          segfault on the 4th
    /// </code>
    ///
    /// The in-page failure is one assertion, every single time it tries to place the
    /// picture:
    ///
    /// <code>
    /// gst_video_overlay_set_render_rectangle:
    ///   assertion '(width == -1 &amp;&amp; height == -1) || (width &gt; 0 &amp;&amp; height &gt; 0)' failed
    /// </code>
    ///
    /// So "in page" on this engine is not compositing frames into the page's texture
    /// at all — it is a hole punch like the overlay, positioned per element instead of
    /// over the whole window, and the rectangle it is given has a width or a height
    /// of zero or less. The video decodes correctly the whole time (`rs4`, right
    /// dimensions, audio playing) and is then drawn into a rectangle of no size. That
    /// is the black screen. It is also why hardware overlay works: overlay passes no
    /// rectangle at all, which is the `-1, -1` whole-screen branch — the one branch
    /// that satisfies that assertion. Overlay is not succeeding at what in-page fails,
    /// it is skipping it.
    ///
    /// **This only measures. It does not touch the page**, and that restraint is the
    /// point of the build rather than caution about it. The obvious guess is that
    /// Instagram's transformed, clipped, scroll-snapped reel containers collapse the
    /// engine's rectangle, and the obvious fix is to flatten the video's box — but the
    /// count argues against it before we spend a build on it. Eighty-eight attempts,
    /// zero successes, across reels of four different intrinsic sizes. A cause in the
    /// page's own layout would succeed sometimes. A cause in the hosting — NUI
    /// composites the web view as a texture, with no native window of its own for the
    /// engine to ask about — fails exactly this way: always, everywhere, whatever the
    /// page does. Flattening the box on that theory would change a page for nothing,
    /// and this issue has already had one build that changed a page on a guess and
    /// cost the reporter the one configuration that worked for him.
    ///
    /// So the question this build asks is which of the two it is, and the answer is
    /// one line: if the page reports a good box — say `856x1520` — while the sink is
    /// still refused, the rectangle is lost between the page and the sink, inside the
    /// engine's own hosting, and nothing on this side of the wall can reach it. In-page
    /// video is then finished on this set and hardware overlay is what Overscan has to
    /// offer it. If instead the box really is zero, or every ancestor is transformed
    /// and clipped, then flattening it is worth the next build.
    ///
    /// NUI-only, like the census and the cap: the channel is the page's console (see
    /// <see cref="NuiMediaWatch"/> for why it is not <c>EvaluateJavaScript</c>), and
    /// the four `Tizen.WebView` packages have neither that hook nor this problem.
    /// </summary>
    internal static class NuiVideoRect
    {
        /// <summary>What its console lines start with, kept apart from the census's and the cap's.</summary>
        public const string Prefix = "__ovs rect: ";

        /// <summary>The last box it reported, for the diagnostics report.</summary>
        public static string LastBox { get; private set; } = "(no video placed yet)";

        /// <summary>Forgets the previous page's reading.</summary>
        public static void Reset()
        {
            LastBox = "(no video placed yet)";
        }

        /// <summary>Records one of its console lines. Called by <see cref="NuiMediaWatch"/>.</summary>
        public static void Note(string line)
        {
            LastBox = line;
            Breadcrumbs.DropToTrail("video rect: " + line);
        }

        /// <summary>
        /// The script, idempotent so it can be re-injected on every load, and on its
        /// own interval rather than sharing the census's so that a throw in here
        /// cannot cost us the count that would explain it.
        /// </summary>
        public static string Script()
        {
            return @"
(function(){
  var NS = '__ovsVideoRect';
  if (window[NS]) { return; }

  var last = '';

  function report(line) { try { console.log('" + Prefix + @"' + line); } catch (e) {} }

  function round(n) { return Math.round(n || 0); }

  /* The one the engine is placing: playing, and if several are, the one nearest the
     middle of the screen. A reel feed keeps several mounted and the interesting box
     is the one the viewer is looking at. */
  function subject() {
    try {
      var vs = document.getElementsByTagName('video');
      var best = null, bestDist = Infinity;
      var mid = (window.innerHeight || 1080) / 2;

      for (var i = 0; i < (vs ? vs.length : 0); i++) {
        var v = vs[i];
        if (v.paused || v.ended || v.readyState < 2) { continue; }
        var r = v.getBoundingClientRect();
        var d = Math.abs(((r.top + r.bottom) / 2) - mid);
        if (d < bestDist) { bestDist = d; best = v; }
      }

      return best;
    } catch (e) { return null; }
  }

  /* What could collapse a rectangle on the way up the tree. Counted rather than
     named: the names are Instagram's minified classes and would say nothing, but
     `tf 0 clip 0 ovf 0` against a zero box would rule the page out in one glance. */
  function ancestry(v) {
    var tf = 0, clip = 0, ovf = 0, zero = 0, depth = 0;

    try {
      for (var p = v.parentElement; p && depth < 40; p = p.parentElement, depth++) {
        var s = window.getComputedStyle(p);
        if (!s) { continue; }

        if (s.transform && s.transform !== 'none') { tf++; }
        if ((s.clipPath && s.clipPath !== 'none') || (s.clip && s.clip !== 'auto')) { clip++; }
        if (s.overflow && s.overflow !== 'visible') { ovf++; }

        var pr = p.getBoundingClientRect();
        if (pr.width <= 0 || pr.height <= 0) { zero++; }
      }
    } catch (e) {}

    return 'tf ' + tf + ' clip ' + clip + ' ovf ' + ovf + ' zeroparents ' + zero;
  }

  function visibility(v) {
    try {
      var s = window.getComputedStyle(v);
      if (!s) { return 'style unreadable'; }
      if (s.display === 'none') { return 'display:none'; }
      if (s.visibility !== 'visible') { return 'visibility:' + s.visibility; }
      if (parseFloat(s.opacity) === 0) { return 'opacity:0'; }
      return 'ok';
    } catch (e) { return 'style unreadable'; }
  }

  /* The engine has to turn a page box into a screen rectangle, so whatever it would
     scale or offset by belongs next to the box. A visual viewport pinned somewhere
     unexpected, or a dpr that is not 1, would be the arithmetic that reaches zero. */
  function mapping() {
    var dpr = 1, vx = 0, vy = 0, vw = 0, vh = 0, scale = 1;

    try { dpr = window.devicePixelRatio || 1; } catch (e) {}
    try {
      var vv = window.visualViewport;
      if (vv) {
        vx = vv.offsetLeft; vy = vv.offsetTop;
        vw = vv.width; vh = vv.height; scale = vv.scale;
      }
    } catch (e) {}

    return 'dpr ' + dpr + ' vv ' + round(vx) + ',' + round(vy) +
           ' ' + round(vw) + 'x' + round(vh) + ' scale ' + scale;
  }

  function look() {
    try {
      var v = subject();
      if (!v) { return; }

      var r = v.getBoundingClientRect();
      var line = 'box ' + round(r.left) + ',' + round(r.top) + ' ' +
                 round(r.width) + 'x' + round(r.height) +
                 ' | off ' + round(v.offsetWidth) + 'x' + round(v.offsetHeight) +
                 ' | client ' + round(v.clientWidth) + 'x' + round(v.clientHeight) +
                 ' | intrinsic ' + (v.videoWidth || 0) + 'x' + (v.videoHeight || 0) +
                 ' | vis ' + visibility(v) +
                 ' | ' + ancestry(v) +
                 ' | ' + mapping();

      if (line === last) { return; }
      last = line;
      report(line);
    } catch (e) {}
  }

  window[NS] = true;
  setInterval(look, 2000);
})();
";
        }
    }
}
