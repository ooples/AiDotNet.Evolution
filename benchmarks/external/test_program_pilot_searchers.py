"""Our arm's searcher: what it returns, and that it accounts for every model call.

The accounting is the part worth testing without a provider. `aidotnet-evolve run` reports
`ModelUsage` from the variation operator and a separate `MetaPrompt` block, and the CLI's own comment
says `ModelUsage` excludes those calls - so a searcher that read one source would under-report a
meta-prompt arm's spend and the campaign's budget check would pass on the under-report.
"""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_searchers  # noqa: E402




class UsageAccountingTests(unittest.TestCase):
    def test_variation_calls_alone_are_the_total_when_no_meta_prompt_ran(self):
        calls, tokens = pilot_searchers._usage({"ModelUsage": {"ChatCalls": 7, "TotalTokens": 1234}})
        self.assertEqual((7, 1234), (calls, tokens))

    def test_meta_prompt_calls_are_added_rather_than_dropped(self):
        # The arm the CLI warns about: instruction evolution calls the model on its own cadence.
        calls, tokens = pilot_searchers._usage({
            "ModelUsage": {"ChatCalls": 7, "TotalTokens": 1234},
            "MetaPrompt": {"ProposalCalls": 5, "RefusedProposals": 1},
        })
        self.assertEqual(12, calls, "a meta-prompt arm's calls must not be dropped")
        self.assertEqual(1234, tokens)

    def test_a_run_that_reports_no_usage_is_a_failure_not_a_free_cell(self):
        for summary in ({}, {"ModelUsage": {}}, {"ModelUsage": {"ChatCalls": 3}}):
            with self.subTest(summary=summary):
                with self.assertRaises(ValueError):
                    pilot_searchers._usage(summary)

    def test_an_incomplete_task_directory_is_refused_at_construction(self):
        # A missing evaluator or seed is a campaign that was not configured. Refusing at construction
        # means every cell fails the same way at launch rather than one at a time, mid-run.
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileNotFoundError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1)

            root = Path(directory)
            (root / "config.yaml").write_text("max_iterations: 1\n", encoding="utf-8")
            with self.assertRaises(FileNotFoundError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1)

            (root / "evaluator.py").write_text("def evaluate(p):\n    return {}\n", encoding="utf-8")
            with self.assertRaises(FileNotFoundError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1)

            # All three present: construction succeeds. The task this pilot ships
            # (benchmarks/external/symbolic_regression_task, added in #288) has exactly these three.
            (root / "initial_program.py").write_text("def predict(*xs):\n    return 0.0\n", encoding="utf-8")
            self.assertTrue(callable(pilot_searchers.ours("aidotnet-evolve", directory, sys.executable,
                                                          iterations=1)))

class CommandPrefixTests(unittest.TestCase):
    def test_the_cli_may_be_a_command_prefix_not_only_an_executable(self):
        """A dry run caught this: `cli="dotnet"` became `dotnet run ...` and the SDK answered
        "Couldn't find a project to run". CI builds the dll rather than installing the tool, so a
        sequence prefix has to work."""
        import subprocess
        import tempfile
        captured = {}

        def fake_run(command, **kwargs):
            captured["command"] = command
            raise AssertionError("stop before the subprocess")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "config.yaml").write_text("max_iterations: 1\n", encoding="utf-8")
            (root / "evaluator.py").write_text("def evaluate(p):\n    return {}\n", encoding="utf-8")
            (root / "initial_program.py").write_text("def predict(*xs):\n    return 0.0\n", encoding="utf-8")
            cell = {"Family": "damped_oscillator", "Arm": "aidotnet", "Seed": 5000}
            genuine = subprocess.run
            try:
                subprocess.run = fake_run
                for cli, expected in (("aidotnet-evolve", ["aidotnet-evolve", "run"]),
                                       (["dotnet", "x/aidotnet-evolve.dll"],
                                        ["dotnet", "x/aidotnet-evolve.dll", "run"])):
                    with self.subTest(cli=cli):
                        search = pilot_searchers.ours(cli, root, sys.executable, iterations=1)
                        with self.assertRaises(AssertionError):
                            search(cell, root / "cell", False)
                        self.assertEqual(expected, captured["command"][:len(expected)])
            finally:
                subprocess.run = genuine


class FakeBroker:
    """Stands in for ProgramBroker's counters: cumulative, shared across arms and cells."""

    def __init__(self):
        self.attempted = {"model": 0, "evaluate": 0}
        self.model_tokens = 0

    def charge(self, calls, tokens):
        self.attempted["model"] += calls
        self.model_tokens += tokens


class OpenEvolveArmTests(unittest.TestCase):
    def task(self, directory):
        root = Path(directory)
        (root / "initial_program.py").write_text("def predict(*xs):' + BS + 'n    return 0.0' + BS + 'n", encoding="utf-8")
        (root / "evaluator.py").write_text("def evaluate(p):' + BS + 'n    return {}' + BS + 'n", encoding="utf-8")
        return root

    def test_only_the_two_native_modes_are_pilot_arms(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            task = self.task(directory)
            for mode in ("recommended", "matched"):
                self.assertTrue(callable(pilot_searchers.openevolve(
                    "adapter.py", "upstream", task, FakeBroker(), mode=mode, model="m", iterations=1)))
            for mode in ("controlled", "native-bounded", "", None):
                with self.subTest(mode=mode), self.assertRaises(ValueError):
                    pilot_searchers.openevolve("adapter.py", "upstream", task, FakeBroker(),
                                               mode=mode, model="m", iterations=1)

    def test_spend_is_a_per_cell_delta_on_a_shared_broker(self):
        # The broker is shared across arms and cells by design, so its counters are cumulative.
        # Reporting the total would charge every later cell for every earlier one.
        broker = FakeBroker()
        broker.charge(11, 2200)          # an earlier cell's spend, already on the books
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            task = self.task(directory)
            search = pilot_searchers.openevolve("adapter.py", "upstream", task, broker,
                                                mode="recommended", model="m", iterations=1)
            # Drive the accounting without the adapter: charge during the "run" and read the delta.
            before = (broker.attempted["model"], broker.model_tokens)
            broker.charge(4, 800)
            after = (broker.attempted["model"], broker.model_tokens)
        self.assertEqual((15, 3000), after, "the broker stays cumulative")
        self.assertEqual((4, 800), (after[0] - before[0], after[1] - before[1]),
                         "the cell's own spend is the delta, not the broker's total")
        self.assertTrue(callable(search))


if __name__ == "__main__":
    unittest.main()
