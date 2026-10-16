"""V1-04d (#113): the committed headroom bundle, checked against the family it claims to come from.

`benchmarks/evidence/symbolic-regression/e083c3b/headroom.json` is what the story's eligibility
reading rests on, and nothing in the repository was checking it. The enumeration behind it is
deterministic, so the whole bundle reproduces exactly - verified at `main` `0354e33`:

    python benchmarks/external/symbolic_regression_headroom.py --budget 20000 --noise-seeds 8 --output fresh.json

produces a file byte-identical to the committed one. That takes ~56 s, which is too slow for this
suite, so what is asserted here are the parts that can go wrong silently and cost nothing to check:
the **seals** (the data the numbers were measured on), the **aggregates** (what the README quotes),
and the **task set** (what the bundle claims to cover). A changed grammar or budget still changes the
rows, and the documented command above is what catches that.
"""
from __future__ import annotations

import json
import unittest
from pathlib import Path

import symbolic_regression_family as family

EVIDENCE = Path(__file__).resolve().parents[1] / "evidence" / "symbolic-regression" / "e083c3b" / "headroom.json"
BUDGET = 20000


class CommittedHeadroomEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bundle = json.loads(EVIDENCE.read_text(encoding="utf-8"))
        cls.rows = cls.bundle["rows"]

    def test_every_sealed_test_split_is_the_one_the_family_builds_today(self):
        # The seal is the provenance of every number in the bundle: if the family's sampling, ranges
        # or closed forms change, these stop matching and the committed NMSEs describe other data.
        for row in self.rows:
            with self.subTest(task=row["task"], seed=row["seed"]):
                split = family.build(row["task"], row["seed"])
                self.assertEqual(row["test_seal"], split["test_seal"])
                self.assertEqual(split["test_seal"], family.seal(split["test"]),
                                 "the split must carry the seal of its own rows")

    def test_the_bundle_covers_every_task_on_every_declared_dev_seed(self):
        self.assertEqual(family.FAMILY, self.bundle["family"])
        self.assertEqual(sorted(family.TASKS), sorted({row["task"] for row in self.rows}))
        for task in family.TASKS:
            with self.subTest(task=task):
                self.assertEqual(sorted(family.DEV_SEEDS),
                                 sorted(row["seed"] for row in self.rows if row["task"] == task))
        for row in self.rows:
            self.assertEqual(BUDGET, row["budget"], "the bundle is the 20,000-candidate run")

    def test_the_quoted_aggregates_are_the_ones_the_rows_produce(self):
        # The README quotes a min/median/max per task; an edited summary beside intact rows is the
        # failure this catches.
        for task, noise in self.bundle["noise"].items():
            with self.subTest(task=task):
                values = noise["values"]
                self.assertEqual(noise["seeds"], len(values))
                self.assertAlmostEqual(min(values), noise["min"], places=12)
                self.assertAlmostEqual(max(values), noise["max"], places=12)
                # The harness's median is the upper middle order statistic, `sorted(v)[len(v)//2]`,
                # not an averaged median - with eight seeds the two differ in the fourth decimal, and
                # asserting the wrong one here would have been a test that fails on correct evidence.
                self.assertAlmostEqual(sorted(values)[len(values) // 2], noise["median"], places=12)
                self.assertAlmostEqual(max(values) - min(values), noise["spread"], places=12)

    def test_the_gap_closed_column_is_derived_from_the_two_nmse_columns(self):
        for row in self.rows:
            with self.subTest(task=row["task"], seed=row["seed"]):
                derived = 1 - row["found_test_nmse"] / row["baseline_test_nmse"]
                self.assertAlmostEqual(derived, row["gap_closed"], places=12)

    def test_the_damped_oscillator_is_the_task_with_room_left_to_compete_on(self):
        # The eligibility reading the story needs: an effect larger than seed noise on one task,
        # a floor that already solves another, and one the floor cannot touch. If that ordering
        # changes, the claim about which task can carry a comparison changes with it.
        by_task = {task: [row["gap_closed"] for row in self.rows if row["task"] == task]
                   for task in family.TASKS}
        spread = {task: self.bundle["noise"][task]["spread"] for task in family.TASKS}
        self.assertGreater(min(by_task["damped_oscillator"]) / spread["damped_oscillator"], 2.0,
                           "the effect must be at least twice the seed noise to be a result")
        self.assertGreater(min(by_task["rational_gain"]), 0.85, "the floor already solves this one")
        self.assertLess(max(by_task["two_variable_ratio"]), 0.10, "and cannot touch this one")

    def test_a_tampered_test_split_is_refused_before_a_candidate_runs(self):
        split = family.build("damped_oscillator", family.DEV_SEEDS[0])
        split["test"] = split["test"][:-1]
        with self.assertRaisesRegex(ValueError, "does not match its seal"):
            family.score(lambda *_: (_ for _ in ()).throw(AssertionError("the candidate must not run")), split)


if __name__ == "__main__":
    unittest.main()
