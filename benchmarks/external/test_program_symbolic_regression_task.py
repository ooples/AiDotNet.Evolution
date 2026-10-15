"""The pilot task both arms run. What matters is that neither arm can see the held-out split.

V1-07 (#116) needs a paired pilot, and a pair is only a pair if both arms search the same task. So
this asserts the task's contract rather than any arm's result: the seed is weak but scorable, a
broken candidate scores worst instead of raising, and selection reads train and validation only.
"""
import importlib
import os
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "symbolic_regression_task"))

import symbolic_regression_family as family  # noqa: E402

TASK_DIR = ROOT / "symbolic_regression_task"
SEED_PROGRAM = TASK_DIR / "initial_program.py"


def evaluator_module():
    """Imported fresh, so EVOLUTION_SR_TASK/SEED are read at the value set by the caller."""
    for name in ("evaluator",):
        sys.modules.pop(name, None)
    return importlib.import_module("evaluator")


class PilotTaskTests(unittest.TestCase):
    def setUp(self):
        self.previous = {key: os.environ.get(key) for key in ("EVOLUTION_SR_TASK", "EVOLUTION_SR_SEED")}

    def tearDown(self):
        for key, value in self.previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    def test_the_seed_is_weak_but_scorable_on_every_task(self):
        # A seed that already fits leaves the arms nothing to differ on; one that crashes makes every
        # arm's first evaluation a failure. Both would make the pilot measure the fixture.
        for task in family.TASKS:
            with self.subTest(task=task):
                os.environ["EVOLUTION_SR_TASK"] = task
                os.environ["EVOLUTION_SR_SEED"] = str(family.DEV_SEEDS[0])
                metrics = evaluator_module().evaluate(str(SEED_PROGRAM))
                self.assertNotIn("error", metrics, "the seed must score, not raise")
                self.assertGreater(metrics["combined_score"], 0.0)
                self.assertLess(metrics["combined_score"], 0.95, "a seed this good would leave no headroom")

    def test_selection_never_reads_the_held_out_split(self):
        # The pilot's endpoint is the sealed test split, scored afterwards. If the evaluator could see
        # those rows, the endpoint would be a training score. Asserted by tampering with the held-out
        # rows the evaluator would receive and requiring its score not to move.
        os.environ["EVOLUTION_SR_TASK"] = "damped_oscillator"
        os.environ["EVOLUTION_SR_SEED"] = str(family.DEV_SEEDS[0])
        module = evaluator_module()
        honest = module.evaluate(str(SEED_PROGRAM))

        genuine = module.family.build
        tampered = genuine("damped_oscillator", family.DEV_SEEDS[0])
        tampered["test"] = [(xs, y + 1000.0) for xs, y in tampered["test"]]
        try:
            module.family.build = lambda *args, **kwargs: tampered
            with_tampered_test = module.evaluate(str(SEED_PROGRAM))
            # Reachability control: the injected split must actually reach the evaluator, or the
            # assertion above is vacuous. Its train rows are the genuine ones, so the score is equal;
            # proving arrival needs a split whose TRAIN rows differ.
            moved = genuine("damped_oscillator", family.DEV_SEEDS[1])
            module.family.build = lambda *args, **kwargs: moved
            with_other_train = module.evaluate(str(SEED_PROGRAM))
        finally:
            module.family.build = genuine

        self.assertEqual(honest["combined_score"], with_tampered_test["combined_score"],
                         "the score moved when only held-out rows changed")
        self.assertNotEqual(honest["combined_score"], with_other_train["combined_score"],
                            "the injected split never reached the evaluator, so the test above proves nothing")

    def test_a_broken_candidate_scores_worst_rather_than_raising(self):
        os.environ["EVOLUTION_SR_TASK"] = "rational_gain"
        os.environ["EVOLUTION_SR_SEED"] = str(family.DEV_SEEDS[0])
        module = evaluator_module()
        for body in ("def predict(*xs):\n    raise ZeroDivisionError('boom')\n",
                     "def predict(*xs):\n    return float('nan')\n",
                     "x = 1\n"):  # no predict at all
            with self.subTest(body=body.splitlines()[0]):
                path = Path(self.enterContext(__import__("tempfile").TemporaryDirectory())) / "candidate.py"
                path.write_text(body, encoding="utf-8")
                metrics = module.evaluate(str(path))
                self.assertEqual(0.0, metrics["combined_score"])

    def test_the_config_declares_what_both_arms_need(self):
        import yaml
        with (TASK_DIR / "config.yaml").open(encoding="utf-8") as handle:
            config = yaml.safe_load(handle)
        self.assertEqual(10, config["max_iterations"])
        self.assertFalse(config["evaluator"]["cascade_evaluation"], "the pilot scores one stage, not a cascade")
        # llm.models, not primary_model: the shape #267's stalling-config notice was written for.
        self.assertIn("models", config["llm"])
        self.assertEqual(["claude-sonnet-4-5"], [model["name"] for model in config["llm"]["models"]])


if __name__ == "__main__":
    unittest.main()
