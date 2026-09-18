import unittest

from program_broker import ProgramBroker
from program_controls import candidate_hash


class IndependentCounterTests(unittest.TestCase):
    def broker(self, generate, evaluate, tokens=100):
        broker = ProgramBroker(generate, evaluate, model_calls=2, evaluations=3, seconds=30, initial="initial", model_tokens=tokens)
        self.addCleanup(broker.server.server_close)
        return broker

    def evaluate(self, code):
        return dict(candidate_hash=candidate_hash(code), status="valid", quality=1, work_units=2, unknown_work=False)

    def generate(self, system, messages):
        return dict(text="candidate", cost_units=10, cost_metric="reported_input_plus_output_tokens")

    def test_independent_totals_survive_trace_record_loss(self):
        broker = self.broker(self.generate, self.evaluate)
        broker.dispatch("evaluate", {"code": "initial"})
        broker.dispatch("model", {"system": "task", "messages": []})
        self.assertEqual([1, 2], [r["sequence"] for r in broker.rows])
        broker.rows.clear()
        self.assertEqual({"model": 1, "evaluate": 1}, broker.attempted)
        self.assertEqual(10, broker.model_tokens)
        self.assertEqual(2, broker.evaluation_seconds)
        broker.dispatch("model", {"system": "task", "messages": []})
        broker.rows.clear()
        with self.assertRaises(ValueError):
            broker.dispatch("model", {"system": "task", "messages": []})

    def test_unknown_model_attempt_is_independently_counted(self):
        def fail(*args):
            raise RuntimeError("unknown provider work")
        broker = self.broker(fail, self.evaluate)
        broker.dispatch("evaluate", {"code": "initial"})
        with self.assertRaises(RuntimeError):
            broker.dispatch("model", {"system": "task", "messages": []})
        self.assertEqual(1, broker.unknown_attempts["model"])

    def test_known_work_survives_invalid_quality(self):
        def evaluate(code):
            return {**self.evaluate(code), "quality": float("inf")}
        broker = self.broker(self.generate, evaluate)
        with self.assertRaises(ValueError):
            broker.dispatch("evaluate", {"code": "initial"})
        self.assertEqual(2, broker.evaluation_seconds)
        self.assertEqual(0, broker.unknown_attempts["evaluate"])

    def test_token_overrun_is_known_work_not_erased(self):
        broker = self.broker(self.generate, self.evaluate, tokens=5)
        broker.dispatch("evaluate", {"code": "initial"})
        with self.assertRaises(ValueError):
            broker.dispatch("model", {"system": "task", "messages": []})
        self.assertEqual(10, broker.model_tokens)
        self.assertEqual(0, broker.unknown_attempts["model"])
