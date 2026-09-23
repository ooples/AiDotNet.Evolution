"""Contract tests. Only the opt-in live test (EVOLUTION_CLAUDE_EXECUTABLE) consumes subscription allowance."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from claude_transport import (CANARY_PROMPT, MAX_OUTPUT, PINNED_CLI, ClaudeTransport, ExclusiveWorkspace, parse_events,
                              reconcile_receipts,
                              scrubbed_environment, verified_executable)


def events(text="candidate"):
    return [
        {"type": "system", "subtype": "init", "model": "claude-haiku-4-5-20251001", "tools": [], "mcp_servers": [],
         "apiKeySource": "none", "plugins": [{"name": "telemetry", "path": "builtin"}]},
        {"type": "assistant", "message": {"content": [{"type": "thinking", "thinking": ""}, {"type": "text", "text": text}]}},
        {"type": "rate_limit_event", "rate_limit_info": {"status": "allowed"}},
        {"type": "result", "subtype": "success", "is_error": False, "num_turns": 1, "result": text,
         "duration_api_ms": 884,
         "usage": {"input_tokens": 389, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 42}},
    ]


def encode(stream):
    return "\n".join(json.dumps(event) for event in stream).encode()


class ParseTests(unittest.TestCase):
    def test_isolated_single_text_response_is_accepted_with_reconciled_usage(self):
        text, usage, init, notes = parse_events(encode(events()))
        self.assertEqual("candidate", text)
        self.assertEqual(389, usage["input_tokens"])
        self.assertEqual("claude-haiku-4-5-20251001", init["model"])
        self.assertEqual(0.884, notes["duration_api_seconds"])
        self.assertEqual(1, len(notes["rate_limit_events"]))

    def test_any_loaded_context_fails_closed(self):
        # Each is a way one arm could see context the other did not.
        for key, value in (("tools", ["Bash"]), ("mcp_servers", [{"name": "token-optimizer"}]),
                           ("apiKeySource", "ANTHROPIC_API_KEY"),
                           ("plugins", [{"name": "token-optimizer", "path": "C:/plugins/token-optimizer"}])):
            stream = copy.deepcopy(events())
            stream[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                parse_events(encode(stream))

    def test_populated_auto_memory_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            stream = events()
            stream[0]["memory_paths"] = {"auto": directory}
            parse_events(encode(stream))
            (Path(directory) / "MEMORY.md").write_text("- injected", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Auto-memory"):
                parse_events(encode(stream))
    def test_a_different_resolved_model_is_refused(self):
        with self.assertRaisesRegex(ValueError, "resolved"):
            parse_events(encode(events()), resolved_model="claude-sonnet-5")

    def test_tool_use_multi_turn_errors_and_unknown_events_fail_closed(self):
        base = events()
        tool = copy.deepcopy(base)
        tool[1]["message"]["content"].append({"type": "tool_use", "name": "Bash"})
        multi = copy.deepcopy(base)
        multi[-1]["num_turns"] = 2
        error = copy.deepcopy(base)
        error[-1]["is_error"] = True
        variants = [tool, multi, error, base[1:], base[:-1], base + [base[-1]], base + [base[0]],
                    base + [{"type": "future_protocol_event"}], base[:2] + [{"type": "user", "message": {}}] + base[2:]]
        for variant in variants:
            with self.subTest(variant=variant), self.assertRaises(ValueError):
                parse_events(encode(variant))

    def test_usage_must_be_complete_nonnegative_integers(self):
        for key, value in (("input_tokens", -1), ("output_tokens", True), ("cache_read_input_tokens", None)):
            stream = copy.deepcopy(events())
            stream[-1]["usage"][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                parse_events(encode(stream))

    def test_retry_delay_is_accounted_separately(self):
        stream = events()
        stream.insert(1, {"type": "system", "subtype": "api_retry", "retry_delay_ms": 1500})
        stream.insert(1, {"type": "system", "subtype": "api_retry", "retry_delay_ms": 2500})
        _, _, _, notes = parse_events(encode(stream))
        self.assertEqual((2, 4.0), (notes["api_retries"], notes["retry_delay_seconds"]))

    def test_oversized_output_and_text_rejected(self):
        with self.assertRaises(ValueError):
            parse_events(b" " * (MAX_OUTPUT + 1))
        with self.assertRaises(ValueError):
            parse_events(encode(events("x" * (64 * 1024 + 1))))


class IsolationTests(unittest.TestCase):
    def test_routing_and_session_variables_are_scrubbed(self):
        source = {"ANTHROPIC_BASE_URL": "http://127.0.0.1:58207", "ANTHROPIC_API_KEY": "k", "TOKEN_OPTIMIZER_CLIENT": "x",
                  "CLAUDE_CODE_SESSION_ID": "s", "CLAUDECODE": "1", "CLAUDE_PID": "1", "PATH": "p", "USERPROFILE": "u"}
        self.assertEqual({"PATH": "p", "USERPROFILE": "u"}, scrubbed_environment(source))

    def test_launchers_and_optimizer_shims_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            for name in ("claude.cmd", "claude.ps1", "run-client.mjs"):
                path = Path(directory) / name
                path.write_text("")
                with self.subTest(name=name), self.assertRaises(ValueError):
                    verified_executable(path)
            shim = Path(directory) / ".token-optimizer" / "bin" / "claude.exe"
            shim.parent.mkdir(parents=True)
            shim.write_bytes(b"")
            with self.assertRaises(ValueError):
                verified_executable(shim)

    def test_unpinned_cli_refuses_before_any_generation(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = ClaudeTransport(sys.executable, "haiku", Path(directory) / "evidence", 1)
            old = subprocess.CompletedProcess([], 0, b"2.0.0 (Claude Code)", b"")
            with patch("claude_transport.subprocess.run", return_value=old), \
                    patch("claude_transport.subprocess.Popen") as spawn:
                with self.assertRaisesRegex(ValueError, "pinned CLI"):
                    transport.generate("system", [])
                spawn.assert_not_called()
            self.assertEqual(0, transport.calls)

    def test_cap_failure_and_concurrency_refuse_before_spawning(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = ClaudeTransport(sys.executable, "haiku", Path(directory) / "evidence", 1)
            with patch("claude_transport.subprocess.run") as run, patch("claude_transport.subprocess.Popen") as spawn:
                transport.calls = 1
                with self.assertRaises(ValueError):
                    transport.generate("system", [])
                transport.calls, transport.failed = 0, True
                with self.assertRaises(ValueError):
                    transport.generate("system", [])
                transport.failed = False
                transport._lock.acquire()
                try:
                    with self.assertRaisesRegex(ValueError, "Concurrent"):
                        transport.generate("system", [])
                finally:
                    transport._lock.release()
                run.assert_not_called()
                spawn.assert_not_called()

    def test_invalid_construction_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            for model, calls, baseline in (("", 1, None), ("has space", 1, None), ("haiku", 0, None),
                                           ("haiku", 100_001, None), ("haiku", 1, 0), ("haiku", 1, True)):
                with self.subTest(model=model, calls=calls, baseline=baseline), self.assertRaises(ValueError):
                    ClaudeTransport(sys.executable, model, Path(directory) / f"e{calls}{baseline}", calls,
                                    canary_baseline=baseline)


class ReceiptTests(unittest.TestCase):
    def test_a_failed_spawn_still_leaves_a_failed_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = ClaudeTransport(sys.executable, "haiku", Path(directory) / "e", 2)
            with patch.object(ClaudeTransport, "_version", return_value=PINNED_CLI), \
                    patch("claude_transport.subprocess.Popen", side_effect=OSError("spawn failed")):
                with self.assertRaises(OSError):
                    transport.generate("system", [])
            record = reconcile_receipts(Path(directory) / "e")
            self.assertEqual((1, 1, [], 1.0), (record["calls"], record["receipts"], record["missing"], record["coverage"]))
            row = record["rows"][0]
            self.assertEqual(("failed", True, 0), (row["status"], row["unknown_usage"], row["api_key_calls"]))
            self.assertNotIn("transport_overhead_seconds", row)
            self.assertTrue(transport.failed)

    def test_reconciliation_reports_missing_receipts_and_refuses_gaps(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("0", "1"):
                (root / name).mkdir()
            (root / "0" / "receipt.json").write_text('{"status": "completed"}', encoding="utf-8")
            record = reconcile_receipts(root)
            self.assertEqual((2, 1, ["1"], 0.5), (record["calls"], record["receipts"], record["missing"], record["coverage"]))
            (root / "3").mkdir()
            with self.assertRaisesRegex(ValueError, "gap-free"):
                reconcile_receipts(root)


class WorkspaceTests(unittest.TestCase):
    def test_path_is_fixed_and_emptied_between_calls(self):
        with tempfile.TemporaryDirectory() as directory:
            with ExclusiveWorkspace(directory) as (cwd, mcp):
                first = (cwd, mcp)
                (cwd / "left-behind.md").write_text("context", encoding="utf-8")
            with ExclusiveWorkspace(directory) as (cwd, mcp):
                self.assertEqual(first, (cwd, mcp))
                self.assertEqual([], list(cwd.iterdir()))
                self.assertEqual({"mcpServers": {}}, json.loads(mcp.read_text(encoding="utf-8")))

    def test_default_root_is_stable(self):
        self.assertEqual(ExclusiveWorkspace().root, ExclusiveWorkspace().root)

    def test_second_holder_is_refused_in_process_and_across_processes(self):
        with tempfile.TemporaryDirectory() as directory:
            probe = ("import sys; from claude_transport import ExclusiveWorkspace\n"
                     "try:\n    ExclusiveWorkspace(sys.argv[1]).__enter__()\nexcept ValueError:\n    sys.exit(3)\n")
            with ExclusiveWorkspace(directory):
                with self.assertRaisesRegex(ValueError, "holds the fixed workspace"):
                    ExclusiveWorkspace(directory).__enter__()
                child = subprocess.run([sys.executable, "-c", probe, directory], cwd=Path(__file__).parent)
                self.assertEqual(3, child.returncode)
            self.assertEqual(0, subprocess.run([sys.executable, "-c", probe, directory],
                                               cwd=Path(__file__).parent).returncode)
            with ExclusiveWorkspace(directory):
                pass

@unittest.skipUnless(os.environ.get("EVOLUTION_CLAUDE_EXECUTABLE"), "Opt-in: consumes subscription allowance")
class LiveTests(unittest.TestCase):
    def test_canary_is_stable_and_isolated_across_calls(self):
        with tempfile.TemporaryDirectory() as directory:
            transport = ClaudeTransport(os.environ["EVOLUTION_CLAUDE_EXECUTABLE"], "haiku", Path(directory) / "e", 3)
            first = transport.canary_input_tokens()
            transport.canary_baseline = first
            self.assertEqual(first, transport.canary_input_tokens())
            receipt = json.loads((Path(directory) / "e" / "0" / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual((PINNED_CLI, "completed", 0), (receipt["cli"], receipt["status"], receipt["api_key_calls"]))
            self.assertTrue(receipt["resolved_model"].startswith("claude-haiku"))
            self.assertEqual(CANARY_PROMPT, (Path(directory) / "e" / "0" / "prompt.txt").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
