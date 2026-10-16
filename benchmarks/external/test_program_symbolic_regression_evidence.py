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
# The committed seals, pinned so a corrupted bundle fails rather than reading as a platform skip.
EXPECTED_SEALS = {
    ("damped_oscillator", 5000): "59e62136063bd3a36c113ad5e04c4071fd18a2b28f5dc53b40b3b0127b51d28d",
    ("damped_oscillator", 5001): "1b1e320d697f4bde22b906820d9852f99c27d8e4cb95fcab680c43d30ddfd0a2",
    ("damped_oscillator", 5002): "f44060b7ab39f5eb38b95441792b59eba339f8fd8408b90df3d1a5b74a085947",
    ("rational_gain", 5000): "4506dfe535fdf19cd0247b3a240277d83706f028a967d44da6a2faa5b75dd93b",
    ("rational_gain", 5001): "bc9c91fba7f8eebe968bd9477765fc2bba477ad1cd79cef4a6b30b04f19b556c",
    ("rational_gain", 5002): "5f105f9f2c1197048a860fdcd8b732b06b867e9df477d9a97ca92a6c4bf31ae1",
    ("two_variable_ratio", 5000): "5552dd27434c0735882293582191bae33cb3dfb4c90e65ef24807aa34934e487",
    ("two_variable_ratio", 5001): "72a21c8056f05993a72e00074c04df1792eb1dfcb076acbec515a76860fd1b53",
    ("two_variable_ratio", 5002): "3cbaf77968ef48bff67134c472cdd4a0e19712d5329cfd50f78f1ecdad74e625",
}


class CommittedHeadroomEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bundle = json.loads(EVIDENCE.read_text(encoding="utf-8"))
        cls.rows = cls.bundle["rows"]

    def test_every_split_carries_the_seal_of_its_own_rows(self):
        # Portable half: whatever the platform computes, a split's seal must be the seal of the rows
        # it carries. This is what `family.score` checks before a candidate runs.
        for row in self.rows:
            with self.subTest(task=row["task"], seed=row["seed"]):
                split = family.build(row["task"], row["seed"])
                self.assertEqual(split["test_seal"], family.seal(split["test"]))

    def test_the_committed_seals_rebuild_on_a_platform_that_can_reproduce_them(self):
        """The provenance check, and the defect it exposed.

        If the family's sampling, ranges or closed forms change, these stop matching and the
        committed NMSEs describe other data - which is what this is for. But the seal digests each
        value to 17 significant digits (`%.16e` keeps one digit before the point and sixteen after), and the closed forms go through libm (`exp`, `sin`, `cos`),
        whose last bits are not identical across platforms. So the committed seals rebuild on the
        machine that produced them (Windows, where they were measured) and not on CI's Linux.

        That is a real limitation of the seal rather than of this test, and it is recorded on #113:
        a sealed partition that only verifies on one platform is weaker than it looks, and it also
        affects the pilot's frozen `TestPartitionHashes`. Changing `seal` would move hashes that a
        paid campaign's pre-registration already committed to, so this skips with the reason rather
        than silently passing or silently weakening.
        """
        # The committed seals are pinned in this file, so the two causes of a mismatch stay apart: a
        # bundle that no longer carries these values is corruption and fails, while a platform that
        # cannot rebuild them is the libm difference and skips. Treating both as a skip - the first
        # version of this - would have let an edited seal through on the machine that produced it.
        for row in self.rows:
            key = (row["task"], row["seed"])
            with self.subTest(task=row["task"], seed=row["seed"]):
                self.assertIn(key, EXPECTED_SEALS, "the bundle carries a row this test does not pin")
                self.assertEqual(EXPECTED_SEALS[key], row["test_seal"],
                                 "the committed seal is not the one this test pins: the bundle changed")
        self.assertEqual(len(EXPECTED_SEALS), len(self.rows), "every pinned seal must appear in the bundle")

        unrebuildable = [key for key in sorted(EXPECTED_SEALS)
                         if family.build(key[0], key[1])["test_seal"] != EXPECTED_SEALS[key]]
        if unrebuildable:
            raise unittest.SkipTest(
                "This platform rebuilds %d of %d pinned seals differently; see #113 on the "
                "17-significant-digit seal over libm results." % (len(unrebuildable), len(EXPECTED_SEALS)))

    def test_the_seal_binds_more_precision_than_libm_agrees_on(self):
        """The exposure, measured rather than described: 17 significant digits (`%.16e` keeps one digit before the point and sixteen after) is finer than libm.

        Two values a relative 1e-16 apart - the scale platform libm implementations differ at - get
        different seals. That is why the test above can only run where the bundle was produced.
        """
        rows = [((0.5,), 0.7761625646072241)]
        nudged = [((0.5,), 0.7761625646072242)]
        self.assertNotEqual(family.seal(rows), family.seal(nudged))

    def test_the_bundle_covers_every_task_on_every_declared_dev_seed(self):
        self.assertEqual(family.FAMILY, self.bundle["family"])
        self.assertEqual(sorted(family.TASKS), sorted({row["task"] for row in self.rows}))
        for task in family.TASKS:
            with self.subTest(task=task):
                self.assertEqual(sorted(family.DEV_SEEDS),
                                 sorted(row["seed"] for row in self.rows if row["task"] == task))
        for row in self.rows:
            self.assertEqual(BUDGET, row["budget"], "the bundle is the 20,000-candidate run")

    def test_the_noise_values_match_the_rows_they_share_seeds_with(self):
        """The sweep and the rows are two recordings of the same three measurements.

        The sweep scores consecutive seeds from the first dev seed, so every row's `found_test_nmse`
        must appear among that task's noise values. Comparing the summary only with the values beside
        it - the first version of this - let a consistent edit to both validate itself, and the
        eligibility test could then read an invented spread (review caught it).
        """
        for task, noise in self.bundle["noise"].items():
            values = [round(value, 12) for value in noise["values"]]
            for row in self.rows:
                if row["task"] != task:
                    continue
                with self.subTest(task=task, seed=row["seed"]):
                    self.assertIn(round(row["found_test_nmse"], 12), values,
                                  "a dev-seed measurement is missing from the noise sweep that shares its seed")

    def test_the_quoted_aggregates_are_the_ones_the_rows_produce(self):
        # The README quotes a min/median/max per task; an edited summary beside intact rows is the
        # failure this catches.
        for task, noise in self.bundle["noise"].items():
            with self.subTest(task=task):
                values = noise["values"]
                self.assertEqual(noise["seeds"], len(values))
                self.assertEqual(sorted(values), values, "the sweep records its values sorted")
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
        # The tasks are compared with each other, not against independent thresholds: 0.95 on
        # damped_oscillator and 0.87 on rational_gain would pass thresholds while reversing the
        # documented ordering, which is the reading the README and #113 rest on (review caught it).
        self.assertGreater(min(by_task["rational_gain"]), max(by_task["damped_oscillator"]),
                           "the floor must solve rational_gain more completely than damped_oscillator")
        self.assertLess(max(by_task["two_variable_ratio"]), min(by_task["damped_oscillator"]),
                        "and must touch two_variable_ratio least of the three")
        self.assertEqual(["two_variable_ratio", "damped_oscillator", "rational_gain"],
                         sorted(family.TASKS, key=lambda task: min(by_task[task])),
                         "the three tasks must stay in the order the eligibility claim is written against")

    def test_a_tampered_test_split_is_refused_before_a_candidate_runs(self):
        split = family.build("damped_oscillator", family.DEV_SEEDS[0])
        split["test"] = split["test"][:-1]
        with self.assertRaisesRegex(ValueError, "does not match its seal"):
            family.score(lambda *_: (_ for _ in ()).throw(AssertionError("the candidate must not run")), split)


if __name__ == "__main__":
    unittest.main()
