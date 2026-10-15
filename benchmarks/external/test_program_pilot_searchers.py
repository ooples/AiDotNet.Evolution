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

if __name__ == "__main__":
    unittest.main()
