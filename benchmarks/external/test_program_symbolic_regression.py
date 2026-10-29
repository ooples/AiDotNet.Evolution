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
    def test_nmse_and_error_is_nmse_with_the_standard_error_of_its_mean(self):
        # E-4: the error is the squared errors' sample standard deviation over sqrt(n), over the targets' variance.
        rows = [((0.0,), 0.0), ((1.0,), 1.0), ((2.0,), 4.0), ((3.0,), 2.0)]
        predict = lambda x: x  # noqa: E731 - errors 0, 0, -2, 1
        squares = [0.0, 0.0, 4.0, 1.0]
        mean = sum(squares) / 4
        variance = sum((y - 1.75) ** 2 for _, y in rows) / 4
        expected = math.sqrt(sum((v - mean) ** 2 for v in squares) / 3) / 2 / variance
        score, error = family.nmse_and_error(predict, rows)
        self.assertEqual(family.nmse(predict, rows), score)
        self.assertAlmostEqual(expected, error, places=12)

    def test_nmse_and_error_is_infinite_wherever_nmse_is(self):
        rows = [((0.0,), 0.0), ((1.0,), 1.0), ((2.0,), 4.0), ((3.0,), 2.0)]
        for predict in (lambda x: float("nan"), lambda x: 1 / 0):
            self.assertEqual((math.inf, math.inf), family.nmse_and_error(predict, rows))

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
        # Finite but huge: math.isfinite passes, and (prediction - y) ** 2 would raise
        # OverflowError and abort the run instead of scoring this candidate worst.
        self.assertEqual(math.inf, family.nmse(lambda *_: 1e200, split["test"]))

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

    def test_the_seal_is_checked_before_the_candidate_runs(self):
        # The declared residual risk is a candidate that tampers with the scorer. It cannot undo a
        # refusal that already happened, so the refusal has to come first - asserted by a candidate
        # that records whether it was ever called.
        split = family.build("rational_gain", family.DEV_SEEDS[0])
        xs, y = split["test"][0]
        split["test"][0] = (xs, y + 1.0)
        calls = []

        def spy(*args):
            calls.append(args)
            return 0.0

        with self.assertRaises(ValueError):
            family.score(spy, split)
        self.assertEqual([], calls, "the candidate ran before the seal was checked")

    def test_a_partition_is_byte_stable_and_matches_the_gates_shape(self):
        first = family.partition({"rational_gain": [21], "damped_oscillator": [7, 9]})
        second = family.partition({"damped_oscillator": [7, 9], "rational_gain": [21]})
        self.assertEqual(first, second, "argument order must not change the sealed bytes")
        self.assertEqual(
            b'{"Family":"sr","TestSeeds":{"damped_oscillator":[7,9],"rational_gain":[21]}}', first)

    def test_a_partition_refuses_input_a_campaign_could_not_seal(self):
        for bad in ({}, {"no_such_task": [1]}, {"rational_gain": []},
                    {"rational_gain": [1, 1]}, {"rational_gain": ["1"]}):
            with self.subTest(bad=bad):
                with self.assertRaises((KeyError, ValueError)):
                    family.partition(bad)

    def test_a_dev_seed_partition_is_reproducible_from_the_tree(self):
        # Not an assertion about the gate, but about why its visibility check is not enough here:
        # these rows are a pure function of a committed seed, so anyone can rebuild the held-out
        # data without the partition bytes ever appearing in the repository.
        rebuilt = family.build("rational_gain", family.DEV_SEEDS[0])
        self.assertEqual(family.build("rational_gain", family.DEV_SEEDS[0])["test_seal"],
                         rebuilt["test_seal"])

    def test_an_unknown_task_and_a_tiny_split_are_refused(self):
        with self.assertRaises(KeyError):
            family.build("no_such_task", family.DEV_SEEDS[0])
        with self.assertRaises(ValueError):
            family.build("rational_gain", family.DEV_SEEDS[0], test=2)


if __name__ == "__main__":
    unittest.main()
