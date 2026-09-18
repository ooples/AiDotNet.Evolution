"""Contract tests only. Never run a Codex generation or consume subscription allowance."""
import copy
import json
from pathlib import Path
from types import SimpleNamespace
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from codex_transport import CodexTransport, MAX_OUTPUT, parse_events


class CodexTransportTests(unittest.TestCase):
    def events(self):
        return [{"type": "thread.started", "thread_id": "fixture"}, {"type": "turn.started"},
                {"type": "item.completed", "item": {"type": "agent_message", "text": "candidate"}},
                {"type": "turn.completed", "usage": {"input_tokens": 20, "cached_input_tokens": 4, "output_tokens": 3}}]

    def encode(self, events):
        return "\n".join(json.dumps(event) for event in events).encode()

    def test_exact_single_text_response_requires_reconciled_usage(self):
        text, usage = parse_events(self.encode(self.events()))
        self.assertEqual("candidate", text)
        self.assertEqual(20, usage["input_tokens"])

    def test_missing_usage_failed_turn_tools_and_multiple_responses_fail_closed(self):
        events = self.events()
        variants = [events[:-1], events + [events[-1]], events + [{"type": "turn.failed"}],
                    events + [{"type": "future_protocol_event"}],
                    events + [{"type": "item.started", "item": {"type": "command_execution"}}],
                    events[:2] + [events[2], events[2]] + events[3:]]
        for variant in variants:
            with self.subTest(variant=variant), self.assertRaises(ValueError):
                parse_events(self.encode(variant))

    def test_usage_cannot_be_negative_boolean_missing_or_excess_cached(self):
        for key, value in (("input_tokens", -1), ("output_tokens", True), ("cached_input_tokens", 21)):
            events = copy.deepcopy(self.events())
            events[-1]["usage"][key] = value
            with self.assertRaises(ValueError):
                parse_events(self.encode(events))

    def test_oversized_events_rejected(self):
        with self.assertRaises(ValueError):
            parse_events(b" " * (MAX_OUTPUT + 1))

    def test_api_login_refused_without_generation_and_environment_keys_removed(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict("os.environ", {"OPENAI_API_KEY": "test-only", "CODEX_API_KEY": "test-only"}):
            transport = CodexTransport(sys.executable, "explicit-fixture-model", Path(directory) / "evidence", 1)
            self.assertNotIn("OPENAI_API_KEY", transport.environment)
            self.assertNotIn("CODEX_API_KEY", transport.environment)
            auth = subprocess.CompletedProcess([], 0, b"", b"Logged in using API key")
            with patch("codex_transport.subprocess.run", return_value=auth) as run, patch("codex_transport.subprocess.Popen") as spawn:
                with self.assertRaisesRegex(ValueError, "requires an existing ChatGPT login"):
                    transport.generate("system", [{"role": "user", "content": "candidate"}])
                self.assertEqual(1, run.call_count)
                spawn.assert_not_called()

    def test_budget_closed_unknown_work_and_concurrency_refuse_before_auth(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = CodexTransport(sys.executable, "explicit-fixture-model", Path(directory) / "evidence", 1)
            with patch("codex_transport.subprocess.run") as run:
                transport.calls = 1
                with self.assertRaises(ValueError):
                    transport.generate("system", [])
                transport.calls = 0
                transport.failed = True
                with self.assertRaises(ValueError):
                    transport.generate("system", [])
                transport._lock.acquire()
                try:
                    with self.assertRaisesRegex(ValueError, "Concurrent"):
                        transport.generate("system", [])
                finally:
                    transport._lock.release()
                run.assert_not_called()

    def test_success_retains_workspace_and_forces_subscription_without_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = CodexTransport(sys.executable, "explicit-fixture-model", Path(directory) / "evidence", 1)
            auth = subprocess.CompletedProcess([], 0, b"Logged in using ChatGPT", b"")
            version = subprocess.CompletedProcess([], 0, b"codex-cli 0.154.0", b"")

            def completed(command, **kwargs):
                self.assertIn('forced_login_method="chatgpt"', command)
                self.assertIn("--ignore-user-config", command)
                kwargs["stdout"].write(self.encode(self.events()))
                return SimpleNamespace(poll=lambda: 0, returncode=0)

            with patch("codex_transport.subprocess.run", side_effect=[auth, version]), \
                    patch("codex_transport.subprocess.Popen", side_effect=completed) as spawn, \
                    patch("codex_transport.tempfile.TemporaryDirectory") as workspace:
                workspace.return_value.__enter__.return_value = directory
                measured = transport.generate_metered("system", [{"role": "user", "content": "fixture"}])
                self.assertEqual("candidate", measured["text"])
                self.assertEqual(23, measured["cost_units"])
                self.assertEqual("reported_input_plus_output_tokens", measured["cost_metric"])
                self.assertFalse(workspace.call_args.kwargs["delete"])
                spawn.assert_called_once()
            receipt = json.loads((Path(directory) / "evidence/0/receipt.json").read_text())
            self.assertEqual("completed", receipt["status"])
            self.assertFalse(receipt["unknown_usage"])
            self.assertEqual(directory, receipt["retained_workspace"])
            self.assertEqual(3, receipt["usage"]["output_tokens"])


if __name__ == "__main__":
    unittest.main()
