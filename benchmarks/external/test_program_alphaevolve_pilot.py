"""E-4 (#347): the AlphaEvolve pilot's seams - construction, broker scoring, the endpoint, the registration."""
from __future__ import annotations

import io
import contextlib
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import time
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "analysis"))

import alphaevolve_pilot as pilot  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402
import alphaevolve_tasks_smoke as smoke  # noqa: E402
import campaign_registration as registration  # noqa: E402
from program_broker import candidate_hash  # noqa: E402
import run_alphaevolve_pilot as runner  # noqa: E402

PYTHON = sys.executable
PROBLEM = "c5-erdos-overlap"


def program(body):
    return "import numpy as np\n\n\ndef construct():\n" + "".join("    " + line + "\n" for line in body.splitlines())


class ConstructionTests(unittest.TestCase):
    def run_program(self, code, **options):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "candidate.py"
            path.write_text(code, encoding="utf-8")
            return pilot.construction(path, PYTHON, **options)


    def test_a_construction_comes_back_as_a_list(self):
        self.assertEqual(([0.5, 0.25], None), self.run_program(program("return np.array([0.5, 0.25])")))

    def test_an_exception_is_a_reason_not_a_raise(self):
        heights, reason = self.run_program(program("raise RuntimeError('broken on purpose')"))
        self.assertIsNone(heights)
        self.assertIn("broken on purpose", reason)

    def test_a_missing_construct_is_a_reason(self):
        heights, reason = self.run_program("x = 1\n")
        self.assertIsNone(heights)
        self.assertIn("construct", reason)

    def test_the_time_limit_binds(self):
        heights, reason = self.run_program(program("import time\ntime.sleep(30)\nreturn [1.0]"), timeout=2)
        self.assertIsNone(heights)
        self.assertIn("2 seconds", reason)

    def test_a_timeout_also_stops_what_the_candidate_spawned(self):
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "survived.txt"
            grandchild = "import time; time.sleep(4); open(%r, 'w').close()" % str(marker)
            spawn = ("import subprocess, sys, time\nsubprocess.Popen([sys.executable, '-c', %r])\n"
                     "time.sleep(30)\nreturn [1.0]" % grandchild)
            heights, reason = self.run_program(program(spawn), timeout=2)
            self.assertIsNone(heights)
            self.assertIn("2 seconds", reason)
            time.sleep(6)
            self.assertFalse(marker.exists(), "the candidate's own child outlived the timeout")

    def test_output_beyond_the_bound_is_refused(self):
        code = program("import sys, time\nsys.stdout.write('x' * %d)\nsys.stdout.flush()\ntime.sleep(30)\nreturn [1.0]"
                       % (pilot.MAX_OUTPUT + 1024))
        heights, reason = self.run_program(code, timeout=25)
        self.assertIsNone(heights)
        self.assertIn("printed more than", reason)

    def test_a_normal_exit_also_stops_what_the_candidate_spawned(self):
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "survived.txt"
            grandchild = "import time; time.sleep(3); open(%r, 'w').close()" % str(marker)
            spawn = "import subprocess, sys\nsubprocess.Popen([sys.executable, '-c', %r])\nreturn [1.0]" % grandchild
            self.assertEqual(([1.0], None), self.run_program(program(spawn)))
            time.sleep(6)
            self.assertFalse(marker.exists(), "the candidate's own child outlived a construction that finished")

    def test_memory_beyond_the_limit_fails_the_construction(self):
        # Twice the limit, committed (np.ones writes every page), so it fails on allocation and not on touch.
        code = program("block = np.ones(%d, dtype=np.uint8)\nreturn [float(block[-1])]" % (2 * pilot.CONSTRUCT_MEMORY))
        heights, reason = self.run_program(code)
        self.assertIsNone(heights)
        self.assertIn("allocate", reason)

    def test_output_printed_just_before_exit_is_still_refused(self):
        # The child has exited before the first poll, so only the check after the loop can see what it printed.
        release = pilot._release

        def finished(process):
            release(process)
            process.wait(timeout=10)

        code = program("import sys\nsys.stdout.write('x' * %d)\nreturn [1.0]" % (pilot.MAX_OUTPUT + 1024))
        with mock.patch.object(pilot, "_release", side_effect=finished):
            heights, reason = self.run_program(code)
        self.assertIsNone(heights)
        self.assertIn("printed more than", reason)

    def test_heights_growing_past_the_bound_are_stopped_while_the_child_runs(self):
        code = program("import time\nwith open('heights.json', 'w') as out:\n    out.write('1' * %d)\n"
                       "time.sleep(30)\nreturn [1.0]" % (pilot.MAX_OUTPUT + 1024))
        started = time.monotonic()
        heights, reason = self.run_program(code, timeout=20)
        self.assertIsNone(heights)
        self.assertIn("bytes of heights", reason)
        self.assertLess(time.monotonic() - started, 15)

    def test_heights_beyond_the_bound_are_refused_before_parsing(self):
        heights, reason = self.run_program(program("return np.full(600000, 0.123456789123)"))
        self.assertIsNone(heights)
        self.assertIn("bytes of heights", reason)

    def test_the_child_does_not_inherit_the_runner_environment(self):
        code = program("import os\nreturn [float('PILOT_SECRET' in os.environ)]")
        with mock.patch.dict("os.environ", {"PILOT_SECRET": "x"}):
            self.assertEqual(([0.0], None), self.run_program(code))


class EvaluateCallbackTests(unittest.TestCase):
    def setUp(self):
        self.evaluate = pilot.evaluate_callback(PROBLEM, PYTHON)

    def test_a_feasible_construction_scores_as_the_verifier_does(self):
        heights = smoke.improved(PROBLEM)
        code = program("return " + repr(heights))
        reply = self.evaluate(code)
        self.assertEqual("valid", reply["status"])
        self.assertAlmostEqual(tasks.score(PROBLEM, heights)["combined_score"], reply["quality"], places=12)
        self.assertEqual(candidate_hash(code), reply["candidate_hash"])
        self.assertFalse(reply["unknown_work"])
        self.assertGreaterEqual(reply["work_units"], 0.0)
        self.assertIn("combined_score", reply["artifacts"]["verifier"])

    def test_an_infeasible_construction_is_valid_at_zero_with_the_reason_shown(self):
        reply = self.evaluate(program("return [2.0, 2.0]"))
        self.assertEqual(("valid", 0.0), (reply["status"], reply["quality"]))
        self.assertTrue(reply["artifacts"]["verifier"].startswith("refused, scored 0.0: infeasible"))

    def test_a_crash_is_invalid_with_no_quality(self):
        reply = self.evaluate(program("raise ValueError('no')"))
        self.assertEqual("invalid", reply["status"])
        self.assertNotIn("quality", reply)

    def test_an_unknown_problem_is_refused(self):
        with self.assertRaises(KeyError):
            pilot.evaluate_callback("c4-uncertainty-hermite", PYTHON)

    def test_every_scored_construction_is_recorded_and_a_crash_is_not(self):
        recorded = []
        evaluate = pilot.evaluate_callback(PROBLEM, PYTHON, record=lambda *entry: recorded.append(entry))
        heights = smoke.improved(PROBLEM)
        feasible, infeasible = program("return " + repr(heights)), program("return [2.0, 2.0]")
        quality = evaluate(feasible)["quality"]
        evaluate(infeasible)
        evaluate(program("raise ValueError('no')"))
        self.assertEqual([(feasible, heights, quality), (infeasible, [2.0, 2.0], 0.0)], recorded)


CELL = {"Family": PROBLEM, "Arm": "aidotnet", "Seed": 7}


class EvaluatedHeightsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.ledger = pilot.EvaluatedHeights(self.root / "heights")
        self.heights = smoke.improved(PROBLEM)
        self.quality = float(tasks.score(PROBLEM, self.heights)["combined_score"])

    def tearDown(self):
        self.directory.cleanup()

    def returned(self, code, newline="\n"):
        path = self.root / "best.py"
        path.write_text(code, encoding="utf-8", newline=newline)
        return path

    def test_the_endpoint_scores_the_recorded_construction_not_a_fresh_run(self):
        # The returned program would construct the seed if it were run again; the endpoint must not run it.
        code = program("return " + tasks.SEEDS[PROBLEM])
        self.ledger.recorder(CELL)(code, self.heights, self.quality)
        self.assertEqual(self.quality, self.ledger.endpoint(CELL, PROBLEM, self.returned(code)))

    def test_a_program_written_with_crlf_still_matches_its_evaluation(self):
        code = program("return [0.5]")
        self.ledger.recorder(CELL)(code, self.heights, self.quality)
        self.assertEqual(self.quality, self.ledger.endpoint(CELL, PROBLEM, self.returned(code, newline="\r\n")))

    def test_a_program_the_cell_never_evaluated_is_refused(self):
        code = program("return [0.5]")
        self.ledger.recorder(CELL)(code, self.heights, self.quality)
        other = dict(CELL, Arm="openevolve-matched")
        with self.assertRaisesRegex(ValueError, "never evaluated"):
            self.ledger.endpoint(other, PROBLEM, self.returned(code))
        with self.assertRaisesRegex(ValueError, "never evaluated"):
            self.ledger.endpoint(CELL, PROBLEM, self.returned(program("return [0.25]")))

    def test_a_candidate_scored_twice_keeps_its_best_construction(self):
        code = program("return [0.5]")
        seed = eval(tasks.SEEDS[PROBLEM])  # noqa: S307
        seed_quality = float(tasks.score(PROBLEM, seed)["combined_score"])
        self.assertLess(seed_quality, self.quality)
        record = self.ledger.recorder(CELL)
        record(code, seed, seed_quality)
        record(code, self.heights, self.quality)
        record(code, seed, seed_quality)
        self.assertEqual(self.quality, self.ledger.endpoint(CELL, PROBLEM, self.returned(code)))

    def test_recorded_heights_the_verifier_scores_differently_are_refused(self):
        code = program("return [0.5]")
        self.ledger.recorder(CELL)(code, self.heights, self.quality + 1e-9)
        with self.assertRaisesRegex(ValueError, "not the"):
            self.ledger.endpoint(CELL, PROBLEM, self.returned(code))

    def test_a_fresh_attempt_forgets_an_abandoned_one_and_a_resume_keeps_it(self):
        stale = program("return [0.5]")
        seen = []

        def search(cell, directory, resume):
            seen.append(sorted(p.name for p in self.ledger.directory(cell).glob("*.json")))
            path = Path(directory) / "best.py"
            path.write_text(stale, encoding="utf-8")
            return {"program": str(path), "model_calls": 1, "model_tokens": 1}

        execute = pilot.executor({PROBLEM: {"aidotnet": search}}, ledger=self.ledger)
        self.ledger.recorder(CELL)(stale, self.heights, self.quality)
        resumed = execute(CELL, self.root, True)
        self.assertEqual(("completed", self.quality), (resumed["Status"], resumed["Value"]))
        fresh = execute(CELL, self.root, False)
        self.assertEqual([[candidate_hash(stale) + ".json"], []], seen)
        self.assertEqual("failed", fresh["Status"])
        self.assertIn("never evaluated", fresh["Reason"])

    def test_a_broker_session_records_into_the_cells_ledger(self):
        heights = smoke.improved(PROBLEM)
        code = program("return " + repr(heights))
        session = pilot.session(lambda *a, **k: (code, 1), initials={PROBLEM: code}, python=PYTHON, model_calls=1,
                                evaluations=2, seconds=120, model_tokens=10_000, ledger=self.ledger)
        with session(CELL) as broker:
            broker.dispatch("evaluate", {"code": code})
        self.assertEqual(self.quality, self.ledger.endpoint(CELL, PROBLEM, self.returned(code)))


class LibraryTests(unittest.TestCase):
    def test_the_statement_names_the_libraries(self):
        with tempfile.TemporaryDirectory() as directory:
            written = tasks.write(Path(directory) / "t", [PROBLEM])[0]
            config = (written / "config.yaml").read_text(encoding="utf-8")
        self.assertEqual(("numpy", "scipy"), tasks.LIBRARIES)
        self.assertIn("Python has numpy and scipy installed", " ".join(config.split()))

    def test_versions_come_from_the_interpreter_and_a_missing_library_is_refused(self):
        with mock.patch.object(tasks, "LIBRARIES", ("numpy",)):
            import numpy
            self.assertEqual({"numpy": numpy.__version__}, pilot.library_versions(PYTHON))
        with mock.patch.object(tasks, "LIBRARIES", ("numpy", "no_such_library_for_this_test")):
            with self.assertRaisesRegex(RuntimeError, "no_such_library_for_this_test"):
                pilot.library_versions(PYTHON)

    def test_the_launcher_refuses_an_interpreter_without_them_before_freezing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "pilot"
            with mock.patch.object(tasks, "LIBRARIES", ("no_such_library_for_this_test",)), \
                    self.assertRaises(SystemExit) as refused, contextlib.redirect_stdout(io.StringIO()):
                runner.main(["--root", str(root), "--cli", "dotnet", "x.dll", "--upstream", directory, "--stub"])
            self.assertIn("no_such_library_for_this_test", str(refused.exception))
            self.assertFalse(root.exists())


class EndpointTests(unittest.TestCase):
    def test_a_program_that_was_never_scored_fails_its_cell_with_the_spend_kept(self):
        def search(cell, directory, resume):
            path = Path(directory) / "best.py"
            path.write_text(program("raise RuntimeError('late failure')"), encoding="utf-8")
            return {"program": str(path), "model_calls": 7, "model_tokens": 70}

        with tempfile.TemporaryDirectory() as directory:
            execute = pilot.executor({PROBLEM: {"aidotnet": search}},
                                     ledger=pilot.EvaluatedHeights(Path(directory) / "heights"))
            row = execute({"Family": PROBLEM, "Arm": "aidotnet", "Seed": 1}, directory, False)
        self.assertEqual(("failed", 7), (row["Status"], row["ModelCalls"]))
        self.assertIn("never evaluated", row["Reason"])

    def test_cells_dispatch_on_their_problem(self):
        seen = []

        def searcher(problem):
            def search(cell, directory, resume):
                seen.append(problem)
                code = program("return " + tasks.SEEDS[problem])
                pilot.evaluate_callback(problem, PYTHON, record=ledger.recorder(cell))(code)
                path = Path(directory) / ("%s.py" % problem)
                path.write_text(code, encoding="utf-8")
                return {"program": str(path), "model_calls": 1, "model_tokens": 1}
            return search

        problems = ["c5-erdos-overlap", "c2-autocorrelation"]
        with tempfile.TemporaryDirectory() as directory:
            ledger = pilot.EvaluatedHeights(Path(directory) / "heights")
            execute = pilot.executor({p: {"aidotnet": searcher(p)} for p in problems}, ledger=ledger)
            row = execute({"Family": "c2-autocorrelation", "Arm": "aidotnet", "Seed": 1}, directory, False)
        self.assertEqual(["c2-autocorrelation"], seen)
        seed = tasks.score("c2-autocorrelation", eval(tasks.SEEDS["c2-autocorrelation"]))["combined_score"]  # noqa: S307
        self.assertAlmostEqual(seed, row["Value"], places=12)


class RegistrationTests(unittest.TestCase):
    def test_each_problem_is_a_maximised_family_sealed_by_its_verifier(self):
        problems = ["c5-erdos-overlap", "c3prime-autocorrelation"]
        declared = pilot.spec(problems, seed_count=6, model="sonnet", model_calls=10, model_tokens=100_000,
                              wall_seconds=600)
        self.assertEqual(problems, [family["Family"] for family in declared["Families"]])
        self.assertEqual({("published_ratio", "maximize", 0.0)},
                         {(f["Endpoint"]["Metric"], f["Endpoint"]["Direction"], f["Endpoint"]["FailureValue"])
                          for f in declared["Families"]})
        self.assertEqual({p: pilot.verifier_seal(p) for p in problems}, declared["TestPartitionHashes"])
        with tempfile.TemporaryDirectory() as directory:
            registration.register(declared, Path(directory) / "registration")

    def test_seals_differ_by_problem_and_follow_the_verifier_source(self):
        self.assertNotEqual(pilot.verifier_seal("c3-autocorrelation"), pilot.verifier_seal("c3prime-autocorrelation"))
        original = Path.read_bytes

        def edited(path):
            data = original(path)
            return data + b"\n# edited" if path.name == "alphaevolve_math.py" else data

        before = pilot.verifier_seal(PROBLEM)
        with mock.patch.object(Path, "read_bytes", edited):
            self.assertNotEqual(before, pilot.verifier_seal(PROBLEM))

    def test_a_repeated_or_unknown_problem_is_refused(self):
        for problems in (["c5-erdos-overlap", "c5-erdos-overlap"], ["c4-uncertainty-hermite"], []):
            with self.assertRaises(ValueError):
                pilot.spec(problems, seed_count=6, model="sonnet", model_calls=10, model_tokens=100_000,
                           wall_seconds=600)


class StubTests(unittest.TestCase):
    def test_the_stub_tells_c3_from_c3prime(self):
        answers = {"c3-autocorrelation": "C3", "c3prime-autocorrelation": "C3PRIME"}
        provider = pilot.stub_provider([], answers)
        for problem, expected in answers.items():
            system = "\n".join("    " + line for line in tasks._wrap(tasks.STATEMENTS[problem]))
            self.assertEqual(expected, provider(system, [{"role": "user", "content": "go"}])[0])


class FeedbackTests(unittest.TestCase):
    def feedback(self, problem):
        return pilot.verifier_feedback(problem, {"value": 1.0, "published": 1.0, "combined_score": 1.0, "length": 4})

    def test_the_direction_word_matches_the_problem(self):
        directions = {problem: pilot.headroom.IN_SCOPE[problem]["direction"] for problem in tasks.TASKS}
        self.assertIn("minimize", directions.values())
        self.assertIn("maximize", directions.values())
        for problem, direction in directions.items():
            expected = "lower is better" if direction == "minimize" else "higher is better"
            self.assertIn(expected, self.feedback(problem), problem)


class LauncherStateTests(unittest.TestCase):
    def test_an_incomplete_task_directory_is_regenerated_and_a_complete_one_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            written = runner.task_directories(directory, [PROBLEM])[PROBLEM]
            (written / "evaluator.py").unlink()
            runner.task_directories(directory, [PROBLEM])
            self.assertTrue((written / "evaluator.py").is_file())
            marker = written / "kept.txt"
            marker.write_text("x", encoding="utf-8")
            runner.task_directories(directory, [PROBLEM])
            self.assertTrue(marker.exists(), "a complete directory was rewritten")

    def test_python_resolves_a_bare_name_through_path(self):
        parser = runner.build_parser()
        name = Path(sys.executable).name
        with mock.patch("shutil.which", return_value=sys.executable):
            self.assertEqual(str(Path(sys.executable).absolute()), runner.resolve_python(parser, name))
        with mock.patch("shutil.which", return_value=None), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                runner.resolve_python(parser, "no-such-python")
        self.assertEqual(str(Path(sys.executable).absolute()), runner.resolve_python(parser, sys.executable))
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stderr(io.StringIO()):
            for missing in (str(Path(directory) / "python"), directory, "venv" + "/" + "no-such-python"):
                with self.assertRaises(SystemExit):
                    runner.resolve_python(parser, missing)

    def test_a_state_directory_refuses_the_other_provider_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / "state"
            runner.check_provider_mode(state, True)
            runner.check_provider_mode(state, True)
            with self.assertRaises(SystemExit):
                runner.check_provider_mode(state, False)

    def test_cells_without_a_recorded_mode_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / "state"
            (state / "cell-0").mkdir(parents=True)
            with self.assertRaises(SystemExit):
                runner.check_provider_mode(state, False)

    def test_a_resume_with_other_iterations_or_none_frozen_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "iterations.json"
            with self.assertRaises(SystemExit):
                runner.frozen_iterations(path, 25)
            path.write_text(json.dumps({"Iterations": 25}), encoding="utf-8")
            self.assertEqual(25, runner.frozen_iterations(path, 25))
            with self.assertRaises(SystemExit):
                runner.frozen_iterations(path, 26)


class RunnerTests(unittest.TestCase):
    def test_a_seed_count_that_leaves_positions_unbalanced_is_refused_before_freezing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "pilot"
            with self.assertRaises(SystemExit) as refused, contextlib.redirect_stdout(io.StringIO()):
                runner.main(["--root", str(root), "--cli", "dotnet", "x.dll", "--upstream", directory,
                             "--seed-count", "3", "--stub"])
            self.assertIn("multiple of 6", str(refused.exception))
            self.assertFalse(root.exists())

    def test_a_nonpositive_iteration_count_is_refused_before_freezing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "pilot"
            for iterations in ("0", "-3"):
                with self.assertRaises(SystemExit) as refused, contextlib.redirect_stdout(io.StringIO()):
                    runner.main(["--root", str(root), "--cli", "dotnet", "x.dll", "--upstream", directory,
                                 "--iterations", iterations, "--stub"])
                self.assertIn("at least 1", str(refused.exception))
                self.assertFalse(root.exists())

    def test_a_resume_with_another_budget_is_refused_and_the_frozen_one_is_described(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "pilot"
            first = ["--root", str(root), "--cli", "dotnet", "x.dll", "--upstream", directory, "--stub",
                     "--problem", PROBLEM, "--model-calls", "5", "--model-tokens", "100000"]
            with mock.patch.object(pilot, "library_versions", return_value={}), \
                    contextlib.redirect_stdout(io.StringIO()) as shown:
                self.assertEqual(2, runner.main(first))
            self.assertIn("per cell             5 calls", shown.getvalue())
            for flag, value in (("--model-calls", "6"), ("--model-tokens", "100001"), ("--wall-seconds", "3599"),
                                ("--seed-count", "12")):
                again = list(first)
                if flag in again:
                    again[again.index(flag) + 1] = value
                else:
                    again += [flag, value]
                with mock.patch.object(pilot, "library_versions", return_value={}), \
                        self.assertRaises(SystemExit) as refused, contextlib.redirect_stdout(io.StringIO()):
                    runner.main(again)
                self.assertIn(flag, str(refused.exception))
            frozen = registration.load_registration(root / "registration" / "registration.json")
            lines = runner.describe(runner.runner.project(frozen), frozen,
                                    runner.build_parser().parse_args(first + ["--model-calls", "9"]), [PROBLEM])
            self.assertIn("per cell             5 calls over 25 iterations, which is the broker's own limit", lines)

    def test_the_engines_evaluator_timeout_exceeds_the_construction_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            written = runner.task_directories(directory, [PROBLEM])[PROBLEM]
            config = (written / "config.yaml").read_text(encoding="utf-8")
        self.assertIn("evaluator:\n  timeout: %d\n" % pilot.ENGINE_EVALUATOR_TIMEOUT, config)
        self.assertIn("%d-second limit" % pilot.CONSTRUCT_TIMEOUT, config)
        self.assertGreater(pilot.ENGINE_EVALUATOR_TIMEOUT, pilot.CONSTRUCT_TIMEOUT)

    def test_write_refuses_an_evaluator_timeout_below_the_construction_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                tasks.write(Path(directory) / "t", [PROBLEM], evaluator_timeout=tasks.TIMEOUT_SECONDS - 1)
            self.assertFalse((Path(directory) / "t").exists())


if __name__ == "__main__":
    unittest.main()
