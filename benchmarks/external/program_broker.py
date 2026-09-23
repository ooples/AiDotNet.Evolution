"""Loopback-only benchmark service with independent sequential admission.

Only trusted optimizer adapters receive its random capability. Candidate evaluation
must be isolated by the supplied evaluator; never give that capability to candidates.
"""
from __future__ import annotations

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
import secrets
import socket
import threading
import time
import urllib.request
from urllib.parse import urlsplit

from program_controls import candidate_hash

MAX_MESSAGE = 256 * 1024
# Both transports' token metrics; one campaign uses one transport, so arms never mix them.
COST_METRICS = ("reported_input_plus_output_tokens", "reported_input_plus_cache_plus_output_tokens")
CHAT_PATH = "/v1/chat/completions"
# Accepted and recorded, never honoured: the subscription CLI exposes no sampling controls,
# so they are identical (the CLI default) for every arm and are declared as such.
IGNORED_SAMPLING = ("temperature", "top_p", "max_tokens", "max_completion_tokens", "seed", "reasoning_effort")


def chat_request(body):
    """An OpenAI chat-completions body as a broker model payload; anything unhonourable is refused."""
    if not isinstance(body, dict) or set(body) - {"model", "messages", "stream", "n", *IGNORED_SAMPLING}:
        raise ValueError("Unsupported chat-completions fields")
    model, messages = body.get("model"), body.get("messages")
    if not isinstance(model, str) or not 0 < len(model) <= 100 or any(char.isspace() for char in model):
        raise ValueError("Invalid model name")
    if body.get("stream", False) is not False or body.get("n", 1) != 1:
        raise ValueError("Streaming and multiple choices are outside the one-request contract")
    if (not isinstance(messages, list) or not messages or
            any(not isinstance(m, dict) or set(m) != {"role", "content"} or not isinstance(m["content"], str) or
                m["role"] not in ("system", "user", "assistant") for m in messages)):
        raise ValueError("Messages must be role/content text")
    system = ""
    if messages[0]["role"] == "system":
        system, messages = messages[0]["content"], messages[1:]
    if not messages or any(m["role"] == "system" for m in messages):
        raise ValueError("Only one leading system message is supported")
    return {"system": system, "messages": messages, "model": model,
            "ignored_sampling": {name: body[name] for name in IGNORED_SAMPLING if name in body}}


def chat_response(model, text, sequence):
    return {"id": f"evolution-{sequence}", "object": "chat.completion", "created": 0, "model": model,
            "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": text}}]}


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
        self.attempted = {"model": 0, "evaluate": 0}
        self.unknown_attempts = {"model": 0, "evaluate": 0}
        self.evaluation_seconds = 0.0
        self.has_valid_evaluation = False
        self.capability = secrets.token_hex(32)
        # Every authenticated chat request, counted before it is parsed, so a refused prompt
        # is still visible: received minus recorded model rows is the unrecorded-prompt count.
        self.chat_received = 0
        broker = self

        class Handler(BaseHTTPRequestHandler):
            # Keep-alive: a new loopback connect() costs ~15 ms p95 on Windows (measured; the
            # handler itself is ~0.02 ms), and OpenEvolve's pooled client reuses connections.
            protocol_version = "HTTP/1.1"

            def setup(self):
                super().setup()
                self.connection.settimeout(5)
                # Headers and body go out as separate small writes; with Nagle on, loopback
                # delayed-ACK stalls put a multi-millisecond tail on every call.
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            def log_message(self, *args):
                pass

            def do_POST(self):
                if not secrets.compare_digest(self.headers.get("Authorization", ""), "Bearer " + broker.capability):
                    self.send_error(403)
                    return
                if self.path == CHAT_PATH:
                    broker.chat_received += 1
                try:
                    length = int(self.headers.get("Content-Length", "-1"))
                    if not 0 <= length <= MAX_MESSAGE or self.headers.get("Transfer-Encoding"):
                        raise ValueError("Invalid request framing")
                    raw = self.rfile.read(length)
                    if len(raw) != length:
                        raise ValueError("Truncated request")
                    if self.path == CHAT_PATH:
                        with broker._admission:  # the row read must belong to this dispatch
                            text = broker.dispatch("model", chat_request(json.loads(raw)))
                            body = chat_response(broker.rows[-1]["request"]["model"], text, broker.rows[-1]["sequence"])
                    else:
                        body = {"status": "ok", "result": broker.dispatch(self.path.removeprefix("/"), json.loads(raw))}
                    payload = json.dumps(body, allow_nan=False).encode()
                    if len(payload) > MAX_MESSAGE:
                        raise ValueError("Oversized response")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                except Exception:
                    self.send_error(400, "Benchmark request failed")

        # Threaded so an idle keep-alive connection cannot block another client's request;
        # admission stays strictly sequential under _admission.
        self._admission = threading.RLock()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
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
        with self._admission:
            return self._dispatch(operation, payload)

    def _dispatch(self, operation, payload):
        if (operation not in self.limits or self.closed or time.monotonic() - self.started >= self.seconds
                or self.attempted[operation] >= self.limits[operation]):
            raise ValueError("Independent broker admission closed")
        if not isinstance(payload, dict):
            raise ValueError("Expected object payload")
        if operation == "evaluate":
            identity = candidate_hash(payload["code"])
            if self.attempted["evaluate"] == 0 and identity != self.initial_hash:
                raise ValueError("Optimizer changed the initial program")
        elif (not self.has_valid_evaluation
              or self.attempted["evaluate"] >= self.limits["evaluate"]
              or self.model_tokens >= self.model_token_cap):
            raise ValueError("A valid shared start and remaining evaluation capacity are required")
        self.attempted[operation] += 1
        row = {"operation": operation, "request": payload, "status": "dispatched", "result": None,
               "sequence": sum(self.attempted.values()), "started_elapsed_seconds": time.monotonic() - self.started}
        self.rows.append(row)
        known_work = False
        try:
            if operation == "model":
                if "model" in payload:
                    measured = self.generate(payload["system"], payload["messages"], model=payload["model"])
                else:
                    measured = self.generate(payload["system"], payload["messages"])
                if (not isinstance(measured, dict) or not isinstance(measured.get("text"), str)
                        or len(measured["text"].encode()) > 64 * 1024
                        or type(measured.get("cost_units")) is not int or measured["cost_units"] < 0
                        or measured.get("cost_metric") not in COST_METRICS):
                    raise ValueError("Missing bounded model response or independently reported token cost")
                self.model_tokens += measured["cost_units"]
                known_work = True
                row["model_usage"] = {"cost_units": measured["cost_units"], "cost_metric": measured["cost_metric"]}
                result = measured["text"]
                if self.model_tokens > self.model_token_cap:
                    row.update(status="budget-exceeded", result=result)
                    raise ValueError("Actual token cost exceeded the declared cap; result is not admissible")
            else:
                result = self.evaluate(payload["code"])
                if (isinstance(result, dict) and result.get("unknown_work") is False and
                        type(result.get("work_units")) in (int, float) and math.isfinite(result["work_units"]) and result["work_units"] >= 0):
                    self.evaluation_seconds += result["work_units"]
                    known_work = True
                if (result.get("candidate_hash") != identity or result.get("unknown_work") is not False
                        or result.get("status") not in ("valid", "invalid")
                        or type(result.get("work_units")) not in (int, float)
                        or not math.isfinite(result["work_units"]) or result["work_units"] < 0):
                    raise ValueError("Evaluator did not reconcile the dispatched program")
                if result["status"] == "valid" and (type(result.get("quality")) not in (int, float)
                        or not math.isfinite(result["quality"]) or result["quality"] <= -1e299):
                    raise ValueError("Valid score must be finite")
                if result["status"] == "valid":
                    self.has_valid_evaluation = True
            row.update(status="completed", result=result)
            return result
        except Exception:
            self.closed = True
            if row["status"] == "dispatched":
                row["status"] = "unknown"
            raise
        finally:
            if not known_work:
                self.unknown_attempts[operation] += 1
            row.update(finished_elapsed_seconds=time.monotonic() - self.started,
                       model_tokens_after=self.model_tokens, evaluation_seconds_after=self.evaluation_seconds)
