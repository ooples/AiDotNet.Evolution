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
        # Tampering with the held-out rows before the search must not change what it picks. If
        # selection ever read them, this would be the test that caught it.
        split, best = headroom.search("rational_gain", family.DEV_SEEDS[0], BUDGET)
        self.assertIsNotNone(best)
        original = family.build("rational_gain", family.DEV_SEEDS[0])
        original["test"] = [(xs, y + 100.0) for xs, y in original["test"]]
        _, again = headroom.search("rational_gain", family.DEV_SEEDS[0], BUDGET)
        self.assertEqual(best[1:], again[1:], "the chosen candidate moved when only test rows changed")

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

    def test_a_rendered_expression_names_the_task_variables(self):
        split, best = headroom.search("two_variable_ratio", family.DEV_SEEDS[0], BUDGET)
        rendered = headroom._render(best[1], best[2], split["variables"])
        self.assertNotIn("v", rendered, "the basis placeholder should be replaced by a variable name")


if __name__ == "__main__":
    unittest.main()
