"""The headroom probe must discriminate between tasks, and must not read the test split.

"A search found something" is not a demonstration of headroom unless the search is weak and the
reported number was not used to steer it. Both are asserted here rather than described.
"""
import unittest

import symbolic_regression_family as family
import symbolic_regression_headroom as headroom

BUDGET = 4000


class HeadroomProbeTests(unittest.TestCase):
    def test_selection_cannot_see_the_test_split(self):
        """Tampering with the held-out rows must not change what the search picks.

        An earlier version of this test mutated a split it built itself, while `search` called
        `family.build` again and never saw the mutation - so it would have passed even if selection
        read the held-out rows. The tampered split is now injected into the search, which is the
        only arrangement in which the assertion means anything.
        """
        _, best = headroom.search("rational_gain", family.DEV_SEEDS[0], BUDGET)
        self.assertIsNotNone(best)

        tampered = family.build("rational_gain", family.DEV_SEEDS[0])
        tampered["test"] = [(xs, y + 100.0) for xs, y in tampered["test"]]
        genuine = headroom.family.build
        try:
            headroom.family.build = lambda *args, **kwargs: tampered
            _, again = headroom.search("rational_gain", family.DEV_SEEDS[0], BUDGET)
        finally:
            headroom.family.build = genuine
        self.assertEqual(best[1:], again[1:], "the chosen candidate moved when only test rows changed")

    def test_the_isolation_test_would_notice_a_searcher_that_peeked(self):
        """A positive control for the test above: a selection that reads test rows must be caught.

        Without this, the injection could be wired up wrongly - patching the wrong name, say - and
        the isolation assertion would be vacuous again in a way that still passes.
        """
        tampered = family.build("damped_oscillator", family.DEV_SEEDS[0])
        tampered["test"] = [(xs, y + 100.0) for xs, y in tampered["test"]]
        genuine = headroom.family.build
        seen = []
        try:
            headroom.family.build = lambda *args, **kwargs: tampered
            # Stand in for a peeking searcher: score on the rows the real one never touches.
            split = headroom.family.build("damped_oscillator", family.DEV_SEEDS[0])
            seen.append(family.nmse(lambda *_: 0.0, split["test"]))
        finally:
            headroom.family.build = genuine
        clean = family.nmse(lambda *_: 0.0, family.build("damped_oscillator", family.DEV_SEEDS[0])["test"])
        self.assertNotAlmostEqual(seen[0], clean, msg="the injected split did not reach the caller")

    def test_the_floor_is_weak_on_a_task_its_basis_cannot_express(self):
        # two_variable_ratio needs a product of two variables; the basis has only single-variable
        # functions, so the floor is stuck at a constant. A probe that reported progress here would
        # be measuring its own noise.
        split, best = headroom.search("two_variable_ratio", family.DEV_SEEDS[0], BUDGET)
        found = family.score(headroom._predict(best[1], best[2]), split)
        self.assertGreater(found["test_nmse"], 0.9, "a constant cannot beat the mean by much")

    def test_the_floor_nearly_solves_a_task_its_basis_covers(self):
        split, best = headroom.search("rational_gain", family.DEV_SEEDS[0], BUDGET)
        found = family.score(headroom._predict(best[1], best[2]), split)
        self.assertLess(found["test_nmse"], 0.5, "sin and 1/(1+x^2) span most of (3x+1)/(x^2+2)")

    def test_the_three_tasks_are_not_equally_far_from_the_floor(self):
        # The ordering is the useful output: a task the floor already solves cannot support a claim
        # about a better searcher, and one it cannot touch at all may be measuring a floor instead.
        scores = {}
        for task in family.TASKS:
            split, best = headroom.search(task, family.DEV_SEEDS[0], BUDGET)
            scores[task] = family.score(headroom._predict(best[1], best[2]), split)["test_nmse"]
        self.assertLess(scores["rational_gain"], scores["damped_oscillator"])
        self.assertLess(scores["damped_oscillator"], scores["two_variable_ratio"])

    def test_more_budget_never_selects_a_worse_candidate(self):
        # The stream is seeded, so a larger budget enumerates a superset prefix and the minimum
        # cannot rise. A probe whose answer wandered with the budget would not be a floor.
        _, small = headroom.search("damped_oscillator", family.DEV_SEEDS[1], BUDGET)
        _, large = headroom.search("damped_oscillator", family.DEV_SEEDS[1], BUDGET * 3)
        self.assertLessEqual(large[0], small[0])

    def test_the_default_split_keeps_the_floors_noise_under_its_headroom(self):
        """The ratchet on why `build` holds out 400 rows rather than 40.

        At 40 the floor's test NMSE on damped_oscillator swung 1.133 across eight seeds, against
        about 0.3 of headroom - so a comparison on it would have been reading the draw. Asserting
        the relation, not the number: the spread at the default size must stay below the gap the
        task has, or the task cannot carry a claim and this test says so before anyone runs one.
        """
        found = []
        for index in range(8):
            seed = family.DEV_SEEDS[0] + index
            split, best = headroom.search("damped_oscillator", seed, BUDGET)
            found.append(family.score(headroom._predict(best[1], best[2]), split)["test_nmse"])
        spread = max(found) - min(found)
        headroom_gap = 1.0 - sum(found) / len(found)  # against the mean predictor, which scores 1.0
        self.assertGreater(headroom_gap, 0.0, "the floor must beat the mean predictor at all")
        self.assertLess(spread, headroom_gap,
                        "seed spread %.3f is not under the headroom %.3f; a smaller held-out split "
                        "is the usual cause" % (spread, headroom_gap))

    def test_a_rendered_expression_names_the_task_variables(self):
        split, best = headroom.search("two_variable_ratio", family.DEV_SEEDS[0], BUDGET)
        rendered = headroom._render(best[1], best[2], split["variables"])
        self.assertNotIn("v", rendered, "the basis placeholder should be replaced by a variable name")


if __name__ == "__main__":
    unittest.main()
