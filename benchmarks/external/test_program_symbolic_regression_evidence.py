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

import hashlib
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

# The *inputs* of each test split, digested on their own. `random.Random.uniform` is platform-stable,
# so these are exact everywhere - including for the task whose targets are not. A changed range or a
# changed sampling order fails here regardless of platform.
EXPECTED_INPUT_DIGESTS = {
    ("damped_oscillator", 5000): "2128a000b0922bfeea88407d56805887f66c079e4bf9320fb26b85faf43a262d",
    ("damped_oscillator", 5001): "6c3d36e89cc4ff4248405ad1990f0409014a9e34a380a6549327bc46b4bd1622",
    ("damped_oscillator", 5002): "cbbed6accef0351642aefdfb2b02bd2e3a4e3031c46e9ccc857cecbb98a30a32",
    ("rational_gain", 5000): "f430fc127a4f31727ff1187fc5c0a7a11a6d60324fe201f702e1144e4b07f5fc",
    ("rational_gain", 5001): "e40bfe0e08367691776ebff04146db1554d6cfc140a5555be8abc8f00eef6188",
    ("rational_gain", 5002): "619011e760418e8a0e266f5efcb42b4fdcd720ee4b0888926a3897c16f356f81",
    ("two_variable_ratio", 5000): "6ae004c21ff3164154bf751da39e16e21533b52f5c1bee954b6138ba23434efc",
    ("two_variable_ratio", 5001): "55d24f66538f9d899ea0f529a1d37b12176d9552b361bdab719b8d28f759c8e5",
    ("two_variable_ratio", 5002): "943c38d5fbea97cb846ea4ccabf114b2d86cd239fb010fe3e810f63c6572969d",
}

# Three (t, y) pairs from `damped_oscillator`'s first dev split, pinned so that task's *targets* are
# guarded too. Compared at 1e-12: libm disagreement between platforms is ~1e-16, and any change to
# the closed form moves these by orders of magnitude more, so this separates the two.
EXPECTED_DAMPED_SAMPLES = (
    (0.830851758963639, 0.7761625646072241),
    (5.943486576498584, -0.10563720279217967),
    (2.393809510145355, -0.4862777551160272),
)
LIBM_TOLERANCE = 1e-12


def input_digest(rows):
    payload = [["%.17g" % value for value in xs] for xs, _ in rows]
    return hashlib.sha256(json.dumps(payload, separators=(",", ":")).encode()).hexdigest()


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

    def test_the_committed_seals_are_the_ones_this_test_pins(self):
        """Corruption check, and it runs everywhere: the bundle must carry the pinned seals.

        This is the half that is platform-independent, so it is asserted rather than skipped. A
        required gate that skips is a failure in this repository (`eng/run_required_tests.py`), and
        rightly: the first version of this skipped the whole provenance check wherever one seal
        differed, which would also have swallowed a corrupted bundle.
        """
        for row in self.rows:
            key = (row["task"], row["seed"])
            with self.subTest(task=row["task"], seed=row["seed"]):
                self.assertIn(key, EXPECTED_SEALS, "the bundle carries a row this test does not pin")
                self.assertEqual(EXPECTED_SEALS[key], row["test_seal"],
                                 "the committed seal is not the one this test pins: the bundle changed")
        self.assertEqual(len(EXPECTED_SEALS), len(self.rows), "every pinned seal must appear in the bundle")

    def test_the_rational_tasks_rebuild_their_seals_on_every_platform(self):
        """Two of the three tasks are pure rational arithmetic, so their seals are IEEE-exact.

        `rational_gain` and `two_variable_ratio` are polynomial ratios: every value is reproducible
        bit for bit anywhere, and a seal that stopped matching would mean the sampling, the ranges or
        the formula changed. Asserted unconditionally, which is what makes the carve-out below narrow
        rather than a blanket excuse.
        """
        for task in ("rational_gain", "two_variable_ratio"):
            for seed in family.DEV_SEEDS:
                with self.subTest(task=task, seed=seed):
                    self.assertEqual(EXPECTED_SEALS[(task, seed)], family.build(task, seed)["test_seal"])

    def test_any_seal_that_cannot_be_rebuilt_belongs_to_the_one_transcendental_task(self):
        """The defect, asserted as the invariant it actually is rather than skipped.

        `seal` keeps 17 significant digits and `damped_oscillator` is the only task whose closed form
        goes through libm - `exp(-0.3 t) sin(2 t)` - whose last bits differ between platforms. So on
        the machine that produced the bundle every seal rebuilds, and on CI's Linux exactly the three
        `damped_oscillator` rows do not. Either way the mismatch set must be a subset of that one
        task; a mismatch anywhere else is a real change to the family.

        Correcting the seal would move the pilot campaign's frozen `TestPartitionHashes`, which a
        paid pre-registration already committed to, so the precision stays and this is the guard.
        Tracked on #113.
        """
        transcendental = {task for task, specification in family.TASKS.items()
                          if any(name in specification["description"] for name in ("exp", "sin", "cos", "log"))}
        self.assertEqual({"damped_oscillator"}, transcendental,
                         "the carve-out below is only valid while one task uses libm")
        mismatched = {key for key in EXPECTED_SEALS
                      if family.build(key[0], key[1])["test_seal"] != EXPECTED_SEALS[key]}
        self.assertTrue({task for task, _ in mismatched} <= transcendental,
                        "a seal outside %s stopped rebuilding, which is a change to the family: %s"
                        % (sorted(transcendental), sorted(mismatched)))

    def test_the_transcendental_tasks_data_is_pinned_even_where_its_seal_cannot_be(self):
        """The carve-out above permits *any* `damped_oscillator` mismatch, which is too much.

        A changed coefficient or range would move those seals and still satisfy it, leaving that
        task's provenance unguarded (review caught it). So the task's data is pinned in two ways
        that do not depend on libm: the digest of its sampled **inputs**, which come from
        `random.Random.uniform` and are exact on every platform, and three of its (t, y) pairs
        compared at 1e-12 - four orders of magnitude looser than the ~1e-16 platform disagreement
        and many orders tighter than any change to `exp(-0.3 t) sin(2 t)` would produce.
        """
        for seed in family.DEV_SEEDS:
            with self.subTest(seed=seed):
                rows = family.build("damped_oscillator", seed)["test"]
                self.assertEqual(EXPECTED_INPUT_DIGESTS[("damped_oscillator", seed)], input_digest(rows),
                                 "the sampled inputs changed, so the range or the sampling did")
        rows = family.build("damped_oscillator", family.DEV_SEEDS[0])["test"]
        for index, (expected_t, expected_y) in enumerate(EXPECTED_DAMPED_SAMPLES):
            with self.subTest(row=index):
                (actual_t,), actual_y = rows[index]
                self.assertEqual(expected_t, round(actual_t, 15), "the sampled input moved")
                self.assertAlmostEqual(expected_y, actual_y, delta=LIBM_TOLERANCE,
                                       msg="the closed form moved by more than libm ever disagrees")

    def test_every_tasks_inputs_are_pinned_on_every_platform(self):
        for key, digest in EXPECTED_INPUT_DIGESTS.items():
            with self.subTest(task=key[0], seed=key[1]):
                self.assertEqual(digest, input_digest(family.build(key[0], key[1])["test"]))

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
