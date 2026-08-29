using System;
using System.Collections.Generic;

namespace Overscan
{
    /// <summary>
    /// Writes down what the page's video elements are doing, so the trail says what
    /// was on screen at the moment the process ended.
    ///
    /// Issue #20's app dies while a reel plays and leaves a trail that ends on a
    /// memory reading. The memory readings now rule out an eviction (see
    /// <see cref="NuiDeathWatch"/>), which makes the interesting question the one
    /// nothing has ever recorded: how many videos were decoding, and whether any of
    /// them was already in trouble. A TV has a small, fixed number of hardware
    /// decoders and one video plane; a feed that mounts a fresh <c>&lt;video&gt;</c>
    /// per reel and leaves the last few running is the shape of thing that exhausts
    /// them, and if that is what happens here then the count in the last line before
    /// the death is the whole finding.
    ///
    /// **The channel is the page's console, not
    /// <c>EvaluateJavaScript</c>.** NUI keeps a single pending result handler per
    /// view, so a periodic evaluation with a callback would steal the answer from
    /// whatever the cursor or the frame-click path was in the middle of asking — the
    /// exact bug that comment on <c>OnKeyboardCommitted</c> is about. A
    /// <c>console.log</c> travels the other way, arrives through
    /// <c>WebView.ConsoleMessageReceived</c>, and cannot collide with anything.
    ///
    /// This is NUI-only on purpose. The ewk packages have no console hook, so the
    /// script would be talking to nobody there, and the four older sets are not the
    /// ones with the problem.
    /// </summary>
    internal static class NuiMediaWatch
    {
        /// <summary>What our own console lines start with, so they can be told from the page's.</summary>
        private const string Prefix = "__ovs media: ";

        /// <summary>
        /// How many distinct console errors from the page itself reach the trail.
        /// Instagram alone produces a steady stream of them and the trail's whole
        /// value is that the last lines are readable, so this is a budget rather
        /// than a filter: the first few are usually the ones about media.
        /// </summary>
        private const int ErrorBudget = 24;

        private const int MaxLineLength = 200;

        /// <summary>What a census line starts with, so the count can be read out of it.</summary>
        private const string CountField = "playing=";

        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static int _errorsTrailed;

        /// <summary>The last census, for the report.</summary>
        public static string LastCensus { get; private set; } = "(no video seen yet)";

        /// <summary>
        /// Whether the last census found anything decoding. Read by
        /// <c>NuiBrowserApp.NoteMemory</c>, which samples this process's size twice
        /// as often while there is video on screen: that is the interval issue #20
        /// needs and it is not worth paying for it on every idle page, because the
        /// whole previous trail goes into the report a reporter has to paste.
        /// Volatile because the report is built on DiagServer's thread.
        /// </summary>
        public static volatile bool VideoPlaying;

        /// <summary>Forgets the previous page's errors, so a new page gets the full budget.</summary>
        public static void Reset()
        {
            Seen.Clear();
            _errorsTrailed = 0;
            VideoPlaying = false;
        }

        /// <summary>
        /// Handles one console message from the page. Ours become a `media:` line on
        /// the trail; the page's own errors get a bounded number of lines and are
        /// then dropped.
        /// </summary>
        public static void Console(string level, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string census = Trim(text.Substring(Prefix.Length));
                LastCensus = census;

                // Only a census carries a count. A stall or an error arrives on the
                // same channel and says nothing about how many videos are running,
                // so it must not be allowed to answer that question with a no —
                // which is the moment the finer sampling is most wanted.
                int playing = PlayingCount(census);
                if (playing >= 0)
                {
                    VideoPlaying = playing > 0;
                }

                Breadcrumbs.DropToTrail("media: " + census);
                return;
            }

            // The decoder cap's own channel. Kept out of LastCensus: the report's
            // `media :` line has to stay the count of what is decoding.
            if (text.StartsWith(NuiVideoCap.Prefix, StringComparison.Ordinal))
            {
                NuiVideoCap.Note(Trim(text.Substring(NuiVideoCap.Prefix.Length)));
                return;
            }

            // Errors only. A page's ordinary logging is not evidence about anything
            // and would bury what is.
            if (level == null || level.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (_errorsTrailed >= ErrorBudget)
            {
                return;
            }

            string line = Trim(text);
            if (!Seen.Add(line))
            {
                return;
            }

            _errorsTrailed++;
            Breadcrumbs.DropToTrail("page error: " + line);

            if (_errorsTrailed == ErrorBudget)
            {
                Breadcrumbs.DropToTrail("page error: budget spent; no more will be recorded this page");
            }
        }

        /// <summary>
        /// How many videos the census said were decoding, or -1 for a line that is
        /// not a census — a stall or an error report, which arrive on the same
        /// channel and say nothing about the count.
        /// </summary>
        private static int PlayingCount(string census)
        {
            if (!census.StartsWith(CountField, StringComparison.Ordinal))
            {
                return -1;
            }

            int digits = CountField.Length;
            while (digits < census.Length && census[digits] >= '0' && census[digits] <= '9')
            {
                digits++;
            }

            int count;
            return int.TryParse(census.Substring(CountField.Length, digits - CountField.Length),
                                out count)
                ? count
                : -1;
        }

        private static string Trim(string text)
        {
            string line = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return line.Length <= MaxLineLength ? line : line.Substring(0, MaxLineLength) + "…";
        }

        /// <summary>
        /// The script, idempotent so it can be re-injected on every load. It only
        /// ever reads and logs — a watcher that paused a video would be changing the
        /// thing it is supposed to be measuring, and the question of whether capping
        /// the decoders fixes this is the *next* build's, not this one's.
        ///
        /// The census is reported when it changes, and otherwise once every ten
        /// seconds, because a feed left playing would fill the trail with one
        /// repeated line and push the start of the run out of reach.
        /// </summary>
        public static string Script()
        {
            return @"
(function(){
  var NS = '__ovsMedia';
  if (window[NS]) { window[NS].install(); return; }

  var last = '', lastAt = 0;

  function report(line) { try { console.log('" + Prefix + @"' + line); } catch (e) {} }

  function dropped(v) {
    try {
      var q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null;
      return q ? q.droppedVideoFrames : -1;
    } catch (e) { return -1; }
  }

  function describe(v) {
    var bits = (v.videoWidth || 0) + 'x' + (v.videoHeight || 0) + ' rs' + v.readyState;
    var d = dropped(v);
    if (d > 0) { bits += ' dropped ' + d; }
    if (v.error) { bits += ' ERROR ' + v.error.code; }
    return bits;
  }

  function census() {
    try {
      var vs = document.getElementsByTagName('video');
      var n = vs ? vs.length : 0;
      if (!n) { last = ''; return; }

      var playing = [];
      for (var i = 0; i < n && playing.length < 4; i++) {
        if (!vs[i].paused && !vs[i].ended) { playing.push(describe(vs[i])); }
      }

      var line = '" + CountField + @"' + playing.length + ' of ' + n +
                 (playing.length ? ' — ' + playing.join('; ') : '');

      var now = Date.now();
      if (line === last && now - lastAt < 10000) { return; }
      last = line;
      lastAt = now;
      report(line);
    } catch (e) {}
  }

  /* A media element's error and stall events do not bubble, so they are only
     visible in the capture phase on the document. These two are the ones that say
     the pipeline is in trouble; the rest of the media events fire constantly on a
     feed and would say nothing. */
  function distress(e) {
    try {
      var t = e && e.target;
      if (!t || (t.tagName !== 'VIDEO' && t.tagName !== 'AUDIO')) { return; }

      /* An element NuiVideoCap has just taken the source away from is expected to
         complain. Reporting that would spend the error budget on our own doing and
         bury the pipeline failures the budget exists for. */
      if (t.__ovsReleased) { return; }
      report(t.tagName.toLowerCase() + ' ' + e.type +
             (t.error ? ' code ' + t.error.code : '') + ' rs' + t.readyState);
    } catch (_) {}
  }

  window[NS] = {
    install: function () {
      try {
        ['error', 'stalled'].forEach(function (type) {
          document.addEventListener(type, distress, true);
        });
      } catch (_) {}
    }
  };

  window[NS].install();
  setInterval(census, 2000);
})();
";
        }
    }
}
