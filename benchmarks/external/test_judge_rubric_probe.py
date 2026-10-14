"""The V1-54 (#167) judge-rubric probe must be importable without calling a model."""
import importlib
import pathlib
import sys
import unittest
import unittest.mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import judge_rubric_probe as probe  # noqa: E402


class ImportingTheProbeCostsNothing(unittest.TestCase):
    def test_the_module_body_makes_no_model_call(self):
        # The loops used to be module level, so importing the probe made four real model calls. That is a
        # bill rather than a run, and it makes the module untestable, because any test that imports it
        # pays for it. Reload re-executes the body: if the guard regresses, subprocess.run is reached and
        # this fails instead of spending.
        with unittest.mock.patch("subprocess.run",
                                 side_effect=AssertionError("the probe ran on import")) as run:
            importlib.reload(probe)
        self.assertFalse(run.called)

    def test_the_entry_point_is_still_there_to_be_run(self):
        # The other half: a guard that hid the work rather than deferring it would also pass the test
        # above.
        self.assertTrue(callable(probe.main))

    def test_the_two_rubrics_it_compares_are_distinct(self):
        # The probe exists to separate the shipped rubric from one that names generalisation. If the two
        # criteria lists ever coincide it measures one rubric twice and reports a gap of zero.
        self.assertNotEqual(probe.DEFAULT_CRITERIA, probe.GENERALISATION_CRITERIA)


if __name__ == "__main__":
    unittest.main()
