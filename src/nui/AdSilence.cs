using System;

namespace Overscan
{
    /// <summary>
    /// The handful of hosts whose requests are answered with a second of silence
    /// instead of being refused, and the clip that answers them. Issue #37.
    /// </summary>
    /// <remarks>
    /// A refusal is the right answer for a tracker: the page asked for something it
    /// did not need, gets a failed load, and carries on. It is the wrong answer for
    /// an audio ad the player is *waiting on*. Spotify's web player treats the ad
    /// slot as a track: it starts the ad's audio, waits for it to end, and only then
    /// moves to the music. A 403 is not an ending, and uBlock Origin found out the
    /// same way twice (uAssets #18148) — too short a clip, or none, and the player
    /// sits on the ad until the page is reloaded. So what works there is not a block
    /// at all: the ad's audio request is answered with a real, decodable, silent
    /// clip, the player hears a track that finishes, and the music follows.
    ///
    /// uBlock can be surgical about which request that is, because it can see
    /// Chromium's resource type. We cannot: <c>build-f295172</c>'s report from the
    /// 2025 set came back with <c>dest=-</c> on all sixty lines of the request
    /// trail, so <c>Sec-Fetch-Dest</c> is not on the request where Tizen's hook
    /// sits, even though the rest of the header map is readable (<c>Sec-Fetch-Mode</c>
    /// and <c>Range</c> both arrive). What that report did give us is better than the
    /// fallback it was sent to find. The ad audio is not on the music's host at all:
    ///
    ///   265 requests  audio-ak.spotifycdn.com/audio   the music
    ///     8 requests  adstudio-assets.scdn.co/mp3     the ad
    ///
    /// So the ad is separable by address alone, with no path rule and no resource
    /// type — which is why this stayed a host list and did not become a rule
    /// language. <c>adstudio-assets.scdn.co</c> is Spotify's Ad Studio creative CDN
    /// and carries nothing else; the music resolves through <c>storage-resolve</c>
    /// to <c>*.spotifycdn.com/audio</c> and never goes near it.
    ///
    /// This list lives in code and not in <c>adhosts.txt</c> on purpose:
    /// <c>tools/adhosts/update.sh</c> rewrites that file wholesale from Peter Lowe's
    /// list, so an entry added there by hand survives exactly until the next refresh.
    /// </remarks>
    internal static class AdSilence
    {
        /// <summary>
        /// Hosts answered with the clip. Matched by whole host or any parent domain,
        /// through <see cref="AdHosts"/> so there is only one matcher in the app and
        /// <c>tools/adblock/run.sh</c> already holds it to its answers.
        /// </summary>
        private static readonly AdHosts Hosts = new AdHosts(new[]
        {
            // Spotify's Ad Studio creative CDN: the audio ads on the web player,
            // and nothing else. Seen as adstudio-assets.scdn.co/mp3/<sha1>.mp3.
            "adstudio-assets.scdn.co",
        });

        /// <summary>What the clip is served as. It is an MPEG-1 Layer III stream.</summary>
        public const string ContentType = "audio/mpeg";

        private static readonly byte[] Silence = BuildClip();

        /// <summary>Whether this host's requests get the clip rather than a refusal.</summary>
        public static bool Matches(string host)
        {
            return Hosts.Matches(host);
        }

        /// <summary>
        /// The clip itself: one second of decodable silence. Shared, never written
        /// to — the request thread hands the same array back for every ad.
        /// </summary>
        public static byte[] Clip
        {
            get { return Silence; }
        }

        /// <summary>
        /// Forty MPEG-1 Layer III frames, 44.1 kHz mono at 32 kbit/s, each one a
        /// four-byte header followed by a hundred zero bytes.
        /// </summary>
        /// <remarks>
        /// Built here rather than committed as a binary so that what ships can be
        /// read. A Layer III frame whose side info is all zeros has
        /// <c>part2_3_length = 0</c> in both granules, so there is no Huffman data to
        /// read and every spectral coefficient is zero: the frame decodes to silence
        /// rather than to noise. The header is <c>FF FB 10 C4</c> — sync, MPEG-1,
        /// Layer III, no CRC, bitrate index 1 (32 kbit/s), sample rate 00 (44.1 kHz),
        /// no padding, mono. At that bitrate a frame is
        /// <c>144 * 32000 / 44100 = 104</c> bytes and lasts <c>1152 / 44100</c>
        /// seconds, so forty of them are 4,160 bytes and 1.045 s.
        ///
        /// The duration is load-bearing and the number is not ours: uBlock ships a
        /// one-second clip because its half-second one left the player stuck on the
        /// ad. There is no Xing header, which is correct for constant bitrate — the
        /// decoder takes the duration from the length of the stream.
        ///
        /// None of that is worth trusting on a reading, so
        /// <c>tools/adsilence/run.sh</c> hands the bytes this method returns to a
        /// desktop Chromium's own decoder and checks it comes back one channel at
        /// 44.1 kHz, a second long, with every sample exactly zero.
        /// </remarks>
        private static byte[] BuildClip()
        {
            const int FrameBytes = 104;
            const int Frames = 40;

            var clip = new byte[FrameBytes * Frames];
            for (int i = 0; i < clip.Length; i += FrameBytes)
            {
                clip[i] = 0xFF;
                clip[i + 1] = 0xFB;
                clip[i + 2] = 0x10;
                clip[i + 3] = 0xC4;
                // The remaining hundred bytes stay zero: side info and main data.
            }

            return clip;
        }
    }
}
