using System;

namespace Overscan
{
    /// <summary>
    /// Gives back the hardware decoder of a video the viewer has scrolled well past.
    ///
    /// Issue #20's set dies on Instagram reels, and `build-85d0e4e`'s trail is the
    /// first one that says why rather than what it is not. The engine's own output
    /// ends:
    ///
    /// <code>
    /// 16:43:30  GstOmxUhdVideoDec ... omxuhdvideodec0
    /// 16:43:48  GstOmxUhdVideoDec ... omxuhdvideodec1
    /// 16:43:59  GstOmxUhdVideoDec ... omxuhdvideodec2
    ///           DotNET onSigsegv called on org.apps2samsung.overscan / render_thread
    /// </code>
    ///
    /// One `GstOmxUhdVideoDec` per reel, none of them going away, and the process
    /// segfaults inside the render thread as the third is allocated. The census on
    /// the same trail says `playing=1 of 16` — sixteen <c>&lt;video&gt;</c> elements
    /// mounted, one of them decoding. So it is not concurrent *playback* that
    /// exhausts the set, it is that a paused reel keeps the decoder it was given, and
    /// this is a TV with very few of them. That is the shape
    /// <see cref="NuiMediaWatch"/> was built to look for and the count is the finding.
    ///
    /// Pausing is not enough — every one of those sixteen was already paused. The
    /// only thing that makes the engine let a media pipeline go is taking the source
    /// away and reloading the element, so that is what this does, and only to
    /// elements more than one screen outside the viewport in either direction: a
    /// reel two swipes back, which the viewer cannot see and which the feed will
    /// re-source itself if they swipe back to it. Anything on screen or next to it is
    /// never touched.
    ///
    /// **Nothing is released that cannot be put back, and that limit was learned the
    /// expensive way.** An ordinary URL is remembered on the element and restored when
    /// it scrolls near again. A `blob:` or <c>srcObject</c> source belongs to a
    /// <c>MediaSource</c> the page built and may have revoked, so taking it away is
    /// permanent — `build-3368aea` released a whole reel feed of them and the report
    /// came back `released 9 (9 blob), restored 0` with the visible video sitting at
    /// `0x0 rs0`. That turned the reporter's one working configuration into a blank
    /// screen. Those are now counted as `held` and left alone; the count is worth
    /// keeping, because it is what says this feed is one we cannot help.
    ///
    /// So on an MSE feed — Instagram reels included — **this does nothing at all**,
    /// and the crash below is untouched. That is the honest state of it.
    ///
    /// It cannot fix the segfault in any case, which is inside the engine's own
    /// GStreamer path and beyond anything managed code may do. Where a feed serves
    /// ordinary URLs it can keep the app from reaching the allocation that trips it,
    /// which is the only lever on this side of the wall.
    ///
    /// NUI-only, like the census: the four `Tizen.WebView` packages have no console
    /// channel to report on and are not the sets with the problem.
    /// </summary>
    internal static class NuiVideoCap
    {
        /// <summary>What its console lines start with, kept apart from the census's.</summary>
        public const string Prefix = "__ovs video: ";

        /// <summary>
        /// How far outside the viewport a video has to be before its decoder is taken
        /// back, in multiples of the window height. One whole screen is about two
        /// reels: far enough that releasing it cannot be what the viewer is looking
        /// at, near enough that the decoder does not sit there until the third reel
        /// asks for one that is not there.
        /// </summary>
        private const string ScreensAway = "1.0";

        /// <summary>The last line it reported, for the diagnostics report.</summary>
        public static string LastAction { get; private set; } = "(nothing released yet)";

        /// <summary>Forgets the previous page's tally.</summary>
        public static void Reset()
        {
            LastAction = "(nothing released yet)";
        }

        /// <summary>Records one of its console lines. Called by <see cref="NuiMediaWatch"/>.</summary>
        public static void Note(string line)
        {
            LastAction = line;
            Breadcrumbs.DropToTrail("video cap: " + line);
        }

        /// <summary>
        /// The script, idempotent so it can be re-injected on every load, like the
        /// census's. It runs on its own interval rather than sharing that one, so a
        /// throw in here cannot cost us the measurement that explains it.
        /// </summary>
        public static string Script()
        {
            return @"
(function(){
  var NS = '__ovsVideoCap';
  if (window[NS]) { return; }

  var SCREENS = " + ScreensAway + @";
  var KEPT = '__ovsSrc';
  var released = 0, restored = 0, held = 0, lastReport = '';

  function report(line) { try { console.log('" + Prefix + @"' + line); } catch (e) {} }

  /* How many screens outside the viewport the element is, in whichever direction
     it left by. Zero for anything overlapping the viewport at all. */
  function screensAway(v) {
    try {
      var r = v.getBoundingClientRect();
      var h = window.innerHeight || 1080, w = window.innerWidth || 1920;
      var down = r.top - h, up = -r.bottom, right = r.left - w, left = -r.right;
      var out = Math.max(0, down, up, right, left);
      return out / (h || 1);
    } catch (e) { return 0; }
  }

  /* Taking the source away and reloading is the only thing that makes the engine
     hand the decoder back; pause() leaves the pipeline attached, which is exactly
     how sixteen paused reels held three decoders between them.

     An element fed by <source> children is left alone: load() would pick the same
     child straight back up, so there is nothing to gain and a working video to
     lose. */
  function release(v) {
    try {
      if (v.getElementsByTagName('source').length) { return false; }

      var object = false;
      try { object = !!v.srcObject; } catch (_) {}

      var src = v.currentSrc || v.getAttribute('src') || '';
      if (!src && !object) { return false; }

      /* Nothing is released that cannot be put back. A blob: or srcObject source
         belongs to a MediaSource the page built and may have revoked, so taking it
         away is permanent: build-3368aea did exactly that to a whole reel feed and
         the report came back `released 9 (9 blob), restored 0` with the visible
         video at `0x0 rs0`. Counted, because the count is what says this feed is
         one we cannot help. */
      if (object || src.lastIndexOf('blob:', 0) === 0) {
        held++;
        return false;
      }

      try { v[KEPT] = src; } catch (_) {}

      v.pause();

      /* removeAttribute and then load(), never src=''. An empty string resolves
         against the document and sends the element off to fetch the page itself. */
      v.removeAttribute('src');
      v.__ovsReleased = true;
      v.load();
      released++;
      return true;
    } catch (e) { return false; }
  }

  function restore(v) {
    try {
      var src = v[KEPT];
      if (!src) { return false; }
      v[KEPT] = null;
      v.__ovsReleased = false;
      v.src = src;
      v.load();
      restored++;
      return true;
    } catch (e) { return false; }
  }

  function sweep() {
    try {
      var vs = document.getElementsByTagName('video');
      for (var i = 0; i < (vs ? vs.length : 0); i++) {
        var v = vs[i], away = screensAway(v);

        if (away > SCREENS) {
          /* readyState 0 means it is holding nothing already, so there is no
             decoder to take back and no reason to touch it. */
          if (!v.paused || v.readyState === 0) { continue; }
          release(v);
        } else if (v[KEPT]) {
          restore(v);
        }
      }

      var line = 'released ' + released + ', restored ' + restored +
                 ', held ' + held + ' (no restorable source)';
      if (line !== lastReport) { lastReport = line; report(line); }
    } catch (e) {}
  }

  window[NS] = true;
  setInterval(sweep, 2000);
})();
";
        }
    }
}
