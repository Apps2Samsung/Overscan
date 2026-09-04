using System;
using System.IO;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// Writes the shipping clip out as a page a browser can be asked about. The
    /// checking is done by the decoder, not here.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "clip.html";
            byte[] clip = AdSilence.Clip;

            // OfflineAudioContext and not an <audio> element: headless chrome has no
            // audio device and does not advance a media clock, so a play()/ended test
            // there passes by timing out. decodeAudioData needs no clock and no
            // device, and it gives back the samples themselves — which is the only
            // way to check that a stream of zeroed Layer III frames really is
            // silence, rather than a second of noise that decodes without error.
            var page = new StringBuilder();
            page.Append("<!doctype html><meta charset=utf-8><body><pre id=o>running</pre><script>\n");
            page.Append("var b64=\"").Append(Convert.ToBase64String(clip)).Append("\";\n");
            page.Append(@"
var bin = atob(b64), buf = new Uint8Array(bin.length);
for (var i = 0; i < bin.length; i++) { buf[i] = bin.charCodeAt(i); }
// Read before decoding, not after: decodeAudioData takes ownership of the
// buffer and detaches it, so buf.length is 0 by the time the promise settles.
var bytes = buf.length;
var out = [];
function done() { document.getElementById('o').textContent = out.join('\n'); }
var Ctx = window.OfflineAudioContext || window.webkitOfflineAudioContext;
new Ctx(1, 44100, 44100).decodeAudioData(buf.buffer).then(function (ab) {
  var peak = 0;
  for (var c = 0; c < ab.numberOfChannels; c++) {
    var d = ab.getChannelData(c);
    for (var i = 0; i < d.length; i++) { var v = Math.abs(d[i]); if (v > peak) { peak = v; } }
  }
  out.push('bytes=' + bytes);
  out.push('channels=' + ab.numberOfChannels);
  out.push('rate=' + ab.sampleRate);
  out.push('duration=' + ab.duration.toFixed(4));
  out.push('peak=' + peak);
  done();
}).catch(function (e) { out.push('DECODE FAILED ' + e); done(); });
");
            page.Append("</script></body>");

            File.WriteAllText(outPath, page.ToString(), new UTF8Encoding(false));
            Console.WriteLine("adsilence: " + clip.Length + " bytes of clip -> " + outPath);
            return 0;
        }
    }
}
