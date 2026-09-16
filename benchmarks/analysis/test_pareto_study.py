import unittest
from pareto_study import volume, validate


class ParetoStudyTests(unittest.TestCase):
    def test_analytic_union(self):
        self.assertAlmostEqual(volume([[.4, .8], [1, .4]]), .58)
        self.assertAlmostEqual(volume([[1, 1, 1], [.5, 1, 1]]), .1875)
        self.assertEqual(volume([]), 0)
        self.assertEqual(volume([[0, 0], [0, 0], [1, 1]]), 1)

    def test_failed_campaign_is_not_summarized(self):
        with self.assertRaises(ValueError):
            validate({'Protocol': 'pareto-development-v1', 'Passed': False})


if __name__ == '__main__':
    unittest.main()
