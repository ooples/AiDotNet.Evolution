"""E-4 (#347): the AlphaEvolve pilot's seams - construction, broker scoring, the endpoint, the registration."""
from __future__ import annotations

import io
import contextlib
import json
from pathlib import Path
import sys
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


class EndpointTests(unittest.TestCase):
    def test_the_endpoint_rescores_the_returned_program(self):
        heights = smoke.improved(PROBLEM)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "best.py"
            path.write_text(program("return " + repr(heights)), encoding="utf-8")
            self.assertAlmostEqual(tasks.score(PROBLEM, heights)["combined_score"],
                                   pilot.score_endpoint(PROBLEM, path, PYTHON), places=12)

    def test_a_program_that_does_not_construct_fails_its_cell_with_the_spend_kept(self):
        def search(cell, directory, resume):
            path = Path(directory) / "best.py"
            path.write_text(program("raise RuntimeError('late failure')"), encoding="utf-8")
            return {"program": str(path), "model_calls": 7, "model_tokens": 70}

        execute = pilot.executor({PROBLEM: {"aidotnet": search}}, python=PYTHON)
        with tempfile.TemporaryDirectory() as directory:
            row = execute({"Family": PROBLEM, "Arm": "aidotnet", "Seed": 1}, directory, False)
        self.assertEqual(("failed", 7), (row["Status"], row["ModelCalls"]))
        self.assertIn("late failure", row["Reason"])

    def test_cells_dispatch_on_their_problem(self):
        seen = []

        def searcher(problem):
            def search(cell, directory, resume):
                seen.append(problem)
                path = Path(directory) / ("%s.py" % problem)
                path.write_text(program("return " + tasks.SEEDS[problem]), encoding="utf-8")
                return {"program": str(path), "model_calls": 1, "model_tokens": 1}
            return search

        problems = ["c5-erdos-overlap", "c2-autocorrelation"]
        execute = pilot.executor({p: {"aidotnet": searcher(p)} for p in problems}, python=PYTHON)
        with tempfile.TemporaryDirectory() as directory:
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
        relative = "venv" + "/" + "python"
        self.assertEqual(str(Path(relative).absolute()), runner.resolve_python(parser, relative))

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
