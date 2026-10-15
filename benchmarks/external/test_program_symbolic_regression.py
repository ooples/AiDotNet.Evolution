"""The instrument has to separate a generalising candidate from one that memorised the train split.

Arms that must fail are the point. A scorer that returned 0 for everything would satisfy the
ground-truth arm alone, so every test below is paired with a case it must reject: a lookup table
scores well on train and badly on test, a tampered test split is refused, and a candidate that
raises or returns a non-finite number scores worst rather than aborting the run.
"""
import math
import unittest

import symbolic_regression_family as family


class SymbolicRegressionFamilyTests(unittest.TestCase):
    def test_the_ground_truth_scores_zero_on_every_split(self):
        for task in family.TASKS:
            with self.subTest(task=task):
                split = family.build(task, family.DEV_SEEDS[0])
                result = family.score(family.TASKS[task]["formula"], split)
                for key in ("train_nmse", "validation_nmse", "test_nmse"):
                    self.assertLess(result[key], 1e-24, key)

    def test_a_memorised_train_split_scores_well_on_train_and_fails_held_out(self):
        # The gaming shape this story exists to catch: a table of the rows it was shown, with a
        # fallback for everything else. Without this arm, "the ground truth scores 0" is also what a
        # scorer that ignores its argument reports.
        split = family.build("rational_gain", family.DEV_SEEDS[1])
        table = {xs: y for xs, y in split["train"]}
        mean = sum(y for _, y in split["train"]) / len(split["train"])

        def memorise(*xs):
            return table.get(xs, mean)

        result = family.score(memorise, split)
        self.assertLess(result["train_nmse"], 1e-24, "it reproduces what it was shown")
        self.assertGreater(result["test_nmse"], 0.5, "and generalises no better than the mean")

    def test_predicting_the_mean_scores_about_one(self):
        split = family.build("damped_oscillator", family.DEV_SEEDS[0])
        targets = [y for _, y in split["test"]]
        mean = sum(targets) / len(targets)
        self.assertAlmostEqual(family.nmse(lambda *_: mean, split["test"]), 1.0, places=6)

    def test_a_tampered_test_split_is_refused(self):
        split = family.build("two_variable_ratio", family.DEV_SEEDS[2])
        xs, y = split["test"][0]
        split["test"][0] = (xs, y + 1.0)
        with self.assertRaises(ValueError):
            family.score(family.TASKS["two_variable_ratio"]["formula"], split)

    def test_a_reordered_test_split_is_also_refused(self):
        # The seal covers order as well as content: a harness that shuffles the held-out rows has
        # changed the thing that was sealed, even though every row survives.
        split = family.build("rational_gain", family.DEV_SEEDS[0])
        split["test"].reverse()
        with self.assertRaises(ValueError):
            family.score(family.TASKS["rational_gain"]["formula"], split)

    def test_the_train_and_test_grids_do_not_overlap(self):
        for task in family.TASKS:
            with self.subTest(task=task):
                split = family.build(task, family.DEV_SEEDS[0])
                train = {xs for xs, _ in split["train"]}
                self.assertEqual(set(), train & {xs for xs, _ in split["test"]})

    def test_a_candidate_that_raises_or_returns_nonfinite_scores_worst(self):
        split = family.build("damped_oscillator", family.DEV_SEEDS[0])

        def raises(*_):
            raise ZeroDivisionError("candidate blew up")

        self.assertEqual(math.inf, family.nmse(raises, split["test"]))
        self.assertEqual(math.inf, family.nmse(lambda *_: float("nan"), split["test"]))
        self.assertEqual(math.inf, family.nmse(lambda *_: float("inf"), split["test"]))

    def test_a_split_is_deterministic_and_its_seal_survives_json(self):
        import json
        first = family.build("rational_gain", family.DEV_SEEDS[0])
        second = family.build("rational_gain", family.DEV_SEEDS[0])
        self.assertEqual(first["test_seal"], second["test_seal"])
        restored = json.loads(json.dumps(first))
        # JSON turns the row tuples into lists, so the seal is recomputed over the same digits.
        rows = [(tuple(xs), y) for xs, y in restored["test"]]
        self.assertEqual(first["test_seal"], family.seal(rows))

    def test_a_different_seed_is_a_different_sealed_split(self):
        seals = {family.build("damped_oscillator", seed)["test_seal"] for seed in family.DEV_SEEDS}
        self.assertEqual(len(family.DEV_SEEDS), len(seals))

    def test_degenerate_rows_are_refused_rather_than_divided_by_zero(self):
        with self.assertRaises(ValueError):
            family.nmse(lambda *_: 0.0, [((0.0,), 2.0), ((1.0,), 2.0)])

    def test_an_unknown_task_and_a_tiny_split_are_refused(self):
        with self.assertRaises(KeyError):
            family.build("no_such_task", family.DEV_SEEDS[0])
        with self.assertRaises(ValueError):
            family.build("rational_gain", family.DEV_SEEDS[0], test=2)


if __name__ == "__main__":
    unittest.main()
