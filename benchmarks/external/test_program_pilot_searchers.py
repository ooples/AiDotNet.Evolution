"""Both arms' searchers: what they hand the executor, and that they account for what they spent.

Three properties are worth testing without a provider, and each one is something a dry run caught
being wrong:

- **Spend is measured, not claimed.** Each cell's own broker is the instrument; our CLI's
  `ModelUsage` (plus the separate `MetaPrompt` block its own comment says `ModelUsage` excludes) is
  only a cross-check, and a self-report above the measurement is a failure rather than a result.
- **The search is never told which rows it is scored on.** The task and both seeds stay with the
  broker's callback, because review showed a candidate that could read the selection seed could
  rebuild the held-out rows and their seal matched.
- **A cell that spent and then failed says so.** The searcher's broker is closed by the time the
  executor sees the exception, so the spend rides on the exception.
"""
import json
import subprocess
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_searchers  # noqa: E402

NL = chr(10)
CELL = {"Cell": "c", "Family": "rational_gain", "Arm": "aidotnet", "Seed": 4242, "Block": 0, "Position": 0}

# Writes the adapter's own result file, then charges the broker as a real run would have.
FAKE_ADAPTER = r'''import json, sys
from pathlib import Path

arguments = dict(zip(sys.argv[1::2], sys.argv[2::2]))
output = Path(arguments["--output"])
output.mkdir(parents=True, exist_ok=True)
record = json.loads(Path(arguments["--config-record"]).read_text(encoding="utf-8"))
(output / "record-as-seen.json").write_text(json.dumps(record), encoding="utf-8")
(output / "adapter-result.json").write_text(json.dumps({
    "changes": record["changes"], "differences": record.get("differences", []),
    "best": None if "--produce-nothing" in sys.argv else
            {"code": "def predict(*xs):\n    return 1.0\n", "metrics": {"combined_score": 0.5}},
}), encoding="utf-8")
sys.exit(int(arguments.get("--exit", "0")))
'''


class FakeBroker:
    """ProgramBroker's surface, as a searcher uses it: an endpoint, and counters for one cell."""

    def __init__(self, calls=0, tokens=0, closed=False, unaccounted=0, throttle=0.0):
        self.endpoint, self.capability = "http://127.0.0.1:9", "capability-for-one-cell"
        self.attempted = {"model": calls, "evaluate": calls + 1}
        self.model_tokens, self.closed = tokens, closed
        self.unknown_attempts = {"model": unaccounted, "evaluate": 0}
        self.rows = [{"throttle_seconds": throttle}]


def session_of(broker):
    @contextmanager
    def open_for(cell):
        broker.cell = cell
        yield broker

    return open_for


def task(directory, *, config="max_iterations: 1\nllm:\n  models:\n    - name: \"sonnet\"\n      weight: 1.0\nprompt:\n  system_message: \"improve it\"\n"):
    root = Path(directory)
    (root / "config.yaml").write_text(config, encoding="utf-8")
    (root / "initial_program.py").write_text("def predict(*xs):\n    return 0.0\n", encoding="utf-8")
    (root / "evaluator.py").write_text("def evaluate(p):\n    return {}\n", encoding="utf-8")
    return root


class UsageAccountingTests(unittest.TestCase):
    def test_variation_calls_alone_are_the_total_when_no_meta_prompt_ran(self):
        self.assertEqual((7, 1234, 0),
                         pilot_searchers._usage({"ModelUsage": {"ChatCalls": 7, "TotalTokens": 1234}}))

    def test_refused_requests_are_reported_beside_the_calls_that_were_sent(self):
        """`ChatCalls` counts what was SENT. Only the difference was served, and that is what the
        broker counts, so the refusals have to travel with the pair or the two cannot be compared."""
        self.assertEqual(
            (13, 1234, 3),
            pilot_searchers._usage({"ModelUsage": {"ChatCalls": 13, "TotalTokens": 1234,
                                                   "ProviderErrors": 3}}))

    def test_meta_prompt_calls_are_added_rather_than_dropped(self):
        # The arm the CLI warns about: instruction evolution calls the model on its own cadence.
        calls, tokens, _ = pilot_searchers._usage({
            "ModelUsage": {"ChatCalls": 7, "TotalTokens": 1234},
            "MetaPrompt": {"ProposalCalls": 5, "RefusedProposals": 1},
        })
        self.assertEqual(12, calls, "a meta-prompt arm's calls must not be dropped")
        self.assertEqual(1234, tokens)

    def test_a_summary_with_no_usage_leaves_the_cross_check_out_rather_than_faking_one(self):
        # The measurement is the broker's; a missing self-report costs the cross-check, not the
        # number, so it is reported as absent instead of being read as zero.
        for summary in ({}, {"ModelUsage": {}}, {"ModelUsage": {"ChatCalls": 3}}):
            with self.subTest(summary=summary):
                self.assertIsNone(pilot_searchers._usage(summary))


class ConstructionTests(unittest.TestCase):
    def test_an_incomplete_task_directory_is_refused_at_construction(self):
        # A missing config or seed is a campaign that was not configured. Refusing at construction
        # means every cell fails the same way at launch rather than one at a time, mid-run.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            broker = FakeBroker()
            with self.assertRaises(FileNotFoundError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1,
                                     session=session_of(broker))
            (root / "config.yaml").write_text("llm:\n  models: []\n", encoding="utf-8")
            with self.assertRaises(FileNotFoundError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1,
                                     session=session_of(broker))
            (root / "initial_program.py").write_text("def predict(*xs):\n    return 0.0\n", encoding="utf-8")
            self.assertTrue(callable(pilot_searchers.ours("aidotnet-evolve", directory, sys.executable,
                                                          iterations=1, session=session_of(broker))))

    def test_a_task_config_that_pins_its_own_endpoint_is_refused(self):
        # The broker reaches our arm through OPENAI_API_BASE, which the importer only consults when
        # llm.api_base is unset - so a pinned api_base would send the arm somewhere unmetered.
        with tempfile.TemporaryDirectory() as directory:
            task(directory, config='llm:\n  api_base: "https://api.example.com/v1"\n  models:\n    - name: "sonnet"\n      weight: 1.0\n')
            with self.assertRaises(ValueError):
                pilot_searchers.ours("aidotnet-evolve", directory, sys.executable, iterations=1,
                                     session=session_of(FakeBroker()))

    def test_only_the_two_native_modes_are_pilot_arms(self):
        with tempfile.TemporaryDirectory() as directory:
            root = task(directory)
            for mode in ("recommended", "matched"):
                self.assertTrue(callable(pilot_searchers.openevolve(
                    "adapter.py", "upstream", root, session_of(FakeBroker()), mode=mode, model="m",
                    iterations=1)))
            for mode in ("controlled", "native-bounded", "", None):
                with self.subTest(mode=mode), self.assertRaises(ValueError):
                    pilot_searchers.openevolve("adapter.py", "upstream", root, session_of(FakeBroker()),
                                               mode=mode, model="m", iterations=1)


class OurArmTests(unittest.TestCase):
    """What our arm hands the CLI, captured by replacing the subprocess rather than running one."""

    def run_capture(self, directory, broker, *, returncode=0, stdout="{}", cli="aidotnet-evolve",
                    champion_standard_errors=None):
        captured = {}

        def fake_run(command, **kwargs):
            captured["command"], captured["environment"] = command, kwargs["env"]
            if returncode == 0:  # the CLI writes the winner; a faked run has to as well
                output = Path(command[command.index("--output") + 1])
                output.mkdir(parents=True, exist_ok=True)
                (output / "best.py").write_text("def predict(*xs):" + chr(10) + "    return 1.0" + chr(10),
                                                encoding="utf-8")
            return subprocess.CompletedProcess(command, returncode, stdout, "")

        root = task(directory)
        genuine = subprocess.run
        try:
            subprocess.run = fake_run
            search = pilot_searchers.ours(cli, root, sys.executable, iterations=2,
                                          session=session_of(broker),
                                          champion_standard_errors=champion_standard_errors)
            try:
                captured["outcome"] = search(CELL, Path(directory) / "cell", False)
            except Exception as error:  # noqa: BLE001 - the failure is what some of these assert
                captured["error"] = error
        finally:
            subprocess.run = genuine
        return captured

    def test_a_champion_rule_reaches_the_cli_with_the_brokers_standard_error_metric(self):
        # E-4: without the option the command is unchanged, so a pilot that does not ask for it runs as before.
        with tempfile.TemporaryDirectory() as directory:
            plain = self.run_capture(directory, FakeBroker(calls=2))["command"]
        with tempfile.TemporaryDirectory() as directory:
            command = self.run_capture(directory, FakeBroker(calls=2), champion_standard_errors=1)["command"]
        self.assertNotIn("--champion-standard-errors", plain)
        self.assertEqual("1.0", command[command.index("--champion-standard-errors") + 1])
        self.assertEqual("quality_standard_error", command[command.index("--standard-error-metric") + 1])

    def test_the_cli_may_be_a_command_prefix_not_only_an_executable(self):
        """A dry run caught this: `cli="dotnet"` became `dotnet run ...` and the SDK answered
        "Couldn't find a project to run". CI builds the dll rather than installing the tool, so a
        sequence prefix has to work."""
        for cli, expected in (("aidotnet-evolve", ["aidotnet-evolve", "run"]),
                              (["dotnet", "x/aidotnet-evolve.dll"], ["dotnet", "x/aidotnet-evolve.dll", "run"])):
            with self.subTest(cli=cli), tempfile.TemporaryDirectory() as directory:
                captured = self.run_capture(directory, FakeBroker(calls=2), cli=cli)
                self.assertEqual(expected, captured["command"][:len(expected)])

    def test_the_evaluator_it_runs_is_the_cells_broker_delegate_not_the_tasks_own_file(self):
        """The CLI scrubs its evaluator's environment, so the endpoint has to be inside the file.

        Measured before this: all six model calls were refused for want of a valid evaluation,
        because the delegate read `EVOLUTION_BROKER_*` from an environment the CLI had emptied. The
        same scrubbing is why the earlier dry run that looked like it worked had been scoring the
        default task instead of the cell's.
        """
        with tempfile.TemporaryDirectory() as directory:
            broker = FakeBroker(calls=2)
            captured = self.run_capture(directory, broker)
            evaluator = Path(captured["command"][captured["command"].index("run") + 4])
            self.assertEqual("cell_evaluator.py", evaluator.name)
            written = evaluator.read_text(encoding="utf-8")
            self.assertIn(broker.endpoint, written)
            self.assertIn(broker.capability, written)
            self.assertIn("pilot_task_evaluator", written)
            self.assertNotIn(str(CELL["Seed"]), written, "the evaluator must not carry the seed")

    def test_the_child_is_told_the_broker_and_nothing_about_the_data(self):
        with tempfile.TemporaryDirectory() as directory:
            broker = FakeBroker(calls=2)
            environment = self.run_capture(directory, broker)["environment"]
            self.assertEqual(broker.endpoint + "/v1", environment["OPENAI_API_BASE"])
            self.assertEqual(broker.capability, environment["OPENAI_API_KEY"])
            self.assertEqual(broker.endpoint, environment["EVOLUTION_BROKER_ENDPOINT"])
            for name in ("EVOLUTION_SR_TASK", "EVOLUTION_SR_SEED", "EVOLUTION_SR_HOLDOUT_SEED"):
                self.assertNotIn(name, environment, name + " would tell the search which rows score it")

    def test_the_task_config_is_handed_over_unmodified(self):
        with tempfile.TemporaryDirectory() as directory:
            captured = self.run_capture(directory, FakeBroker(calls=2))
            command = captured["command"]
            config = Path(command[command.index("--openevolve-config") + 1])
            self.assertEqual(Path(directory) / "config.yaml", config)

    def test_spend_is_the_brokers_measurement_and_the_self_report_is_only_a_cross_check(self):
        with tempfile.TemporaryDirectory() as directory:
            outcome = self.run_capture(directory, FakeBroker(calls=5, tokens=900),
                                       stdout='{"ModelUsage": {"ChatCalls": 4, "TotalTokens": 0}}')["outcome"]
            self.assertEqual(5, outcome["model_calls"], "the broker measured five")
            self.assertEqual(900, outcome["model_tokens"])
            self.assertEqual(4, outcome["reported_model_calls"], "and the self-report is kept beside it")

    def test_a_self_report_above_the_measurement_fails_the_cell(self):
        # The broker is the ceiling: calls it SERVED that it never admitted are unmetered spend, so
        # this is a broken measurement, not a cheap cell, and it must not be reported as a result.
        with tempfile.TemporaryDirectory() as directory:
            error = self.run_capture(directory, FakeBroker(calls=2),
                                     stdout='{"ModelUsage": {"ChatCalls": 9, "TotalTokens": 0}}')["error"]
            self.assertIsInstance(error, pilot_searchers.SearchFailed)
            self.assertEqual(2, error.accounting["model_calls"])

    def test_requests_the_provider_refused_are_not_unmetered_spend(self):
        """The live damped_oscillator pilot failed 2 of our 6 cells at 13 sent / 3 refused / 10
        admitted. Thirteen requests went out, the broker refused three of them at its ceiling, and
        ten were served - which is exactly what the broker counted. Reading that as an accounting
        fault penalises a cell for retrying an unusable answer, on precisely the tasks where answers
        are unusable."""
        with tempfile.TemporaryDirectory() as directory:
            outcome = self.run_capture(
                directory, FakeBroker(calls=10, tokens=500),
                stdout='{"ModelUsage": {"ChatCalls": 13, "TotalTokens": 0, "ProviderErrors": 3}}',
            )["outcome"]
            self.assertEqual(10, outcome["model_calls"], "the broker's count is still the measurement")
            self.assertEqual(13, outcome["reported_model_calls"])
            self.assertEqual(3, outcome["reported_provider_errors"], "and the refusals are kept beside it")

    def test_one_served_call_too_many_still_fails_even_with_refusals(self):
        # The refusals explain a surplus; they must not absorb one. Twelve sent and one refused is
        # eleven served against ten admitted, so one call was never metered.
        with tempfile.TemporaryDirectory() as directory:
            error = self.run_capture(
                directory, FakeBroker(calls=10),
                stdout='{"ModelUsage": {"ChatCalls": 12, "TotalTokens": 0, "ProviderErrors": 1}}',
            )["error"]
            self.assertIsInstance(error, pilot_searchers.SearchFailed)
            self.assertIn("served 11", str(error))


    def test_a_cell_that_spent_and_then_failed_carries_its_spend_on_the_exception(self):
        with tempfile.TemporaryDirectory() as directory:
            error = self.run_capture(directory, FakeBroker(calls=3, tokens=600), returncode=1)["error"]
            self.assertIsInstance(error, pilot_searchers.SearchFailed)
            self.assertEqual(3, error.accounting["model_calls"])
            self.assertEqual(600, error.accounting["model_tokens"])

    def test_a_closed_broker_fails_the_cell_even_when_the_arm_exited_zero(self):
        # An arm can lose a proposal to a refused call and still finish, so the broker is checked
        # before the exit code; otherwise a run that lost half its calls reads as a result.
        with tempfile.TemporaryDirectory() as directory:
            error = self.run_capture(directory, FakeBroker(calls=4, closed=True))["error"]
            self.assertIsInstance(error, pilot_searchers.SearchFailed)
            self.assertIn("broker closed", str(error))

    def test_an_unaccounted_call_fails_the_cell(self):
        with tempfile.TemporaryDirectory() as directory:
            error = self.run_capture(directory, FakeBroker(calls=4, unaccounted=1))["error"]
            self.assertIsInstance(error, pilot_searchers.SearchFailed)
            self.assertEqual(1, error.accounting["unaccounted_calls"])


class OpenEvolveArmTests(unittest.TestCase):
    """The OpenEvolve arms, driven through a stand-in adapter that really runs as a subprocess."""

    def run_arm(self, directory, broker, *, mode="recommended", iterations=2, adapter_source=FAKE_ADAPTER):
        root = task(directory)
        adapter = Path(directory) / "fake_adapter.py"
        adapter.write_text(adapter_source, encoding="utf-8")
        search = pilot_searchers.openevolve(adapter, "upstream", root, session_of(broker), mode=mode,
                                            model="sonnet", iterations=iterations)
        cell_directory = Path(directory) / "cell"
        return search(CELL, cell_directory, False), cell_directory

    def test_the_adapters_required_config_record_is_built_for_this_cells_seed(self):
        """The blocker a dry run hit: a native mode refuses to run without --config-record.

        `openevolve_adapter` raises "Invalid bounded OpenEvolve run" when a native mode arrives with
        no record (its own check is `(mode in NATIVE_MODES) != (config_record is not None)`), and it
        refuses a record whose `random_seed` is not the seed it was told to run - so the record has
        to be rebuilt per cell, not once per campaign.
        """
        with tempfile.TemporaryDirectory() as directory:
            outcome, cell_directory = self.run_arm(directory, FakeBroker(calls=2))
            record = json.loads((cell_directory / "out" / "record-as-seen.json").read_text(encoding="utf-8"))
            self.assertEqual(CELL["Seed"], record["config"]["random_seed"])
            self.assertEqual(2, record["config"]["max_iterations"], "the config's budget is the cell's")
            self.assertEqual("recommended", record["kind"])
            self.assertEqual(0, record["config"]["llm"]["retries"], "a retry is work the budget never admitted")
            self.assertTrue(outcome["program"].endswith("candidate.py"))

    def test_the_recommended_arm_is_the_tasks_own_config_with_the_models_swapped(self):
        with tempfile.TemporaryDirectory() as directory:
            _, cell_directory = self.run_arm(directory, FakeBroker(calls=2))
            record = json.loads((cell_directory / "out" / "record-as-seen.json").read_text(encoding="utf-8"))
            self.assertEqual(["sonnet"], record["models"])
            self.assertEqual("improve it", record["config"]["prompt"]["system_message"],
                             "the task's own prompt, not one this module wrote")

    def test_the_matched_arm_carries_our_engine_features_and_declares_each_difference(self):
        with tempfile.TemporaryDirectory() as directory:
            _, cell_directory = self.run_arm(directory, FakeBroker(calls=2), mode="matched")
            record = json.loads((cell_directory / "out" / "record-as-seen.json").read_text(encoding="utf-8"))
            self.assertEqual("matched", record["kind"])
            self.assertEqual(1, record["config"]["database"]["num_islands"], "our arm runs one island")
            self.assertTrue(record["config"]["diff_based_evolution"] is False, "our engine rewrites whole programs")
            self.assertEqual("improve it", record["config"]["prompt"]["system_message"])
            self.assertTrue(record["differences"], "a non-equivalence that is not declared is a hidden one")

    def test_the_cells_spend_is_its_own_brokers_count(self):
        # Each cell has its own broker, so the count needs no delta - and a cell cannot be charged
        # for an earlier one, which is what a shared cumulative counter would have done.
        with tempfile.TemporaryDirectory() as directory:
            outcome, _ = self.run_arm(directory, FakeBroker(calls=6, tokens=1800))
            self.assertEqual(6, outcome["model_calls"])
            self.assertEqual(1800, outcome["model_tokens"])

    def test_a_run_that_produced_no_candidate_is_a_failure_carrying_its_spend(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(pilot_searchers.SearchFailed) as raised:
                self.run_arm(directory, FakeBroker(calls=5),
                             adapter_source=FAKE_ADAPTER.replace('"--produce-nothing" in sys.argv', "True"))
            self.assertEqual(5, raised.exception.accounting["model_calls"],
                             "an empty run still spent, and an unreported spend understates the bill")

    def test_an_unreadable_adapter_result_is_a_failure_carrying_its_spend(self):
        # Exit 0 with no readable result is still a cell that spent; the bill outlives the file.
        broken = ("import sys" + NL + "from pathlib import Path" + NL
                  + "arguments = dict(zip(sys.argv[1::2], sys.argv[2::2]))" + NL
                  + "output = Path(arguments['--output'])" + NL
                  + "output.mkdir(parents=True, exist_ok=True)" + NL
                  + "(output / 'adapter-result.json').write_text('{not json', encoding='utf-8')" + NL)
        for source in ("import sys" + NL + "sys.exit(0)" + NL, broken):
            with self.subTest(source=source[:24]), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(pilot_searchers.SearchFailed) as raised:
                    self.run_arm(directory, FakeBroker(calls=3), adapter_source=source)
                self.assertEqual(3, raised.exception.accounting["model_calls"])

    def test_an_arm_that_ran_to_its_wall_limit_still_reports_its_spend(self):
        # The most expensive way to fail: losing this spend understates the bill by the most.
        for arm in ("ours", "openevolve"):
            with self.subTest(arm=arm), tempfile.TemporaryDirectory() as directory:
                root = task(directory)
                broker = FakeBroker(calls=7, tokens=2100)
                genuine = subprocess.run

                def timing_out(command, **kwargs):
                    raise subprocess.TimeoutExpired(command, kwargs.get("timeout", 0))

                try:
                    subprocess.run = timing_out
                    if arm == "ours":
                        search = pilot_searchers.ours("aidotnet-evolve", root, sys.executable,
                                                      iterations=1, session=session_of(broker))
                    else:
                        search = pilot_searchers.openevolve("adapter.py", "upstream", root,
                                                            session_of(broker), mode="recommended",
                                                            model="sonnet", iterations=1)
                    with self.assertRaises(pilot_searchers.SearchFailed) as raised:
                        search(CELL, Path(directory) / "cell", False)
                finally:
                    subprocess.run = genuine
                self.assertEqual(7, raised.exception.accounting["model_calls"])
                self.assertEqual(2100, raised.exception.accounting["model_tokens"])

    def test_a_task_declaring_a_model_with_no_tier_is_refused_at_construction(self):
        with tempfile.TemporaryDirectory() as directory:
            root = task(directory, config='llm:' + NL + '  models:' + NL + '    - name: "llama-3"' + NL
                                          + '      weight: 1.0' + NL + 'prompt:' + NL
                                          + '  system_message: "improve it"' + NL)
            with self.assertRaises(ValueError) as raised:
                pilot_searchers.openevolve("adapter.py", "upstream", root, session_of(FakeBroker()),
                                           mode="matched", model="m", iterations=1)
            self.assertIn("llama-3", str(raised.exception), "the message has to name the model")


if __name__ == "__main__":
    unittest.main()
