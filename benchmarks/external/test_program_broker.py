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


class ChatShimTests(unittest.TestCase):
    def post(self, broker, body, capability=None):
        import json
        import urllib.request
        query = urllib.request.Request(broker.endpoint + "/v1/chat/completions", data=json.dumps(body).encode(),
                                       headers={"Authorization": "Bearer " + (capability or broker.capability),
                                                "Content-Type": "application/json"})
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(query, timeout=10) as response:
            return json.loads(response.read())

    def test_translation_keeps_roles_records_sampling_and_refuses_the_unhonourable(self):
        from program_broker import chat_request
        payload = chat_request({"model": "haiku", "temperature": 0.4, "max_tokens": 128000,
                                "messages": [{"role": "system", "content": "S"}, {"role": "user", "content": "U"}]})
        self.assertEqual({"system": "S", "messages": [{"role": "user", "content": "U"}], "model": "haiku",
                          "ignored_sampling": {"temperature": 0.4, "max_tokens": 128000}}, payload)
        user = [{"role": "user", "content": "U"}]
        for body in ({"model": "haiku", "messages": user, "stream": True}, {"model": "haiku", "messages": user, "n": 2},
                     {"model": "haiku", "messages": user, "tools": []}, {"model": "has space", "messages": user},
                     {"model": "haiku", "messages": []}, {"model": "haiku", "messages": [{"role": "tool", "content": "x"}]},
                     {"model": "haiku", "messages": user + [{"role": "system", "content": "late"}]},
                     {"model": "haiku", "messages": [{"role": "system", "content": "only"}]}):
            with self.subTest(body=body), self.assertRaises(ValueError):
                chat_request(body)

    def test_openai_shaped_round_trip_is_metered_and_counted(self):
        initial = "def solve(x):\n    return x\n"
        generate = Mock(return_value={"text": "reply", "cost_units": 5,
                                      "cost_metric": "reported_input_plus_cache_plus_output_tokens"})
        evaluate = lambda code: dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0,
                                     work_units=1, unknown_work=False)
        with ProgramBroker(generate, evaluate, model_calls=2, evaluations=3, seconds=30, initial=initial) as broker:
            request(broker.endpoint, broker.capability, "evaluate", {"code": initial})
            body = {"model": "sonnet", "messages": [{"role": "system", "content": "S"}, {"role": "user", "content": "U"}]}
            response = self.post(broker, body)
            self.assertEqual("reply", response["choices"][0]["message"]["content"])
            self.assertEqual("sonnet", response["model"])
            generate.assert_called_once_with("S", [{"role": "user", "content": "U"}], model="sonnet")
            # 403 is sent before the body is read, so Windows may abort the upload instead.
            with self.assertRaises((urllib.error.HTTPError, ConnectionError)):
                self.post(broker, body, capability="0" * 64)
            with self.assertRaises(urllib.error.HTTPError):
                self.post(broker, dict(body, stream=True))
            # The forged-capability request is refused before counting; the refused stream is counted.
            self.assertEqual((2, 1, 5), (broker.chat_received, sum(r["operation"] == "model" for r in broker.rows),
                                         broker.model_tokens))

class KeepAliveTests(unittest.TestCase):
    def test_a_keep_alive_client_that_exits_is_a_close_not_a_server_error(self):
        import subprocess
        import sys
        initial = "def solve(x):\n    return x\n"
        child = ("import http.client, json, os, sys\n"
                 "c = http.client.HTTPConnection('127.0.0.1', int(sys.argv[1]))\n"
                 "c.request('POST', '/evaluate', json.dumps({'code': sys.argv[3]}).encode(), {'Authorization': 'Bearer ' + sys.argv[2]})\n"
                 "c.getresponse().read()\n"
                 "os._exit(0)\n")
        evaluate = lambda code: dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0,
                                     work_units=0, unknown_work=False)
        with ProgramBroker(Mock(), evaluate, model_calls=1, evaluations=2, seconds=30, initial=initial) as broker:
            errors = Mock()
            broker.server.handle_error = errors
            subprocess.run([sys.executable, "-c", child, broker.endpoint.rsplit(":", 1)[1], broker.capability, initial],
                           check=True, timeout=30)
            import time
            time.sleep(0.5)
            errors.assert_not_called()
            self.assertEqual(["completed"], [row["status"] for row in broker.rows])

if __name__ == "__main__":
    unittest.main()
