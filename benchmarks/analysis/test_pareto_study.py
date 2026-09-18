import copy
import gzip
import hashlib
import json
from pathlib import Path
import unittest
from pareto_study import volume, validate, summarize


class ParetoStudyTests(unittest.TestCase):
    def test_analytic_union(self):
        self.assertAlmostEqual(volume([[.4, .8], [1, .4]]), .58)
        self.assertAlmostEqual(volume([[1, 1, 1], [.5, 1, 1]]), .1875)
        self.assertEqual(volume([]), 0)
        self.assertEqual(volume([[0, 0], [0, 0], [1, 1]]), 1)

    def test_failed_campaign_is_not_summarized(self):
        with self.assertRaises(ValueError):
            validate({'Protocol': 'pareto-development-v1', 'Passed': False})

    def test_retained_current_evidence_recomputes_exactly(self):
        directory = Path(__file__).resolve().parents[1] / 'evidence/pareto/us09-current-stack'
        raw = gzip.decompress((directory / 'report.json.gz').read_bytes())
        expected = summarize(json.loads(raw))
        expected['RawSha256'] = hashlib.sha256(raw).hexdigest()
        self.assertEqual(expected, json.loads((directory / 'summary.json').read_text()))

    def test_corruption_is_rejected_not_averaged_away(self):
        directory = Path(__file__).resolve().parents[1] / 'evidence/pareto/us09-current-stack'
        report = json.loads(gzip.decompress((directory / 'report.json.gz').read_bytes()))
        for field, value in [('Hypervolume', 0), ('Cost', 127), ('ReplayMatched', False), ('InitialHash', 'changed')]:
            with self.subTest(field=field):
                bad = copy.deepcopy(report)
                bad['Rows'][0][field] = value
                with self.assertRaises(ValueError):
                    validate(bad)
        bad = copy.deepcopy(report)
        bad['Rows'][0]['Samples'][0]['Violations'] = []
        with self.assertRaises(ValueError):
            validate(bad)

    def test_original_failed_campaign_remains_visible(self):
        directory = Path(__file__).resolve().parents[1] / 'evidence/pareto/us09-current-stack'
        failed = json.loads(gzip.decompress((directory / 'failed-09a3a4d.json.gz').read_bytes()))
        self.assertFalse(failed['Passed'])
        self.assertTrue(all(row['Calls'] == 0 and row['Status'] == 'failed' for row in failed['Rows']))


if __name__ == '__main__':
    unittest.main()
