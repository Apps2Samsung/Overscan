using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Clicks through the engine's own debugging protocol, for the one case
    /// nothing else can reach: a cross-origin <c>&lt;iframe&gt;</c>.
    ///
    /// Everything before this tried to hand input to the platform *underneath* the
    /// engine — <see cref="NuiNativeTouch"/> feeds a touch point into the DALi
    /// window, which is the documented way, and on the set in issue #20 four
    /// separate builds proved that the call succeeds and nothing arrives: no
    /// trusted event, no frame taking focus. There is no variation on that idea
    /// left that is not a guess at the same layer.
    ///
    /// This is the other side of the engine. <see cref="NuiInspector"/> starts
    /// chromium's inspector server; this connects to it and sends
    /// <c>Input.dispatchMouseEvent</c>, which the browser process injects into the
    /// widget's input pipeline. Two things follow, and they are exactly the two
    /// things a captcha needs: the renderer treats it as real input, so the event
    /// is <c>isTrusted</c>, and the *browser* hit-tests the point, so it is routed
    /// to whichever renderer owns the frame at that position rather than to the
    /// page that happens to contain it. That is the same mechanism Puppeteer and
    /// Playwright use for this exact problem.
    ///
    /// The reporter's `/json/list` came back over the network, so the server is a
    /// real one and speaks the standard protocol. Nothing here is Tizen-specific:
    /// it is an HTTP GET for the page's socket address and a WebSocket carrying
    /// three JSON messages, and every piece of it is exercised against desktop
    /// chromium in <c>tools/cdpharness</c> before it ever goes near a TV.
    ///
    /// Threading: the socket is driven by one background thread, because
    /// everything here blocks and the DALi main loop may not. Nothing in this
    /// class touches NUI. The main thread learns what happened the way it already
    /// does for the native path — by asking the page, a moment later, what it saw
    /// (see <c>NuiCursor.ReportNativeWitness</c>), and by reading
    /// <see cref="LastResult"/>.
    /// </summary>
    internal static class NuiInspectorInput
    {
        /// <summary>How long a connect, a send or a reply may take.</summary>
        private const int CallTimeoutMilliseconds = 3000;

        /// <summary>
        /// Between press and release. A contact with no duration is not a tap to
        /// any gesture recogniser, and the scoring half of a captcha reads the
        /// timing of the press it is given.
        /// </summary>
        private const int TapMilliseconds = 90;

        private static readonly object Sync = new object();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);

        private static Thread _worker;
        private static ClientWebSocket _socket;

        /// <summary>
        /// The path of the page target, once discovered — <c>/devtools/page/ID</c>.
        /// The path and not the whole address, because the host is always the
        /// loopback and the port belongs to whichever server is up now: the one
        /// this was discovered against may since have been stopped and restarted
        /// somewhere else.
        /// </summary>
        private static string _target;

        private static int _nextId = 1;

        /// <summary>The click waiting to go out. Latest wins — see <see cref="Click"/>.</summary>
        private static bool _pending;
        private static int _pendingX;
        private static int _pendingY;

        /// <summary>What happened to the last click, for the diagnostics screen.</summary>
        public static string LastResult
        {
            get { lock (Sync) { return _lastResult; } }
        }

        private static string _lastResult = "(not used yet)";

        /// <summary>
        /// Whether the last click went out cleanly. The caller uses it to decide
        /// whether the user needs to be told that a frame could not be clicked.
        /// </summary>
        public static bool Succeeded
        {
            get { lock (Sync) { return _succeeded; } }
        }

        private static bool _succeeded;

        /// <summary>
        /// Clicks at a point in the page's own CSS pixels — which is what the
        /// protocol wants, and what <c>PageScript</c> reports back with the
        /// <c>FRAME:</c> result, so no assumption about zoom or viewport width has
        /// to hold for this to land in the right place.
        ///
        /// Returns immediately; the work happens on the worker thread. A second
        /// press while one is still going out replaces it rather than queueing:
        /// two taps 50 ms apart are a person leaning on the OK button, and the
        /// second position is the one they meant.
        /// </summary>
        public static bool Click(int x, int y)
        {
            if (NuiInspector.Port == 0)
            {
                Set("no inspector server to talk to", false);
                DiagLog.Add("inspector click: " + LastResult);
                return false;
            }

            lock (Sync)
            {
                _pending = true;
                _pendingX = x;
                _pendingY = y;
            }

            EnsureWorker();
            Wake.Set();
            return true;
        }

        /// <summary>
        /// Forgets the connection, because the server behind it has been stopped.
        /// The next click discovers and connects from scratch.
        /// </summary>
        public static void Reset()
        {
            ClientWebSocket socket;
            lock (Sync)
            {
                socket = _socket;
                _socket = null;
                _target = null;
            }

            Dispose(socket);
        }

        private static void EnsureWorker()
        {
            lock (Sync)
            {
                if (_worker != null)
                {
                    return;
                }

                _worker = new Thread(Loop);
                _worker.IsBackground = true;
                _worker.Start();
            }
        }

        private static void Loop()
        {
            while (true)
            {
                Wake.WaitOne();

                int x;
                int y;

                lock (Sync)
                {
                    if (!_pending)
                    {
                        continue;
                    }

                    x = _pendingX;
                    y = _pendingY;
                    _pending = false;
                }

                try
                {
                    // Twice at most, and only for a socket that was already open
                    // when this click started. A connection the engine has since
                    // torn down — the page navigated, the server was stopped and
                    // started again — still reports itself as open, and says
                    // otherwise only when something is sent down it. That is worth
                    // one silent retry. A socket opened fresh for this click and
                    // failing immediately is a real failure, and is reported.
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        bool reused;
                        ClientWebSocket socket = Connect(out reused);
                        if (socket == null || Send(socket, x, y) || !reused)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Drop(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------ connection

        /// <summary>
        /// The live socket, opening one if there is not one already. Returns null
        /// when it could not be opened, having recorded why.
        /// </summary>
        private static ClientWebSocket Connect(out bool reused)
        {
            ClientWebSocket existing;
            string target;
            lock (Sync)
            {
                existing = _socket;
                target = _target;
            }

            reused = false;

            if (existing != null && existing.State == WebSocketState.Open)
            {
                reused = true;
                return existing;
            }

            Dispose(existing);
            lock (Sync)
            {
                _socket = null;
            }

            // A remembered target is worth one attempt and no more. It survives a
            // navigation, because the page target is the view rather than the
            // document — but not a server that has been stopped and started again
            // in between, and from out here those two look identical until the
            // connect fails.
            if (target != null)
            {
                ClientWebSocket remembered = Open(target);
                if (remembered != null)
                {
                    return remembered;
                }
            }

            target = Discover();
            return target == null ? null : Open(target);
        }

        /// <summary>
        /// One attempt at the socket for a known target. Null, with the reason
        /// recorded, if it did not open.
        /// </summary>
        private static ClientWebSocket Open(string target)
        {
            string address = "ws://127.0.0.1:" +
                             NuiInspector.Port.ToString(CultureInfo.InvariantCulture) + target;

            try
            {
                var socket = new ClientWebSocket();
                using (var timeout = new CancellationTokenSource(CallTimeoutMilliseconds))
                {
                    socket.ConnectAsync(new Uri(address), timeout.Token).GetAwaiter().GetResult();
                }

                lock (Sync)
                {
                    _socket = socket;
                    _target = target;
                }

                Set("connected to " + address, false);
                DiagLog.Add("inspector click: " + LastResult);
                return socket;
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _target = null;
                }

                Set("connect failed — " + Innermost(ex), false);
                DiagLog.Add("inspector click: " + LastResult);
                return null;
            }
        }

        /// <summary>
        /// Asks the inspector server which page it has, over its plain HTTP side.
        ///
        /// Always over the loopback, whatever address the JSON hands back: the
        /// reporter reached this from another machine, so the URLs in it carry the
        /// TV's LAN address, and routing our own click back in over the network
        /// would be both slower and one more thing to fail.
        /// </summary>
        private static string Discover()
        {
            string json;
            try
            {
                json = Get("127.0.0.1", (int)NuiInspector.Port, "/json/list");
            }
            catch (Exception ex)
            {
                Drop("could not read /json/list — " + Innermost(ex));
                return null;
            }

            foreach (string target in Objects(json))
            {
                // Type matters: a cross-origin frame can itself appear in this list
                // as its own target, and sending the click *into* the frame's own
                // renderer would defeat the point — it is the page-level hit test
                // that routes a point to the right frame.
                if (Field(target, "type") != "page")
                {
                    continue;
                }

                string url = Field(target, "webSocketDebuggerUrl");
                if (url != null)
                {
                    return PathOf(url);
                }
            }

            Drop("no page target in /json/list (" + json.Length + " bytes)");
            return null;
        }

        /// <summary>
        /// The path out of a <c>ws://host:port/path</c>, which is the only part of
        /// it that identifies the target. The host in the JSON is whatever address
        /// the request came in on — the TV's own LAN address, when the reporter
        /// fetched it from a phone — and none of it is any use from in here.
        /// </summary>
        private static string PathOf(string url)
        {
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
            {
                return url;
            }

            int path = url.IndexOf('/', scheme + 3);
            return path < 0 ? "/" : url.Substring(path);
        }

        /// <summary>
        /// One HTTP GET over a socket, and the body it answered with.
        ///
        /// HttpClient would do, but this is a handful of bytes to the loopback and
        /// the app already talks to itself this way (see <see cref="DiagServer"/>);
        /// a stack that can be configured is a stack that can be configured wrongly
        /// on one firmware.
        ///
        /// Two things about the server on the other end are worth writing down,
        /// because both of them look exactly like "nothing is listening" from here
        /// and neither is:
        ///
        /// It answers HTTP/1.1 and only 1.1 — a 1.0 request gets silence and a
        /// socket held open. And it ignores <c>Connection: close</c>: the response
        /// arrives complete and the socket then stays up, so reading to the end of
        /// the stream reaches a timeout rather than an end, and throws away a
        /// perfectly good answer on the way. <c>Content-Length</c> is what says
        /// where the body stops, so that is what is read.
        /// </summary>
        private static string Get(string host, int port, string path)
        {
            using (var client = new TcpClient())
            {
                if (!client.ConnectAsync(host, port).Wait(CallTimeoutMilliseconds))
                {
                    throw new TimeoutException("no answer from " + host + ":" + port);
                }

                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = CallTimeoutMilliseconds;
                    stream.WriteTimeout = CallTimeoutMilliseconds;

                    byte[] request = Encoding.ASCII.GetBytes(
                        "GET " + path + " HTTP/1.1\r\n" +
                        "Host: " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                        "Connection: close\r\n\r\n");

                    stream.Write(request, 0, request.Length);
                    stream.Flush();

                    return Body(stream);
                }
            }
        }

        /// <summary>
        /// Reads one HTTP response: headers to the blank line, then exactly as many
        /// bytes as <c>Content-Length</c> promised. A response without one is read
        /// until the stream ends, which is what the header's absence means.
        /// </summary>
        private static string Body(Stream stream)
        {
            var buffered = new MemoryStream();
            var scratch = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0)
                {
                    throw new IOException("the connection ended before the headers did");
                }

                buffered.Write(scratch, 0, read);
                headerEnd = IndexOfBlankLine(buffered.GetBuffer(), (int)buffered.Length);
            }

            byte[] all = buffered.GetBuffer();
            int total = (int)buffered.Length;
            int bodyStart = headerEnd + 4;

            string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            int length = ContentLength(headers);

            if (length < 0)
            {
                // No promise about the length, so the end of the stream is the end
                // of the body — and the read timeout is the backstop if the server
                // does not close either.
                int read;
                while ((read = stream.Read(scratch, 0, scratch.Length)) > 0)
                {
                    buffered.Write(scratch, 0, read);
                }

                all = buffered.GetBuffer();
                total = (int)buffered.Length;
                return Encoding.UTF8.GetString(all, bodyStart, total - bodyStart);
            }

            while (total - bodyStart < length)
            {
                int read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0)
                {
                    break;
                }

                buffered.Write(scratch, 0, read);
                all = buffered.GetBuffer();
                total = (int)buffered.Length;
            }

            int have = Math.Min(length, total - bodyStart);
            return Encoding.UTF8.GetString(all, bodyStart, have);
        }

        /// <summary>Where the CRLFCRLF between headers and body starts, or -1.</summary>
        private static int IndexOfBlankLine(byte[] bytes, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 &&
                    bytes[i + 2] == 13 && bytes[i + 3] == 10)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The Content-Length header's value, or -1 if there is not one.</summary>
        private static int ContentLength(string headers)
        {
            const string Name = "content-length:";

            int at = headers.ToLowerInvariant().IndexOf(Name, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            int i = at + Name.Length;
            while (i < headers.Length && headers[i] == ' ')
            {
                i++;
            }

            int start = i;
            while (i < headers.Length && headers[i] >= '0' && headers[i] <= '9')
            {
                i++;
            }

            int value;
            return i > start &&
                   int.TryParse(headers.Substring(start, i - start),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : -1;
        }

        // --------------------------------------------------------------- the click

        /// <summary>
        /// Move, press, release. The move is not decoration: a page that only
        /// reacts on hover has to see the pointer arrive before it is pressed, and
        /// the frame under it has to be the one the browser hit-tests — sending a
        /// press to a point the engine has never seen a pointer at is the one way
        /// this differs from a person doing it.
        /// </summary>
        private static bool Send(ClientWebSocket socket, int x, int y)
        {
            try
            {
                return Dispatch(socket, x, y);
            }
            catch (Exception ex)
            {
                Drop(Innermost(ex));
                return false;
            }
        }

        /// <summary>Move, press, release — see <see cref="Send"/>.</summary>
        private static bool Dispatch(ClientWebSocket socket, int x, int y)
        {
            string moved = Call(socket,
                "Input.dispatchMouseEvent",
                "\"type\":\"mouseMoved\",\"x\":" + x + ",\"y\":" + y +
                ",\"button\":\"none\",\"buttons\":0,\"clickCount\":0," +
                "\"pointerType\":\"mouse\"");

            if (moved != null)
            {
                Drop("move rejected — " + moved);
                return false;
            }

            string pressed = Call(socket,
                "Input.dispatchMouseEvent",
                "\"type\":\"mousePressed\",\"x\":" + x + ",\"y\":" + y +
                ",\"button\":\"left\",\"buttons\":1,\"clickCount\":1," +
                "\"pointerType\":\"mouse\"");

            if (pressed != null)
            {
                Drop("press rejected — " + pressed);
                return false;
            }

            Thread.Sleep(TapMilliseconds);

            string released = Call(socket,
                "Input.dispatchMouseEvent",
                "\"type\":\"mouseReleased\",\"x\":" + x + ",\"y\":" + y +
                ",\"button\":\"left\",\"buttons\":0,\"clickCount\":1," +
                "\"pointerType\":\"mouse\"");

            if (released != null)
            {
                // The press is already in. Saying so matters: a page that saw the
                // press and never the release behaves very differently from one
                // that saw nothing, and the difference is invisible from here.
                Drop("release rejected after the press landed — " + released);
                return false;
            }

            Set("clicked at " + x + "," + y, true);
            DiagLog.Add("inspector click: " + LastResult);
            return true;
        }

        /// <summary>
        /// One command, and its reply. Returns null when the engine accepted it,
        /// or the protocol's own complaint when it did not.
        /// </summary>
        private static string Call(ClientWebSocket socket, string method, string parameters)
        {
            int id;
            lock (Sync)
            {
                id = _nextId++;
            }

            string message = "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) +
                             ",\"method\":\"" + method + "\",\"params\":{" + parameters + "}}";

            using (var timeout = new CancellationTokenSource(CallTimeoutMilliseconds))
            {
                byte[] payload = Encoding.UTF8.GetBytes(message);
                socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    true,
                    timeout.Token).GetAwaiter().GetResult();

                // Nothing here enables a domain, so the only thing that can come
                // back is a reply to a command — but ids are still matched, because
                // a reply that belongs to an abandoned earlier call would otherwise
                // pass for this one's.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    string reply = Receive(socket, timeout.Token);
                    if (!HasId(reply, id))
                    {
                        continue;
                    }

                    return Field(reply, "message") ??
                           (reply.IndexOf("\"error\"", StringComparison.Ordinal) < 0
                               ? null
                               : "the engine refused it");
                }

                return "no reply to " + method;
            }
        }

        private static string Receive(ClientWebSocket socket, CancellationToken token)
        {
            var message = new MemoryStream();
            var buffer = new byte[8192];

            while (true)
            {
                WebSocketReceiveResult part = socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), token).GetAwaiter().GetResult();

                if (part.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("the engine closed the connection");
                }

                message.Write(buffer, 0, part.Count);

                if (part.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        // -------------------------------------------------------- reading the JSON

        /// <summary>
        /// The objects of a JSON array, as text. A parser would be the right answer
        /// to a general problem; this one has exactly two shapes to read and both
        /// come from chromium, so tracking braces (and staying out of strings while
        /// doing it) is the whole of it.
        /// </summary>
        private static List<string> Objects(string json)
        {
            var found = new List<string>();
            if (json == null)
            {
                return found;
            }

            int depth = 0;
            int start = -1;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        found.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// The string value of a top-level field, or null. Only the escapes
        /// chromium actually emits are undone — a target url is the one field here
        /// that carries them, and it is never used for anything but reading.
        /// </summary>
        private static string Field(string json, string name)
        {
            if (json == null)
            {
                return null;
            }

            string key = "\"" + name + "\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            int i = at + key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' ||
                                       json[i] == '\r' || json[i] == '\n'))
            {
                i++;
            }

            if (i >= json.Length || json[i] != ':')
            {
                return null;
            }

            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' ||
                                       json[i] == '\r' || json[i] == '\n'))
            {
                i++;
            }

            if (i >= json.Length || json[i] != '"')
            {
                return null;
            }

            i++;
            var value = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];

                if (c == '"')
                {
                    return value.ToString();
                }

                if (c != '\\' || i >= json.Length)
                {
                    value.Append(c);
                    continue;
                }

                char escape = json[i++];
                switch (escape)
                {
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(i, 4),
                                             NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture,
                                             out code))
                            {
                                value.Append((char)code);
                            }

                            i += 4;
                        }

                        break;
                    default: value.Append(escape); break;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether a reply carries this command's id. Read rather than parsed, and
        /// bounded on both sides so <c>"id":12</c> is not taken for <c>"id":1</c>.
        /// </summary>
        private static bool HasId(string json, int id)
        {
            if (json == null)
            {
                return false;
            }

            int at = json.IndexOf("\"id\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            int i = at + 4;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':'))
            {
                i++;
            }

            int start = i;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9')
            {
                i++;
            }

            if (i == start)
            {
                return false;
            }

            int value;
            return int.TryParse(json.Substring(start, i - start),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out value) && value == id;
        }

        // ------------------------------------------------------------ bookkeeping

        private static void Drop(string reason)
        {
            ClientWebSocket socket;
            lock (Sync)
            {
                socket = _socket;
                _socket = null;
            }

            Dispose(socket);
            Set(reason, false);
            DiagLog.Add("inspector click: " + reason);
        }

        private static void Dispose(ClientWebSocket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
                // A socket being closed because something already went wrong with
                // it does not get to make things worse.
            }
        }

        private static void Set(string result, bool succeeded)
        {
            lock (Sync)
            {
                _lastResult = result;
                _succeeded = succeeded;
            }
        }

        /// <summary>
        /// The message that actually says something. A socket failure arrives
        /// wrapped two or three deep, and only the innermost one names the errno.
        /// </summary>
        private static string Innermost(Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
