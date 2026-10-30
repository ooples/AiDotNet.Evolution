"""E-4 (#347): the AlphaEvolve task harness scores what the verifier scores, and says why it refuses.

The five tasks are what a searcher is pointed at, so the checks are on the two things a campaign
reads from them: the score (oriented against the published value and the committed floor) and the
refusal (a bad construction scores 0.0 with a reason, never an exception that ends the run).
`alphaevolve_tasks_smoke.py` covers the other half - both engines running the generated directories.
"""
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import alphaevolve_headroom as headroom  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402


class AlphaEvolveTaskTests(unittest.TestCase):
    def test_the_tasks_are_the_problems_with_room_and_c4_is_left_out(self):
        self.assertEqual(set(tasks.TASKS), set(headroom.IN_SCOPE) - {"c4-uncertainty-hermite"})
        self.assertEqual(set(tasks.TASKS), set(tasks.SEEDS))

    def test_each_statement_names_the_direction_the_verifier_is_scored_in(self):
        for problem in tasks.TASKS:
            direction = headroom.IN_SCOPE[problem]["direction"].upper()
            other = "MAXIMIZE" if direction == "MINIMIZE" else "MINIMIZE"
            self.assertIn(direction, tasks.STATEMENTS[problem], problem)
            self.assertNotIn(other, tasks.STATEMENTS[problem], problem)

    def test_the_floor_is_the_committed_bundle(self):
        bundle = json.loads(tasks.FLOOR_BUNDLE.read_text(encoding="utf-8"))
        floors = {row["Problem"]: row["FloorBest"] for row in bundle["Rows"]}
        for problem in tasks.TASKS:
            self.assertEqual(tasks.FLOORS[problem], floors[problem])

    def test_every_seed_is_feasible_and_weaker_than_the_floor(self):
        for problem in tasks.TASKS:
            result = tasks.score(problem, eval(tasks.SEEDS[problem]))  # noqa: S307 - the module's own constant
            self.assertNotIn("error", result, problem)
            self.assertGreater(result["combined_score"], 0.0, problem)
            self.assertFalse(result["beats_floor"], problem)
            self.assertGreater(result["gap_fraction"], 0.0, problem)

    def test_the_score_is_the_verifiers_value_against_the_published_one_higher_is_better(self):
        heights = [0.2, 0.9, 1.0, 0.7, 0.4, 0.8]
        c1 = tasks.score("c1-autocorrelation", heights)
        value = headroom.IN_SCOPE["c1-autocorrelation"]["objective"](heights)
        self.assertEqual(c1["value"], value)
        self.assertAlmostEqual(c1["combined_score"], 1.5053 / value, places=12)
        self.assertAlmostEqual(c1["gap_fraction"], (value - 1.5053) / 1.5053, places=12)
        self.assertEqual(c1["length"], 6)

        c2 = tasks.score("c2-autocorrelation", heights)
        value = headroom.IN_SCOPE["c2-autocorrelation"]["objective"](heights)
        self.assertAlmostEqual(c2["combined_score"], value / 0.8962, places=12)
        self.assertAlmostEqual(c2["gap_fraction"], (0.8962 - value) / 0.8962, places=12)

    def test_matching_the_published_value_scores_one_and_passing_the_floor_is_reported(self):
        for problem in tasks.TASKS:
            spec = headroom.IN_SCOPE[problem]
            at = tasks.distance(problem, spec["published"])
            self.assertAlmostEqual(at["combined_score"], 1.0, places=12)
            self.assertEqual(at["gap_fraction"], 0.0)
            self.assertTrue(at["beats_floor"], problem)
            floor = tasks.FLOORS[problem]
            self.assertFalse(tasks.distance(problem, floor)["beats_floor"], problem)
            worse = floor * (1.01 if spec["direction"] == "minimize" else 0.99)
            self.assertFalse(tasks.distance(problem, worse)["beats_floor"], problem)
            better = floor * (0.99 if spec["direction"] == "minimize" else 1.01)
            self.assertTrue(tasks.distance(problem, better)["beats_floor"], problem)

    def test_an_infeasible_construction_scores_zero_and_states_the_constraint(self):
        for problem, construction in (("c5-erdos-overlap", [1.0] * 16), ("c1-autocorrelation", [1.0, -0.5, 1.0]),
                                      ("c3-autocorrelation", [1.0, -1.0]), ("c2-autocorrelation", [1.0, math.nan])):
            result = tasks.score(problem, construction)
            self.assertEqual(result["combined_score"], 0.0, problem)
            self.assertEqual(result["error"], "infeasible: " + tasks.STATEMENTS[problem])

    def test_a_malformed_construction_scores_zero_with_its_reason(self):
        cases = {
            "nested": ([[1.0, 2.0], [3.0, 4.0]], "flat sequence"),
            "text": (["a", "b"], "sequence of reals"),
            "long": ([0.5] * (tasks.MAX_LENGTH + 1), "exceeds the %d-entry limit" % tasks.MAX_LENGTH),
        }
        for name, (construction, reason) in cases.items():
            result = tasks.score("c5-erdos-overlap", construction)
            self.assertEqual(result["combined_score"], 0.0, name)
            self.assertIn(reason, result["error"], name)
        self.assertNotIn("error", tasks.score("c5-erdos-overlap", [0.5] * tasks.MAX_LENGTH))

    def test_a_value_that_cannot_be_ranked_scores_zero(self):
        # No verifier in scope returns these today; a ratio against zero or infinity would rank a broken
        # verifier's output above every real construction, so the guard is held here.
        spec = headroom.IN_SCOPE["c1-autocorrelation"]
        original = spec["objective"]
        try:
            for bad in (0.0, math.inf, -1.0):
                spec["objective"] = lambda heights, bad=bad: bad
                result = tasks.score("c1-autocorrelation", [1.0, 1.0])
                self.assertEqual(result["combined_score"], 0.0, bad)
                self.assertIn("cannot be ranked", result["error"])
        finally:
            spec["objective"] = original

    def test_a_candidate_that_raises_or_defines_nothing_scores_zero_instead_of_ending_the_run(self):
        evaluate = tasks.evaluator("c2-autocorrelation")
        with tempfile.TemporaryDirectory() as directory:
            raising = Path(directory) / "raising.py"
            raising.write_text("def construct():\n    raise ValueError('no idea')\n", encoding="utf-8")
            empty = Path(directory) / "empty.py"
            empty.write_text("X = 1\n", encoding="utf-8")
            self.assertEqual(evaluate(str(raising)), {"combined_score": 0.0, "error": "ValueError: no idea"})
            exiting = Path(directory) / "exiting.py"
            exiting.write_text("import sys\n\ndef construct():\n    sys.exit(3)\n", encoding="utf-8")
            self.assertEqual(evaluate(str(exiting)), {"combined_score": 0.0, "error": "SystemExit: 3"})
            result = evaluate(str(empty))
            self.assertEqual(result["combined_score"], 0.0)
            self.assertIn("construct()", result["error"])
        with self.assertRaises(KeyError):
            tasks.evaluator("c4-uncertainty-hermite")

    def test_write_produces_a_task_each_engine_can_load_and_refuses_to_overwrite(self):
        import yaml

        with tempfile.TemporaryDirectory() as directory:
            written = tasks.write(directory)
            self.assertEqual([path.name for path in written], list(tasks.TASKS))
            for path in written:
                problem = path.name
                self.assertEqual(sorted(child.name for child in path.iterdir()),
                                 ["config.yaml", "evaluator.py", "initial_program.py", "support"])
                self.assertEqual(sorted(child.name for child in (path / "support").iterdir()),
                                 sorted(tasks.SUPPORT + ("floors.json",)))
                import runpy

                evaluate = runpy.run_path(str(path / "evaluator.py"))["evaluate"]
                self.assertEqual(evaluate(str(path / "initial_program.py")),
                                 tasks.score(problem, eval(tasks.SEEDS[problem])))  # noqa: S307
                config = yaml.safe_load((path / "config.yaml").read_text(encoding="utf-8"))
                message = " ".join(config["prompt"]["system_message"].split())
                self.assertIn(" ".join(tasks.STATEMENTS[problem].split()), message)
                self.assertIn(str(headroom.IN_SCOPE[problem]["published"]), message)
                self.assertEqual(config["evaluator"]["timeout"], tasks.TIMEOUT_SECONDS)
                # The message asks for a whole program, so the config must not leave both engines in diff mode.
                self.assertIs(config["diff_based_evolution"], False)
                self.assertIn("complete program in a python", message)

            with self.assertRaises(FileExistsError):
                tasks.write(directory, ["c1-autocorrelation"])

    def test_a_task_runs_from_its_own_directory_with_the_checkout_out_of_reach(self):
        # A container mounts only the evaluator's directory. Run the generated evaluator in a fresh
        # interpreter, from a copy whose only neighbours are its own files, with sys.path stripped of
        # the checkout: it must score the seed exactly as the checkout does.
        import shutil
        import subprocess

        with tempfile.TemporaryDirectory() as directory:
            source = tasks.write(Path(directory) / "written", ["c5-erdos-overlap"])[0]
            moved = Path(directory) / "elsewhere" / "task"
            shutil.copytree(source, moved)
            shutil.rmtree(source)
            probe = (
                "import json, runpy, sys\n"
                "sys.path = [p for p in sys.path if 'benchmarks' not in p]\n"
                "evaluate = runpy.run_path(sys.argv[1])['evaluate']\n"
                "print(json.dumps(evaluate(sys.argv[2])))\n")
            run = subprocess.run([sys.executable, "-I", "-c", probe, str(moved / "evaluator.py"),
                                  str(moved / "initial_program.py")], capture_output=True, text=True, timeout=120,
                                 cwd=str(moved))
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertEqual(json.loads(run.stdout), json.loads(json.dumps(tasks.score("c5-erdos-overlap", [0.5] * 16))))
            self.assertNotIn(str(HERE), (moved / "evaluator.py").read_text(encoding="utf-8"))
            self.assertEqual(json.loads((moved / "support" / "floors.json").read_text(encoding="utf-8")),
                             {problem: tasks.FLOORS[problem] for problem in tasks.TASKS})

    def test_a_refused_write_leaves_nothing_behind(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "c5-erdos-overlap").mkdir()
            with self.assertRaises(FileExistsError):
                tasks.write(root, ["c1-autocorrelation", "c5-erdos-overlap"])
            with self.assertRaises(ValueError):
                tasks.write(root, ["c2-autocorrelation", "c2-autocorrelation"])
            with self.assertRaises(KeyError):
                tasks.write(root, ["c3-autocorrelation", "c4-uncertainty-hermite"])
            self.assertEqual(sorted(path.name for path in root.iterdir()), ["c5-erdos-overlap"])

    def test_a_dangling_link_at_a_destination_is_refused_before_anything_is_written(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            try:
                (root / "c5-erdos-overlap").symlink_to(root / "missing", target_is_directory=True)
            except OSError as error:  # Windows without the symlink privilege
                self.skipTest("cannot create a symlink here: %s" % error)
            with self.assertRaises(FileExistsError):
                tasks.write(root, ["c1-autocorrelation", "c5-erdos-overlap"])
            self.assertFalse((root / "c1-autocorrelation").exists())


class AlphaEvolveSmokeTests(unittest.TestCase):
    """The smoke's pass rule and its readers, without running either engine."""

    def setUp(self):
        import alphaevolve_tasks_smoke as smoke

        self.smoke = smoke

    def test_a_row_passes_only_when_the_seed_was_scored_and_the_best_is_the_stand_ins(self):
        ran = {"status": "ran", "seed_scored": True, "best": 0.9}
        self.assertTrue(self.smoke.passed(ran, 0.9))
        self.assertFalse(self.smoke.passed(dict(ran, seed_scored=False), 0.9))
        self.assertFalse(self.smoke.passed(dict(ran, best=0.75), 0.9))
        self.assertFalse(self.smoke.passed(dict(ran, best=None), 0.9))
        self.assertFalse(self.smoke.passed(dict(ran, status="failed"), 0.9))

    def test_a_trace_line_cut_short_is_skipped_rather_than_ending_the_smoke(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "trace-000.jsonl"
            trace.write_text('{"variationOperatorId":"seed","status":"Completed","quality":0.5}\n'
                             '{"diagnostics":[{"code":"evaluator_exception","message":"boom"}]}\n'
                             '{"variationOperatorId":"llm","stat\n', encoding="utf-8")
            records = list(self.smoke.trace_records(Path(directory)))
            self.assertEqual(len(records), 2)
            self.assertEqual(self.smoke.first_diagnostic(Path(directory)), "evaluator_exception: boom")

    def test_the_seed_score_is_the_first_evaluation_openevolve_logs_after_adding_it(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "openevolve.log"
            log.write_text(
                "x - openevolve.evaluator - INFO - Evaluated program a in 0.01s: combined_score=0.1111\n"
                "x - openevolve.controller - INFO - Adding initial program to database\n"
                "x - openevolve.evaluator - INFO - Evaluated program b in 0.01s: value=2.0000, combined_score=0.7527\n"
                "x - openevolve.evaluator - INFO - Evaluated program c in 0.01s: combined_score=0.9015\n",
                encoding="utf-8")
            self.assertEqual(self.smoke.logged_seed_score(Path(directory)), 0.7527)
            log.write_text("x - openevolve.controller - INFO - Adding initial program to database\n", encoding="utf-8")
            self.assertIsNone(self.smoke.logged_seed_score(Path(directory)))


if __name__ == "__main__":
    unittest.main()
