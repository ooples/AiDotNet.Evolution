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

    def test_the_evaluator_never_builds_the_held_out_rows(self):
        """Reading, not just tampering. Review found the earlier version wide open here.

        The old evaluator built the whole split and the held-out seed was `seed + TEST_SEED_OFFSET`,
        so a candidate could `import symbolic_regression_family`, read `EVOLUTION_SR_TASK` and
        `EVOLUTION_SR_SEED` from its own environment, and rebuild the endpoint's rows exactly - the
        rebuilt seal matched. A seal stops tampering; it does nothing about reading.
        """
        os.environ["EVOLUTION_SR_TASK"] = "rational_gain"
        os.environ["EVOLUTION_SR_SEED"] = str(family.DEV_SEEDS[0])
        module = evaluator_module()

        asked = []
        genuine_selection, genuine_holdout = module.family.selection, module.family.holdout
        try:
            module.family.selection = lambda *args, **kwargs: (asked.append("selection")
                                                               or genuine_selection(*args, **kwargs))
            module.family.holdout = lambda *args, **kwargs: asked.append("holdout")
            metrics = module.evaluate(str(SEED_PROGRAM))
        finally:
            module.family.selection, module.family.holdout = genuine_selection, genuine_holdout

        self.assertEqual(["selection"], asked, "the evaluator asked for held-out rows")
        self.assertNotIn("error", metrics)

    def test_the_endpoints_seed_is_not_in_the_candidates_environment(self):
        # The selection seed is exported, because the evaluator needs it. The endpoint's seed is
        # chosen by the scoring side and must not be derivable from what the candidate can read.
        os.environ["EVOLUTION_SR_TASK"] = "rational_gain"
        os.environ["EVOLUTION_SR_SEED"] = str(family.DEV_SEEDS[0])
        exported = {key: value for key, value in os.environ.items() if key.startswith("EVOLUTION_SR")}
        self.assertEqual({"EVOLUTION_SR_TASK", "EVOLUTION_SR_SEED"}, set(exported))

        chosen = 918273
        from_environment = family.holdout("rational_gain", family.DEV_SEEDS[0] + family.TEST_SEED_OFFSET)
        self.assertNotEqual(family.holdout("rational_gain", chosen)["test_seal"],
                            from_environment["test_seal"],
                            "the endpoint's rows are derivable from the exported seed")

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
