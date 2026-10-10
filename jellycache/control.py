"""Authenticated loopback-only control for the running cache owner."""
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading

from .jellyfin_events import normalized_event


class ControlServer:
    def __init__(self, worker, token, port=0, media_root="/video"):
        if not isinstance(token, str) or len(token) < 32 or "\n" in token or "\r" in token:
            raise ValueError("control token must contain at least 32 characters")
        expected = ("Bearer " + token).encode("utf-8")

        class Handler(BaseHTTPRequestHandler):
            def setup(self):
                super().setup()
                self.connection.settimeout(5)

            def log_message(self, *args):
                pass  # Do not log tokens, raw DTOs or player identities.

            def reply(self, code, payload):
                data = json.dumps(payload).encode("utf-8")
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(data)

            def authenticated(self):
                actual = self.headers.get("Authorization", "").encode("utf-8")
                if not hmac.compare_digest(actual, expected):
                    self.reply(401, {"error": "unauthorized"})
                    return False
                return True

            def do_GET(self):
                if not self.authenticated():
                    return
                if self.path != "/v1/status":
                    self.reply(404, {"error": "unknown route"})
                    return
                try:
                    self.reply(200, worker.status())
                except RuntimeError:
                    self.reply(409, {"error": "worker unavailable"})

            def do_POST(self):
                if not self.authenticated():
                    return
                routes = {"/v1/events", "/v1/jellyfin-events", "/v1/pause", "/v1/resume", "/v1/retry"}
                if self.path not in routes:
                    self.reply(404, {"error": "unknown route"})
                    return
                try:
                    length = int(self.headers.get("Content-Length", "-1"))
                    if not 0 <= length <= 1048576:
                        self.reply(413, {"error": "body length required; maximum 1 MiB"})
                        return
                    if self.headers.get("Content-Type", "").split(";")[0].strip() != "application/json":
                        self.reply(415, {"error": "JSON required"})
                        return
                    body = self.rfile.read(length)
                    if len(body) != length:
                        raise ValueError("incomplete body")
                    payload = json.loads(body)
                    if not isinstance(payload, dict):
                        raise ValueError("body must be an object")
                    if self.path == "/v1/events":
                        result = worker.event(payload)
                    elif self.path == "/v1/jellyfin-events":
                        result = worker.event(normalized_event(payload, media_root))
                    elif self.path in ("/v1/pause", "/v1/resume"):
                        worker.pause(self.path == "/v1/pause")
                        result = {"paused": self.path == "/v1/pause"}
                    else:
                        result = worker.retry(payload.get("path"))
                    self.reply(200, result)
                except (ValueError, TypeError, KeyError, UnicodeError):
                    self.reply(400, {"error": "invalid event or control body"})
                except RuntimeError:
                    self.reply(409, {"error": "worker unavailable"})

        self.server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
        self.server.daemon_threads = False
        self.server.block_on_close = True
        self._thread = None

    @property
    def address(self):
        return self.server.server_address

    def start(self):
        if self._thread:
            raise RuntimeError("control server already started")
        self._thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.1),
                                        name="cache-control", daemon=True)
        self._thread.start()

    def close(self):
        if self._thread:
            self.server.shutdown()
            self._thread.join(5)
        self.server.server_close()
