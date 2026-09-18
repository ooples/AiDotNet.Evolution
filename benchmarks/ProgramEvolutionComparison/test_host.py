"""Offline transport/engine contracts; no model API or generated-code execution."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ASSEMBLY = Path(__file__).resolve().parent / "bin/Release/net10.0/ProgramEvolutionComparison.dll"
CAPABILITY = "a" * 64
DESCRIPTION = "Improve the fixture without changing its interface."
SEED = "def solve(x):\n    return x\n"


class HostTests(unittest.TestCase):
    def test_real_shared_broker_admits_and_accounts_the_standalone_host(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "external"))
        from program_broker import ProgramBroker
        calls = []

        def generate(system, messages):
            calls.append((system, messages))
            return {"text": f"\u0060\u0060\u0060python\ndef solve(x):\n    return x + {len(calls)}\n\n\u0060\u0060\u0060",
                    "cost_units": 7, "cost_metric": "reported_input_plus_output_tokens"}

        def evaluate(code):
            return {"candidate_hash": hashlib.sha256(code.encode()).hexdigest(), "unknown_work": False,
                    "status": "valid", "work_units": 1, "quality": float(len(code))}

        with tempfile.TemporaryDirectory(prefix="evolution-real-broker-") as directory:
            root = Path(directory)
            (root / "seed.py").write_text(SEED, encoding="utf-8", newline="")
            (root / "task.txt").write_text(DESCRIPTION, encoding="utf-8", newline="")
            with ProgramBroker(generate, evaluate, model_calls=2, evaluations=3, seconds=30, initial=SEED) as broker:
                env = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=broker.endpoint,
                           EVOLUTION_BROKER_CAPABILITY=broker.capability)
                run = subprocess.run(["dotnet", str(ASSEMBLY), str(root / "seed.py"), str(root / "run.json"),
                                      "fixture-model", "2", "7", str(root / "task.txt"), "controlled"],
                                     env=env, capture_output=True, text=True, timeout=30)
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual(2, len(calls))
                self.assertEqual(14, broker.model_tokens)
                self.assertEqual(3, broker.evaluation_seconds)
                self.assertEqual(5, len(broker.rows))
                self.assertTrue(all(row["status"] == "completed" for row in broker.rows))

    def run_host(self, mode="controlled", fault=None, existing=False):
        records = []

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                self.assert_authorization()
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 256 * 1024:
                    self.send_error(400)
                    return
                payload = json.loads(self.rfile.read(length))
                records.append((self.path, payload))
                if self.path == "/model":
                    count = sum(path == "/model" for path, _ in records)
                    result = f"\u0060\u0060\u0060python\ndef solve(x):\n    return x + {count}\n\n\u0060\u0060\u0060"
                else:
                    code = payload["code"]
                    digest = hashlib.sha256(code.encode()).hexdigest()
                    result = {
                        "candidate_hash": "0" * 64 if fault == "hash" else digest,
                        "unknown_work": fault == "unknown",
                        "status": "broken" if fault == "status" else "valid",
                        "work_units": -1 if fault == "negative-work" else 1,
                        "quality": float(len(code)),
                    }
                body = json.dumps({"status": "ok", "result": result}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def assert_authorization(self):
                if self.headers.get("Authorization") != "Bearer " + CAPABILITY:
                    raise AssertionError("Missing capability")

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            with tempfile.TemporaryDirectory(prefix="evolution-host-") as directory:
                root = Path(directory)
                source = root / "initial.py"
                description = root / "task.txt"
                output = root / "result.json"
                source.write_text(SEED, encoding="utf-8", newline="")
                description.write_text(DESCRIPTION, encoding="utf-8", newline="")
                if existing:
                    output.write_text("keep", encoding="utf-8")
                env = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=f"http://127.0.0.1:{server.server_port}/",
                           EVOLUTION_BROKER_CAPABILITY=CAPABILITY)
                if fault == "endpoint":
                    env["EVOLUTION_BROKER_ENDPOINT"] = "http://example.invalid/"
                elif fault == "capability":
                    env["EVOLUTION_BROKER_CAPABILITY"] = ""
                run = subprocess.run(["dotnet", str(ASSEMBLY), str(source), str(output),
                                      "fixture-model", "2", "7", str(description), mode],
                                     env=env, capture_output=True, text=True, timeout=30)
                text = output.read_text(encoding="utf-8") if output.exists() else None
                return run, text, records
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=5)

    def test_controlled_and_native_tracks_use_the_real_model_runtime(self):
        for mode in ("controlled", "native-bounded"):
            with self.subTest(mode=mode):
                run, text, records = self.run_host(mode)
                self.assertEqual(0, run.returncode, run.stderr)
                report = json.loads(text)
                self.assertEqual(mode, report["mode"])
                self.assertEqual(3, len(report["evaluations"]))
                self.assertEqual(2, report["usage"]["ChatCalls"])
                self.assertEqual(2, sum(path == "/model" for path, _ in records))
                self.assertEqual(3, sum(path == "/evaluate" for path, _ in records))
                self.assertIn("AiDotNet.Evolution.Programs.dll", report["artifacts"])
                self.assertNotIn("AiDotNet.dll", report["artifacts"])
                model = next(payload for path, payload in records if path == "/model")
                if mode == "controlled":
                    self.assertEqual("Optimize the supplied Python program for this task. Return the complete program in a python code fence. "
                                     "Preserve its interface and correctness. Do not access tools or evaluate it yourself.\nTask:\n" + DESCRIPTION,
                                     model["system"])
                    self.assertEqual("Parent program:\n\u0060\u0060\u0060python\n" + SEED + "\n\u0060\u0060\u0060",
                                     model["messages"][0]["content"])
                else:
                    self.assertIn("solve", model["messages"][0]["content"])

    def test_unreconciled_or_invalid_receipts_cannot_report_success(self):
        for fault in ("hash", "unknown", "status", "negative-work"):
            with self.subTest(fault=fault):
                run, text, records = self.run_host(fault=fault)
                self.assertNotEqual(0, run.returncode)
                self.assertEqual("failed", json.loads(text)["status"])
                self.assertFalse(any(path == "/model" for path, _ in records))

    def test_existing_output_and_invalid_admission_do_not_dispatch(self):
        run, text, records = self.run_host(existing=True)
        self.assertNotEqual(0, run.returncode)
        self.assertEqual("keep", text)
        self.assertEqual([], records)
        for fault in ("endpoint", "capability"):
            with self.subTest(fault=fault):
                run, _, records = self.run_host(fault=fault)
                self.assertNotEqual(0, run.returncode)
                self.assertEqual([], records)


if __name__ == "__main__":
    unittest.main(verbosity=2)
