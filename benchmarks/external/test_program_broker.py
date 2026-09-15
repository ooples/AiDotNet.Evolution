import unittest
from unittest.mock import Mock
import urllib.error

from program_broker import ProgramBroker, request
from program_controls import candidate_hash


class ProgramBrokerTests(unittest.TestCase):
    def evaluator(self, code):
        return dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0, work_units=1, unknown_work=False)

    def test_authenticated_loopback_initialization_and_independent_caps(self):
        generate = Mock(return_value={"text": "```python\npass\n```", "cost_units": 5, "cost_metric": "reported_input_plus_output_tokens"})
        with ProgramBroker(generate, self.evaluator, model_calls=1, evaluations=2, seconds=30, initial="pass") as broker:
            def call(operation, payload):
                return request(broker.endpoint, broker.capability, operation, payload)
            with self.assertRaises(urllib.error.HTTPError):
                request(broker.endpoint, "wrong", "evaluate", {"code": "pass"})
            with self.assertRaises(urllib.error.HTTPError):
                call("model", {"system": "s", "messages": []})
            with self.assertRaises(urllib.error.HTTPError):
                call("evaluate", {"code": "changed start"})
            self.assertEqual([], broker.rows)
            self.assertEqual("valid", call("evaluate", {"code": "pass"})["status"])
            self.assertIn("python", call("model", {"system": "s", "messages": []}))
            with self.assertRaises(urllib.error.HTTPError):
                call("model", {"system": "s", "messages": []})
            call("evaluate", {"code": "pass\n"})
            with self.assertRaises(urllib.error.HTTPError):
                call("evaluate", {"code": "pass\n"})
            self.assertEqual(3, len(broker.rows))
            generate.assert_called_once()
            self.assertEqual(5, broker.model_tokens)

    def test_token_overrun_is_charged_and_cannot_produce_an_admissible_result(self):
        generate = Mock(return_value={"text": "candidate", "cost_units": 11, "cost_metric": "reported_input_plus_output_tokens"})
        with ProgramBroker(generate, self.evaluator, model_calls=2, evaluations=3, seconds=30,
                           initial="pass", model_tokens=10) as broker:
            request(broker.endpoint, broker.capability, "evaluate", {"code": "pass"})
            with self.assertRaises(urllib.error.HTTPError):
                request(broker.endpoint, broker.capability, "model", {"system": "s", "messages": []})
            self.assertEqual(11, broker.model_tokens)
            self.assertEqual("budget-exceeded", broker.rows[-1]["status"])
            self.assertTrue(broker.closed)
            with self.assertRaises(urllib.error.HTTPError):
                request(broker.endpoint, broker.capability, "model", {"system": "s", "messages": []})
            generate.assert_called_once()

    def test_unknown_work_stops_all_later_dispatch(self):
        evaluate = Mock(side_effect=TimeoutError("fixture timeout"))
        with ProgramBroker(Mock(), evaluate, model_calls=1, evaluations=2, seconds=30, initial="pass") as broker:
            with self.assertRaises(urllib.error.HTTPError):
                request(broker.endpoint, broker.capability, "evaluate", {"code": "pass"})
            self.assertTrue(broker.closed)
            self.assertEqual("unknown", broker.rows[0]["status"])
            with self.assertRaises(urllib.error.HTTPError):
                request(broker.endpoint, broker.capability, "evaluate", {"code": "pass"})
            evaluate.assert_called_once()

    def test_nonlocal_endpoint_rejected(self):
        for endpoint in ("https://example.com", "http://localhost:1234", "http://192.168.1.1:1234",
                         "http://127.0.0.1:1234@evil.example", "http://127.0.0.1:1234/path"):
            with self.assertRaises(ValueError):
                request(endpoint, "capability", "model", {})


if __name__ == "__main__":
    unittest.main()
