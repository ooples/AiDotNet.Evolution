"""Loopback-only benchmark service with independent sequential admission.

Only trusted optimizer adapters receive its random capability. Candidate evaluation
must be isolated by the supplied evaluator; never give that capability to candidates.
"""
from __future__ import annotations

from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import math
import secrets
import threading
import time
import urllib.request
from urllib.parse import urlsplit

from program_controls import candidate_hash

MAX_MESSAGE = 256 * 1024


def request(endpoint, capability, operation, payload):
    address = urlsplit(endpoint)
    if (address.scheme != "http" or address.hostname != "127.0.0.1" or address.port is None
            or address.username is not None or address.password is not None or address.path
            or address.query or address.fragment or operation not in ("model", "evaluate")):
        raise ValueError("Only the local benchmark broker is permitted")
    data = json.dumps(payload, allow_nan=False).encode()
    if len(data) > MAX_MESSAGE:
        raise ValueError("Broker request exceeds its bound")
    query = urllib.request.Request(endpoint + "/" + operation, data=data,
                                   headers={"Authorization": "Bearer " + capability, "Content-Type": "application/json"})
    # Bypass ambient proxy settings: this capability must never leave loopback.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            raise ValueError("Benchmark broker redirects are forbidden")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(query, timeout=310) as response:
        raw = response.read(MAX_MESSAGE + 1)
    if len(raw) > MAX_MESSAGE:
        raise ValueError("Broker response exceeds its bound")
    result = json.loads(raw)
    if result.get("status") != "ok":
        raise ValueError("Benchmark broker rejected work")
    return result["result"]


class ProgramBroker:
    def __init__(self, generate, evaluate, *, model_calls, evaluations, seconds, initial, model_tokens=100000):
        if (type(model_calls) is not int or not 1 <= model_calls <= 64
                or type(evaluations) is not int or not 2 <= evaluations <= 65
                or type(seconds) not in (int, float) or not math.isfinite(seconds) or not 0 < seconds <= 3600
                or type(model_tokens) is not int or not 1 <= model_tokens <= 10000000):
            raise ValueError("Invalid broker budget")
        self.initial_hash = candidate_hash(initial)
        self.generate, self.evaluate = generate, evaluate
        self.limits = {"model": model_calls, "evaluate": evaluations}
        self.seconds = seconds
        self.started = time.monotonic()
        self.rows = []
        self.closed = False
        self.model_tokens = 0
        self.model_token_cap = model_tokens
        self.capability = secrets.token_hex(32)
        broker = self

        class Handler(BaseHTTPRequestHandler):
            def setup(self):
                super().setup()
                self.connection.settimeout(5)

            def log_message(self, *args):
                pass

            def do_POST(self):
                if not secrets.compare_digest(self.headers.get("Authorization", ""), "Bearer " + broker.capability):
                    self.send_error(403)
                    return
                try:
                    length = int(self.headers.get("Content-Length", "-1"))
                    if not 0 <= length <= MAX_MESSAGE or self.headers.get("Transfer-Encoding"):
                        raise ValueError("Invalid request framing")
                    raw = self.rfile.read(length)
                    if len(raw) != length:
                        raise ValueError("Truncated request")
                    result = broker.dispatch(self.path.removeprefix("/"), json.loads(raw))
                    payload = json.dumps({"status": "ok", "result": result}, allow_nan=False).encode()
                    if len(payload) > MAX_MESSAGE:
                        raise ValueError("Oversized response")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                except Exception:
                    self.send_error(400, "Benchmark request failed")

        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.endpoint = "http://127.0.0.1:" + str(self.server.server_port)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *args):
        self.server.shutdown()
        self.thread.join(timeout=315)
        self.server.server_close()

    def dispatch(self, operation, payload):
        if (operation not in self.limits or self.closed or time.monotonic() - self.started >= self.seconds
                or sum(row["operation"] == operation for row in self.rows) >= self.limits[operation]):
            raise ValueError("Independent broker admission closed")
        if not isinstance(payload, dict):
            raise ValueError("Expected object payload")
        if operation == "evaluate":
            identity = candidate_hash(payload["code"])
            if not any(row["operation"] == "evaluate" for row in self.rows) and identity != self.initial_hash:
                raise ValueError("Optimizer changed the initial program")
        elif (not any(row["operation"] == "evaluate" and row["status"] == "completed"
                      and row["result"]["status"] == "valid" for row in self.rows)
              or sum(row["operation"] == "evaluate" for row in self.rows) >= self.limits["evaluate"]
              or self.model_tokens >= self.model_token_cap):
            raise ValueError("A valid shared start and remaining evaluation capacity are required")
        row = {"operation": operation, "request": payload, "status": "dispatched", "result": None}
        self.rows.append(row)
        try:
            if operation == "model":
                measured = self.generate(payload["system"], payload["messages"])
                if (not isinstance(measured, dict) or not isinstance(measured.get("text"), str)
                        or len(measured["text"].encode()) > 64 * 1024
                        or type(measured.get("cost_units")) is not int or measured["cost_units"] < 0
                        or measured.get("cost_metric") != "reported_input_plus_output_tokens"):
                    raise ValueError("Missing bounded model response or independently reported token cost")
                self.model_tokens += measured["cost_units"]
                row["model_usage"] = {"cost_units": measured["cost_units"], "cost_metric": measured["cost_metric"]}
                result = measured["text"]
                if self.model_tokens > self.model_token_cap:
                    row.update(status="budget-exceeded", result=result)
                    raise ValueError("Actual token cost exceeded the declared cap; result is not admissible")
            else:
                result = self.evaluate(payload["code"])
                if (result.get("candidate_hash") != identity or result.get("unknown_work") is not False
                        or result.get("status") not in ("valid", "invalid")
                        or type(result.get("work_units")) not in (int, float)
                        or not math.isfinite(result["work_units"]) or result["work_units"] < 0):
                    raise ValueError("Evaluator did not reconcile the dispatched program")
                if result["status"] == "valid" and (type(result.get("quality")) not in (int, float)
                        or not math.isfinite(result["quality"]) or result["quality"] <= -1e299):
                    raise ValueError("Valid score must be finite")
            row.update(status="completed", result=result)
            return result
        except Exception:
            self.closed = True
            if row["status"] == "dispatched":
                row["status"] = "unknown"
            raise
