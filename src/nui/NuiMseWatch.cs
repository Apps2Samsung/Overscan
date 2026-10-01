using System;

namespace Overscan
{
    /// <summary>
    /// Reports what a page feeds the engine's Media Source player, so the next report
    /// says what a TikTok reel was in the middle of when it froze.
    ///
    /// Issue #100, `build-7e24138`'s report: every reel on TikTok goes black and
    /// silent two to four seconds after it starts, with the census reading
    /// <c>rs4 stuck</c> and never <c>starved</c>. The element has enough data buffered
    /// past the playhead and the playhead does not move. Pause and resume brings it
    /// back. YouTube, through the same proxy, plays. The engine prints nothing at the
    /// moment of the freeze: every reel's pipeline comes up with the same block of
    /// GStreamer warnings (<c>Sticky event misordering</c>, <c>GST_IS_CAPS</c>) and then
    /// is silent until the next reel's. So the native output, which has answered
    /// everything else on this set, has nothing to say about this.
    ///
    /// A freeze a few seconds into every stream, with the data there, is the shape of
    /// a player that has just switched quality: an adaptive player starts low and
    /// moves up once it has measured the connection, and on MSE that switch is a new
    /// initialisation segment appended into the same SourceBuffer mid-stream. A
    /// hardware decoder that cannot follow a change of resolution or codec under it
    /// would stop exactly like this, and a pause/resume, which restarts the pipeline,
    /// would be what frees it. The other candidate is the format itself: TikTok serves
    /// HEVC (its <c>bytevc1</c>) to browsers that say they can play it. The two need
    /// different fixes, so this build only measures which one it is:
    ///
    /// <code>
    /// mse: ms3 add video video/mp4; codecs="hvc1.1.6.L93.B0"     what the player asked for
    /// mse: ms3 video init #1 hvc1 576x1024                       first init segment
    /// mse: ms3 video init #2 hvc1 720x1280 after 4 appends       a quality switch, mid-stream
    /// mse: stall ms3 t 3.1 buf 0-9.8 | frames 92 | video ...      the freeze, with what led to it
    /// </code>
    ///
    /// An `init #2` landing just before every `stall` is the quality switch, and the
    /// fix is to keep the player on one rendition. A stall with only `init #1` behind
    /// it, on `hvc1` but not `avc1`, is the format, and the fix is to stop answering
    /// yes to HEVC. Neither of those, with frames still counting up during the stall,
    /// is the engine drawing nothing while it decodes, which is below anything we can
    /// reach from the page.
    ///
    /// `build-c05e9b9` answered neither: H.264 only, one init per source, the whole
    /// reel buffered. What lined up with every freeze was in the native output, a
    /// second hardware pipeline opening a few seconds into a reel while it played,
    /// and two of the three stalls were on an element whose source this probe never
    /// saw made. So it now names every <c>&lt;video&gt;</c> (<c>v1</c>, <c>v2</c>, ...), writes
    /// down when each one asks for a pipeline and where its media comes from, and
    /// follows a stall with one <c>with</c> line per other element:
    ///
    /// <code>
    /// mse: v2 loadstart ms4, preload auto, off screen        the next reel asks for a pipeline
    /// mse: stall v1 ms3 t 2.1 buf 0.1-29.5 | frames 0 | ...  the one on screen stops
    /// mse: with v2 paused rs1 ns2 ms4, off screen, last loadedmetadata 0.6s ago
    /// </code>
    ///
    /// Another element's <c>loadstart</c>/<c>loadedmetadata</c> a moment before every
    /// stall is two videos holding the one hardware decoder. A stall with nothing
    /// else stirring is not that.
    ///
    /// **This wraps four MSE calls and changes none of them.** Every wrapper calls the
    /// original with the same <c>this</c> and arguments and returns what it returned,
    /// and an exception from the original reaches the page unchanged after the wrapper
    /// has written down its name. Our own bookkeeping is in its own try, so a mistake
    /// in it costs a line, never the append. <c>tools/msewatch/run.sh</c> holds it to
    /// that against desktop chromium's real MediaSource.
    ///
    /// It is installed after the page has started, like the census, so a reel whose
    /// MediaSource was made before the script arrived is missed; TikTok makes a fresh
    /// one per reel, so everything after the first is seen.
    ///
    /// NUI-only, for the same reasons as <see cref="NuiMediaWatch"/>: the channel is
    /// the page's console, which the ewk packages cannot hear.
    /// </summary>
    internal static class NuiMseWatch
    {
        /// <summary>What its console lines start with, kept apart from the census's.</summary>
        public const string Prefix = "__ovs mse: ";

        /// <summary>The last format line (an add or an init segment), for the report.</summary>
        public static string LastFormat { get; private set; } = "(no media source seen yet)";

        /// <summary>The last stall line, for the report.</summary>
        public static string LastStall { get; private set; } = "(no stall seen yet)";

        /// <summary>Forgets the previous page's readings.</summary>
        public static void Reset()
        {
            LastFormat = "(no media source seen yet)";
            LastStall = "(no stall seen yet)";
        }

        /// <summary>Records one of its console lines. Called by <see cref="NuiMediaWatch"/>.</summary>
        public static void Note(string line)
        {
            if (line.StartsWith("stall", StringComparison.Ordinal) ||
                line.StartsWith("still", StringComparison.Ordinal) ||
                line.StartsWith("moving", StringComparison.Ordinal))
            {
                LastStall = line;
            }
            else if (line.IndexOf(" add ", StringComparison.Ordinal) >= 0 ||
                     line.IndexOf(" init #", StringComparison.Ordinal) >= 0)
            {
                LastFormat = line;
            }

            Breadcrumbs.DropToTrail("mse: " + line);
        }

        /// <summary>
        /// The script, idempotent so it can be re-injected on every load. The wrappers
        /// go on the prototypes once per window; a second injection into the same
        /// window would otherwise wrap the wrappers and count every append twice.
        /// </summary>
        public static string Script()
        {
            return @"
(function(){
  var NS = '__ovsMse';
  if (window[NS]) { return; }
  window[NS] = true;

  var MS = window.MediaSource;
  var SB = window.SourceBuffer;
  if (!MS || !SB || typeof WeakMap !== 'function') { return; }

  /* A feed appends many times a second for as long as it plays, so appends are
     counted, never reported one by one. What is reported is bounded per page: the
     trail's value is that its last lines are readable. */
  var BUDGET = 320, spent = 0;
  function report(line) {
    if (spent >= BUDGET) { return; }
    spent++;
    try {
      console.log('" + Prefix + @"' +
                  (spent === BUDGET ? 'budget spent; no more this page' : line));
    } catch (e) {}
  }

  var sources = new WeakMap();   /* MediaSource -> {id, url, buffers} */
  var buffers = new WeakMap();   /* SourceBuffer -> its counts */
  var byUrl = {};                /* object URL -> MediaSource record, last few only */
  var urls = [];
  var nextId = 1;

  function source(ms) {
    var r = sources.get(ms);
    if (!r) { r = { id: 'ms' + (nextId++), url: '', buffers: [] }; sources.set(ms, r); }
    return r;
  }

  function label(sb) {
    var s = buffers.get(sb);
    return s ? s.src.id + ' ' + s.kind : 'ms? ?';
  }

  function ago(at) {
    return at ? ((Date.now() - at) / 1000).toFixed(1) + 's ago' : 'never';
  }

  function bytesOf(data) {
    if (data instanceof ArrayBuffer) { return new Uint8Array(data); }
    if (ArrayBuffer.isView(data)) { return new Uint8Array(data.buffer, data.byteOffset, data.byteLength); }
    return null;
  }

  function fourcc(u, p) {
    return String.fromCharCode(u[p], u[p + 1], u[p + 2], u[p + 3]);
  }

  function size(u, p) {
    return ((u[p] << 24) | (u[p + 1] << 16) | (u[p + 2] << 8) | u[p + 3]) >>> 0;
  }

  /* An init segment is an ftyp (or a bare moov) at the very start, or WebM's EBML
     header. Media segments start with styp/moof or a WebM cluster. */
  function isInit(u) {
    if (!u || u.length < 8) { return false; }
    if (u[0] === 0x1A && u[1] === 0x45 && u[2] === 0xDF && u[3] === 0xA3) { return true; }
    var t = fourcc(u, 4);
    return t === 'ftyp' || t === 'moov';
  }

  /* The sample entry names the codec, and for video the 24 bytes after its type
     are fixed fields with the coded width and height right behind them. A box
     size in front of the type is what tells it from the same four letters
     anywhere else in the segment, and the scan starts past the ftyp, whose list
     of compatible brands is where `avc1` sits in nearly every MP4 (build-c05e9b9
     read TikTok's audio init as `avc1 0x0` off it). */
  var VIDEO = ['avc1', 'avc3', 'hvc1', 'hev1', 'av01', 'vp09', 'dvh1', 'dvhe'];
  var AUDIO = ['mp4a', 'ac-3', 'ec-3', 'Opus', 'fLaC'];
  function format(u) {
    if (u[0] === 0x1A) { return 'webm'; }
    var end = Math.min(u.length - 4, 65536);
    var from = 8;
    if (fourcc(u, 4) === 'ftyp') { from = Math.max(from, size(u, 0) + 4); }
    for (var p = from; p < end; p++) {
      var t = fourcc(u, p), n = size(u, p - 4);
      if (n < 8 || p - 4 + n > u.length) { continue; }
      if (VIDEO.indexOf(t) >= 0 && p + 32 <= u.length) {
        var w = (u[p + 28] << 8) | u[p + 29], h = (u[p + 30] << 8) | u[p + 31];
        return t + ' ' + w + 'x' + h;
      }
      if (AUDIO.indexOf(t) >= 0) { return t; }
    }
    return 'unknown codec';
  }

  /* --- the four wrappers. Each calls the original and returns what it did. --- */

  var createUrl = URL.createObjectURL;
  URL.createObjectURL = function (obj) {
    var url = createUrl.apply(this, arguments);
    try {
      if (obj instanceof MS) {
        var r = source(obj);
        r.url = url;
        byUrl[url] = r;
        urls.push(url);
        if (urls.length > 8) { delete byUrl[urls.shift()]; }
      }
    } catch (e) {}
    return url;
  };

  var add = MS.prototype.addSourceBuffer;
  MS.prototype.addSourceBuffer = function (mime) {
    var sb;
    try {
      sb = add.apply(this, arguments);
    } catch (err) {
      try { report(source(this).id + ' add REFUSED ' + mime + ' ' + err.name); } catch (e) {}
      throw err;
    }
    try {
      var r = source(this);
      var m = String(mime);
      var kind = /^video/i.test(m) ? 'video' : /^audio/i.test(m) ? 'audio' : '?';
      buffers.set(sb, { src: r, kind: kind, inits: 0, appends: 0, bytes: 0, lastAt: 0, fmt: '' });
      r.buffers.push(sb);
      report(r.id + ' add ' + kind + ' ' + m);
      sb.addEventListener('error', function () { report(label(sb) + ' append error'); });
      if (r.buffers.length === 1) {
        var ms = this;
        ms.addEventListener('sourceended', function () {
          var why = '';
          try { why = ms.readyState; } catch (e) {}
          report(r.id + ' ended (' + why + ')');
        });
      }
    } catch (e) {}
    return sb;
  };

  var append = SB.prototype.appendBuffer;
  SB.prototype.appendBuffer = function (data) {
    try {
      var s = buffers.get(this);
      if (s) {
        var u = bytesOf(data);
        s.appends++;
        s.bytes += u ? u.length : 0;
        s.lastAt = Date.now();
        if (isInit(u)) {
          s.inits++;
          s.fmt = format(u);
          report(label(this) + ' init #' + s.inits + ' ' + s.fmt +
                 (s.inits > 1 ? ' after ' + (s.appends - 1) + ' appends' : ''));
        }
      }
    } catch (e) {}
    try {
      return append.apply(this, arguments);
    } catch (err) {
      try { report(label(this) + ' append REFUSED ' + err.name); } catch (e) {}
      throw err;
    }
  };

  if (SB.prototype.changeType) {
    var change = SB.prototype.changeType;
    SB.prototype.changeType = function (mime) {
      try { report(label(this) + ' changeType ' + mime); } catch (e) {}
      return change.apply(this, arguments);
    };
  }

  /* What the player asks before it picks a format. One line per distinct question,
     with the answer the engine actually gave, because the answer is what decides
     whether TikTok sends HEVC at all. */
  var asked = {}, askedCount = 0;
  function noteAsk(how, mime, answer) {
    try {
      var key = how + ' ' + mime;
      if (asked[key] || askedCount >= 24) { return; }
      asked[key] = true;
      askedCount++;
      report('asks ' + how + ' ' + mime + ' -> ' + (answer === true ? 'yes' : answer === false ? 'no' : (answer || 'no')));
    } catch (e) {}
  }

  if (MS.isTypeSupported) {
    var supported = MS.isTypeSupported;
    MS.isTypeSupported = function (mime) {
      var answer = supported.apply(this, arguments);
      noteAsk('mse', mime, answer);
      return answer;
    };
  }

  /* --- the stall, from the player's side --- */

  var watch = new WeakMap();   /* video -> {t, since, reported, frames, appends, lastPause} */

  function frames(v) {
    try {
      var q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null;
      return q ? q.totalVideoFrames : -1;
    } catch (e) { return -1; }
  }

  function ranges(v) {
    try {
      var b = v.buffered, out = [];
      for (var i = 0; i < b.length && i < 3; i++) {
        out.push(b.start(i).toFixed(1) + '-' + b.end(i).toFixed(1));
      }
      return out.length ? out.join(',') : 'none';
    } catch (e) { return '?'; }
  }

  function sourceFor(v) {
    try { return byUrl[v.currentSrc || v.src] || byUrl[v.src] || null; } catch (e) { return null; }
  }

  function appendsOf(r) {
    var n = 0;
    if (!r) { return 0; }
    for (var i = 0; i < r.buffers.length; i++) {
      var s = buffers.get(r.buffers[i]);
      if (s) { n += s.appends; }
    }
    return n;
  }

  function feeds(r) {
    if (!r) { return 'not an MSE source we saw made'; }
    var out = [];
    for (var i = 0; i < r.buffers.length; i++) {
      var s = buffers.get(r.buffers[i]);
      if (!s) { continue; }
      out.push(s.kind + ' ' + (s.fmt || '?') + ' init ' + s.inits + ' last ' + ago(s.lastAt));
    }
    return out.join(' | ');
  }

  /* Pause and seek are the two things that would end a stall from the page's side,
     so the line that says it is over says whether either happened first. */
  ['pause', 'seeking'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      try {
        var w = e.target && watch.get(e.target);
        if (w) { w.touched = type; }
      } catch (_) {}
    }, true);
  });

  /* --- every video element, by name ---

     build-c05e9b9's native output put every freeze next to a second hardware
     pipeline opening while a reel was already playing, and two of the three
     stalls were on an element whose source this probe never saw made. So each
     <video> gets a name (v1, v2, ...) that stays with it, the moments an element
     asks the engine for a pipeline are written down against that name, and a
     stall lists what every other element was doing. Media events do not bubble,
     but they do pass through a capturing listener on the document. */
  var tags = new WeakMap();   /* video -> {id, ev, at} */
  var nextTag = 1;

  function tag(v) {
    var g = tags.get(v);
    if (!g) { g = { id: 'v' + (nextTag++), ev: '', at: 0 }; tags.set(v, g); }
    return g;
  }

  /* Where an element's media comes from: one of our MediaSources by id, a blob we
     never saw made, a srcObject (a MediaSource handed over from a worker comes
     this way, and our wrappers on this window cannot see one), or a plain URL,
     which the engine plays with its own demuxer instead of the MSE path. */
  function origin(v) {
    try {
      var s = v.currentSrc || v.src || '';
      if (!s) { return v.srcObject ? 'srcObject' : 'no src'; }
      var r = byUrl[s];
      if (r) { return r.id; }
      if (s.indexOf('blob:') === 0) { return 'blob not ours'; }
      var a = document.createElement('a');
      a.href = s;
      return 'url ' + a.host + '/' + (a.pathname.split('/')[1] || '');
    } catch (e) { return '?'; }
  }

  function onScreen(v) {
    try {
      var b = v.getBoundingClientRect();
      var w = window.innerWidth, h = window.innerHeight;
      if (b.width <= 0 || b.height <= 0) { return 'no box'; }
      return (b.right > 0 && b.bottom > 0 && b.left < w && b.top < h) ? 'on screen' : 'off screen';
    } catch (e) { return '?'; }
  }

  ['loadstart', 'loadedmetadata', 'playing', 'waiting', 'pause', 'emptied'].forEach(function (type) {
    document.addEventListener(type, function (e) {
      try {
        var v = e.target;
        if (!v || v.tagName !== 'VIDEO') { return; }
        var g = tag(v);
        g.ev = type;
        g.at = Date.now();
        var what = type === 'loadstart' ? origin(v) + ', preload ' + v.preload + ', ' + onScreen(v)
                 : type === 'loadedmetadata' ? v.videoWidth + 'x' + v.videoHeight + ', ' + onScreen(v)
                 : type === 'emptied' ? onScreen(v)
                 : 't ' + v.currentTime.toFixed(1) + ', ' + onScreen(v);
        report(g.id + ' ' + type + ' ' + what);
      } catch (_) {}
    }, true);
  });

  /* One line per other element, because a stall line already runs near the
     trail's line length on its own. */
  function others(v) {
    try {
      var vs = document.getElementsByTagName('video');
      for (var i = 0, n = 0; i < vs.length && n < 3; i++) {
        var o = vs[i];
        if (o === v) { continue; }
        n++;
        var g = tag(o);
        report('with ' + g.id + ' ' + (o.paused ? 'paused' : 'playing') +
               ' rs' + o.readyState + ' ns' + o.networkState + ' ' + origin(o) + ', ' +
               onScreen(o) + ', last ' + (g.ev ? g.ev + ' ' + ago(g.at) : 'no event seen'));
      }
      if (vs.length > 4) { report('with ' + (vs.length - 4) + ' more not listed'); }
    } catch (e) {}
  }

  function look() {
    try {
      var vs = document.getElementsByTagName('video');
      var now = Date.now();
      for (var i = 0; i < (vs ? vs.length : 0); i++) {
        var v = vs[i];
        if (v.paused || v.ended || v.readyState < 3) { continue; }

        var t = v.currentTime;
        var w = watch.get(v);
        if (!w) { w = { t: t, since: now, reported: 0 }; watch.set(v, w); continue; }

        if (t !== w.t) {
          if (w.reported) {
            var r0 = sourceFor(v);
            report('moving again ' + tag(v).id + ' after ' + ((now - w.since) / 1000).toFixed(1) + 's' +
                   (w.touched ? ' (after a ' + w.touched + ')' : ' (on its own)') +
                   ', frames +' + (frames(v) - w.frames) +
                   ', appends +' + (appendsOf(r0) - w.appends));
          }
          w.t = t; w.since = now; w.reported = 0; w.touched = '';
          continue;
        }

        var held = now - w.since;
        var r = sourceFor(v);
        if (!w.reported && held >= 2000) {
          w.reported = 1;
          w.frames = frames(v);
          w.appends = appendsOf(r);
          w.touched = '';
          report('stall ' + tag(v).id + ' ' + origin(v) + ' t ' + t.toFixed(1) + ' buf ' + ranges(v) +
                 ' | frames ' + w.frames + ' | ' + feeds(r));
          others(v);
        } else if (w.reported === 1 && held >= 8000) {
          w.reported = 2;
          report('still stalled 8s ' + tag(v).id + ': frames +' + (frames(v) - w.frames) +
                 ', appends +' + (appendsOf(r) - w.appends) + ', buf ' + ranges(v));
        }
      }
    } catch (e) {}
  }

  setInterval(look, 1000);
})();
";
        }
    }
}
