"""The live pilot runner's two refusals, which are the only things standing between a command and a bill.

Everything the campaign does is already covered where it lives (`test_program_pilot_campaign.py`,
`test_program_pilot_cells.py`, `test_program_pilot_searchers.py`). What is only true here is the
order: the projection must be read before anything runs, the confirming run must reuse the seeds the
first run drew, and the registration must commit to the partition the endpoint is actually scored on.

No model and no engine: every test here stops at or before the gate, so none of them can spend.
"""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT.parent / "analysis"))

import pilot_cells  # noqa: E402
import run_pilot_campaign as runner  # noqa: E402

TASK = "rational_gain"
HOLDOUT = 20261015


def invoke(root, *extra):
    """Runs the entry point with a fixed argument set, returning (exit code, printed output)."""
    argv = ["run_pilot_campaign.py", "--root", str(root), "--cli", "aidotnet-evolve",
            "--upstream", str(root), "--holdout-seed", str(HOLDOUT), "--task", TASK,
            "--seed-count", "6", "--iterations", "1", "--model-calls", "4",
            "--model-tokens", "200000", "--wall-seconds", "600", "--stub", *extra]
    saved, output = sys.argv, io.StringIO()
    try:
        sys.argv = argv
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            try:
                runner.main()
            except SystemExit as exit_code:
                return exit_code.code, output.getvalue()
    finally:
        sys.argv = saved
    return 0, output.getvalue()


class GateTests(unittest.TestCase):
    def test_a_first_run_prints_the_ceiling_freezes_the_seeds_and_spends_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "campaign"
            code, output = invoke(root)
            self.assertEqual(2, code, "a run without --confirm must not launch")
            self.assertIn("ProjectionSha256", output)
            self.assertIn("72 model calls", output, "the ceiling is 18 cells x 4 calls, printed before anything runs")
            frozen = json.loads((root / "registration" / "registration.json").read_text(encoding="utf-8"))
            self.assertEqual(6, len(frozen["Seeds"]), "the seeds are drawn by the registration, not the operator")
            self.assertFalse((root / "state").exists(), "no cell ran")
            self.assertFalse((root / "evidence").exists(), "no transport was opened")

    def test_the_confirming_run_reuses_the_seeds_the_first_run_drew(self):
        # Re-freezing here would draw new seeds after the operator had seen the projection, which is
        # the one thing a pre-registration exists to prevent.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "campaign"
            first = invoke(root)[1]
            digest = next(line.split()[-1] for line in first.splitlines() if line.startswith("ProjectionSha256"))
            frozen_before = json.loads((root / "registration" / "registration.json").read_text(encoding="utf-8"))
            code, output = invoke(root, "--confirm", "not-the-digest")
            self.assertEqual(2, code)
            self.assertIn(digest, output, "the same projection, so the same digest")
            frozen_after = json.loads((root / "registration" / "registration.json").read_text(encoding="utf-8"))
            self.assertEqual(frozen_before, frozen_after, "a second run must not re-freeze anything")

    def test_a_holdout_seed_that_is_not_the_sealed_one_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "campaign"
            invoke(root)
            code, output = invoke(root, "--holdout-seed", str(HOLDOUT + 1))
            self.assertIsInstance(code, str)
            self.assertIn("does not match the partition the registration sealed", code)

    def test_the_registration_seals_the_partition_the_endpoint_is_scored_on(self):
        # A placeholder hash here would commit to nothing: campaign_registration takes it on trust
        # and nothing downstream recomputes it.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "campaign"
            invoke(root)
            frozen = json.loads((root / "registration" / "registration.json").read_text(encoding="utf-8"))
            self.assertEqual(pilot_cells.holdout_seal(TASK, HOLDOUT),
                             frozen["Spec"]["TestPartitionHashes"][TASK])
            self.assertNotEqual("0" * 64, frozen["Spec"]["TestPartitionHashes"][TASK])

    def test_an_existing_tree_without_a_registration_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "campaign"
            (root / "evidence").mkdir(parents=True)
            code, _ = invoke(root)
            self.assertIsInstance(code, str)
            self.assertIn("holds no frozen registration", code)


class BudgetConsistencyTests(unittest.TestCase):
    """The two ceilings have to be set against each other, not independently."""

    def test_a_token_ceiling_below_the_call_ceiling_is_refused(self):
        # The trap this closes: a token cap that binds first does not refuse a call, it shortens the
        # search, and the row still reads as a completed cell. The first version of this script
        # defaulted to 30 calls and 400,000 tokens, which would have stopped every cell at about 10.
        with tempfile.TemporaryDirectory() as directory:
            code, _ = invoke(Path(directory) / "campaign", "--model-calls", "30", "--model-tokens", "400000")
            self.assertIsInstance(code, str)
            self.assertIn("token ceiling would bind", code)

    def test_the_defaults_leave_the_call_ceiling_binding(self):
        # Read from the parser itself, so a default cannot be asserted from memory.
        defaults = {action.dest: action.default for action in runner.build_parser()._actions}
        self.assertGreaterEqual(defaults["model_tokens"], defaults["model_calls"] * runner.TOKENS_PER_CALL)
        self.assertEqual(10, defaults["model_calls"],
                         "18 cells x 10 calls is what the authorised envelope buys at this account's rate")


class SummaryTests(unittest.TestCase):
    """What the run's own account says about a cell it could not account for."""

    def summary(self, rows):
        arguments = runner.build_parser().parse_args(
            ["--root", "unused", "--cli", "x", "--upstream", ".", "--holdout-seed", str(HOLDOUT), "--stub"])
        frozen = {"RegistrationSha256": "r" * 64, "Spec": {"TestPartitionHashes": {TASK: "p" * 64}}}
        return runner.summarise(rows, {"Endpoints": []}, task=TASK, arguments=arguments, frozen=frozen,
                                projection={"ProjectionSha256": "j" * 64}, wall=1.0, canaries=None, receipts={})

    def test_a_cell_with_no_call_count_is_counted_beside_the_total_not_absorbed_into_it(self):
        # Summing an unaccounted cell as zero is the same under-report the searchers' accounting
        # exists to prevent: the row would read as a free cell.
        rows = [{"Status": "completed", "ModelCalls": 7, "ModelTokens": 2100},
                {"Status": "failed", "ModelCalls": None, "ModelTokens": None}]
        summary = self.summary(rows)
        self.assertEqual(7, summary["model_calls"], "the known spend is still the total")
        self.assertEqual(1, summary["cells_with_unknown_spend"])
        self.assertEqual({"completed": 1, "failed": 1}, summary["statuses"])

    def test_a_fully_accounted_run_reports_no_unknown_cells(self):
        rows = [{"Status": "completed", "ModelCalls": 3, "ModelTokens": 90},
                {"Status": "failed", "ModelCalls": 2, "ModelTokens": 60}]
        summary = self.summary(rows)
        self.assertEqual(5, summary["model_calls"])
        self.assertEqual(0, summary["cells_with_unknown_spend"], "a failed cell that accounted for itself is known")


class TransportDirectoryTests(unittest.TestCase):
    def test_a_second_attempt_gets_a_directory_of_its_own(self):
        # ClaudeModelSet refuses a directory that already exists, so a fixed name would make the
        # retry after a failed canary crash instead of re-running - on a command that costs money.
        with tempfile.TemporaryDirectory() as directory:
            first = runner.transport_directory(directory)
            first.mkdir(parents=True)
            second = runner.transport_directory(directory)
            self.assertNotEqual(first, second)
            self.assertFalse(second.exists())
            self.assertTrue(second.name.startswith("transport-"))


class StubProviderTests(unittest.TestCase):
    def test_the_stub_answers_whichever_shape_the_prompt_asked_for(self):
        # A stub that answered one shape would have every proposal of the other arms abandoned after
        # three calls, which reads as a working run: measured, and it is why this is not one answer.
        calls = []
        provider = runner.stub_provider(calls)
        diff, _ = provider("return SEARCH/REPLACE blocks", [{"content": "parent"}], model="sonnet")
        fence, _ = provider("return the complete program", [{"content": "parent"}], model="sonnet")
        self.assertIn("<<<<<<< SEARCH", diff)
        self.assertIn("```python", fence)
        self.assertEqual(["sonnet", "sonnet"], calls, "the model name reaches the provider")

    def test_the_cost_it_reports_is_an_int_the_broker_will_accept(self):
        _, cost = runner.stub_provider([])("system", [{"content": "x"}])
        self.assertIs(int, type(cost))


if __name__ == "__main__":
    unittest.main()
