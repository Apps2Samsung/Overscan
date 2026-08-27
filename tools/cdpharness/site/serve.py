#!/usr/bin/env python3
"""Serves one origin of the test page, and remembers the clicks it was told about.

Two of these run: one for the page, one for the frame inside it. The frame's
copy is the one that matters — it is asked for /hit by the frame's own click
handler, and /hits is how run.sh finds out whether the click ever arrived and
whether the page considered it real.

    serve.py <port> <directory>
"""

import http.server
import socketserver
import sys

HITS = []


class Handler(http.server.SimpleHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def do_GET(self):
        if self.path.startswith('/hit?'):
            HITS.append(self.path[5:])
            self.send_plain(b'ok')
            return

        if self.path == '/hits':
            self.send_plain(('\n'.join(HITS) + '\n').encode())
            return

        http.server.SimpleHTTPRequestHandler.do_GET(self)

    def send_plain(self, body):
        self.send_response(200)
        self.send_header('Content-Type', 'text/plain')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    port = int(sys.argv[1])
    root = sys.argv[2]

    socketserver.TCPServer.allow_reuse_address = True
    handler = lambda *a, **kw: Handler(*a, directory=root, **kw)
    socketserver.ThreadingTCPServer(('127.0.0.1', port), handler).serve_forever()
