"""The reference worker serving OpenEvolve-contract evaluators (evaluate(program_path)) against the real host."""
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

# AIDOTNET_PYTHON_INSTALLED=1 tests the installed wheel instead of the source tree (the packaging check in CI).
if os.environ.get("AIDOTNET_PYTHON_INSTALLED") != "1":
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from aidotnet_evolution import DurableWorkClient, engine_program_source, load_openevolve_evaluator, raw_program_source, serve
from aidotnet_evolution.reference_worker import _normalize

ROOT = Path(__file__).resolve().parents[3]
DLL = os.environ.get("AIDOTNET_DURABLE_HOST_DLL") or str(ROOT / "src/AiDotNet.Evolution.Host/bin/Release/net10.0/aidotnet-evolution-host.dll")
HOST = os.environ.get("AIDOTNET_DURABLE_HOST_PATH")
TRANSPORT = {"host_path": HOST} if HOST else {"host_path": "dotnet", "host_args": [DLL]}

# Both OpenEvolve return shapes: a metrics dict, and an EvaluationResult-like object with metrics and artifacts.
DICT_EVALUATOR = '''
import importlib.util

def evaluate(program_path):
    spec = importlib.util.spec_from_file_location("candidate", program_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    value = module.run()
    if value < 0:
        raise ValueError("negative")
    return {"combined_score": float(value), "path_is_file": program_path.endswith(".py")}
'''
RESULT_EVALUATOR = '''
class EvaluationResult:
    def __init__(self, metrics, artifacts):
        self.metrics, self.artifacts = metrics, artifacts

def evaluate(program_path):
    with open(program_path, encoding="utf-8") as handle:
        source = handle.read()
    return EvaluationResult({"combined_score": float(len(source)), "inf": float("inf")}, {"stderr": "none"})
'''


def envelope(evaluation_id, source):
    """The payload the engine bridge enqueues: a versioned envelope around the encoded genome."""
    return json.dumps({"schema": 1, "genomePayload": source, "canonicalGenomeId": "program:" + evaluation_id,
                       "evaluationId": evaluation_id, "attempt": 1, "rootSeed": "7", "seedStream": "0"})


class PayloadTests(unittest.TestCase):
    def test_the_engine_envelope_yields_the_genome_source_and_raw_payloads_pass_through(self):
        self.assertEqual("def run():\n    return 1\n", engine_program_source(envelope("1", "def run():\n    return 1\n")))
        self.assertEqual("x = 1\n", raw_program_source("x = 1\n"))
        with self.assertRaises(Exception):
            engine_program_source("x = 1\n")

    def test_array_metrics_and_artifacts_are_recorded_not_turned_into_failures(self):
        try:
            import numpy
        except ImportError:
            self.skipTest("numpy is not installed")
        result = _normalize({"combined_score": numpy.float64(2.5), "flag": numpy.bool_(True), "curve": numpy.array([1.0, 2.0])})
        self.assertEqual(2.5, result["metrics"]["combined_score"])
        self.assertIs(True, result["metrics"]["flag"])
        self.assertIsInstance(result["metrics"]["curve"], str)
        json.dumps(result, allow_nan=False)


def job(evaluation_id, source):
    return {"evaluationId": evaluation_id, "attempt": 1, "canonicalGenomeId": "program:" + evaluation_id,
            "payload": source, "estimated": {"cost": "1"}, "maximum": {"cost": "1"}}


@unittest.skipUnless(HOST or Path(DLL).exists(), "Build src/AiDotNet.Evolution.Host (Release, net10.0) first")
class ReferenceWorkerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp(prefix="reference-worker-")
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.config = {"directory": os.path.join(self.directory, "work"), "runId": "run", "compatibilityHash": "compat",
                       "limits": {"cost": "100"}}
        self.worker = {"workerId": "reference-1", "compatibilityHash": "compat"}

    def evaluator(self, text):
        path = Path(self.directory) / "evaluator.py"
        path.write_text(text, encoding="utf-8")
        return load_openevolve_evaluator(path)

    def test_every_lease_is_committed_once_and_failures_are_receipts_not_crashes(self):
        evaluate = self.evaluator(DICT_EVALUATOR)
        with DurableWorkClient(self.config, **TRANSPORT) as work:
            for index, value in enumerate([3, 5, -1], 1):
                self.assertTrue(work.enqueue(job(str(index), f"def run():\n    return {value}\n")))
            self.assertEqual(["accepted"] * 3, serve(work, self.worker, evaluate, provenance="dict-evaluator-v1", actual={"cost": "1"}, source_from=raw_program_source))
            self.assertEqual([], serve(work, self.worker, evaluate, provenance="dict-evaluator-v1", actual={"cost": "1"}, source_from=raw_program_source))
            first = work.result("1", 1)
            self.assertEqual("completed", first["outcome"])
            self.assertEqual({"combined_score": 3.0, "path_is_file": True}, json.loads(first["payload"])["metrics"])
            failed = work.result("3", 1)
            self.assertEqual("failed", failed["outcome"])
            self.assertEqual({"error": "ValueError"}, json.loads(failed["payload"]))

    def test_evaluation_result_objects_keep_metrics_and_artifacts_and_max_leases_bounds_the_loop(self):
        evaluate = self.evaluator(RESULT_EVALUATOR)
        with DurableWorkClient(self.config, **TRANSPORT) as work:
            work.enqueue(job("1", "x = 1\n"))
            work.enqueue(job("2", "y = 22\n"))
            self.assertEqual(["accepted"], serve(work, self.worker, evaluate, provenance="result-v1", actual={"cost": "1"}, max_leases=1, source_from=raw_program_source))
            receipt = json.loads(work.result("1", 1)["payload"])
            self.assertEqual({"combined_score": 6.0, "inf": "inf"}, receipt["metrics"])  # non-finite floats survive as text
            self.assertEqual({"stderr": "none"}, receipt["artifacts"])
            self.assertIsNone(work.result("2", 1))

    def test_an_evaluator_without_evaluate_is_refused_at_load(self):
        with self.assertRaises(AttributeError):
            self.evaluator("def score(path):\n    return {}\n")


    def test_leases_enqueued_by_the_engine_bridge_are_evaluated_as_their_genome_source(self):
        evaluate = self.evaluator(DICT_EVALUATOR)
        with DurableWorkClient(self.config, **TRANSPORT) as work:
            self.assertTrue(work.enqueue(dict(job("1", ""), payload=envelope("1", "def run():\n    return 4\n"))))
            self.assertEqual(["accepted"], serve(work, self.worker, evaluate, provenance="engine-v1", actual={"cost": "1"}))
            self.assertEqual(4.0, json.loads(work.result("1", 1)["payload"])["metrics"]["combined_score"])


if __name__ == "__main__":
    unittest.main()
