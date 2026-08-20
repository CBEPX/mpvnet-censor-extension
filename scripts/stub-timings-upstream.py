#!/usr/bin/env python3
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlparse


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = urlparse(self.path).path
        print(path, flush=True)
        if path == "/api/public/timings/301":
            self.send_json({
                "kp_id": "301",
                "timings": [
                    {
                        "id": 1,
                        "timing_text": "00:01-00:02 сцена",
                        "status": "approved",
                        "vote_score": 1,
                    },
                    {
                        "id": 2,
                        "timing_text": "Чисто",
                        "status": "approved",
                        "vote_score": 0,
                    },
                ],
            })
            return
        if not path.startswith("/api/search/"):
            self.send_error(404)
            return

        query = unquote(path.removeprefix("/api/search/"))
        if query in {"negative-cache-probe", "private-query-failed"}:
            self.send_error(503)
            return
        if query == "upstream-rate-limit-probe":
            self.send_error(429)
            return
        if query == "not-found-probe":
            self.send_error(404)
            return
        if query == "redirect-probe":
            self.send_response(302)
            self.send_header(
                "Location",
                f"http://127.0.0.1:{self.server.server_port}/api/search/redirect-target",
            )
            self.end_headers()
            return
        if query == "coalesce-probe":
            time.sleep(0.2)
        if query.startswith("slow-probe-"):
            time.sleep(6)
        movies = []
        if query == "aggregate-success":
            movies = [{
                "id": 301,
                "title": "Матрица (1999)",
                "year": "1999",
                "raw_data": {"film_length": "02:16"},
            }]
        self.send_json(movies)

    def send_json(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format, *_args):
        pass


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
print(f"http://127.0.0.1:{server.server_port}", flush=True)
server.serve_forever()
