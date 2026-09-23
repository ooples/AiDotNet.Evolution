import collections
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import campaign_runner as cr

sys.path.insert(0, str(Path(__file__).parents[1] / "analysis"))
import campaign_registration  # noqa: E402

SPEC = dict(Campaign="runner-unit", Budget=dict(ModelTokens=1000, ModelCalls=4, WallSeconds=60), Models=["haiku"],
            SeedCount=4, BootstrapSamples=1000, BootstrapSeed=3, FamilywiseAlpha=0.05,
            Families=[dict(Family="algotune", Configs=["recommended", "matched"],
                           Endpoint=dict(Metric="speedup", Direction="maximize", FailureValue=0.0))],
            TestPartitionHashes={"algotune": "c" * 64}, Published=None)

CHILD = r'''
import json, os, sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import campaign_runner as cr
kill_on = sys.argv[4]
def execute(cell, directory, resume):
    with open(Path(sys.argv[3]) / "calls.jsonl", "a") as log:
        log.write(json.dumps([cell["Cell"], resume]) + "\n")
    if cell["Cell"] == kill_on and not resume:
        os._exit(9)  # killed mid-cell, after "started" was made durable
    return {"Status": "completed", "Value": 2.0 if cell["Arm"] == "aidotnet" else 1.0 + cell["Position"] * 0.01,
            "ThrottleSeconds": 0.5, "ModelCalls": 4, "ModelTokens": 800}
cr.run(sys.argv[2], sys.argv[3], execute, confirm=sys.argv[5], log=lambda _: None)
'''


class WilliamsTests(unittest.TestCase):
    def test_positions_and_first_order_carryover_are_balanced(self):
        for k in range(2, 6):
            rows = cr.williams(k)
            self.assertEqual(k if k % 2 == 0 else 2 * k, len(rows))
            for row in rows:
                self.assertEqual(sorted(row), list(range(k)))
            for position in range(k):
                counts = collections.Counter(row[position] for row in rows)
                self.assertEqual({len(rows) // k}, set(counts.values()), (k, position))
            pairs = collections.Counter((row[i], row[i + 1]) for row in rows for i in range(k - 1))
            self.assertEqual(k * (k - 1), len(pairs), k)
            self.assertEqual(1, len(set(pairs.values())), k)


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        self.registration = campaign_registration.register(SPEC, self.root / "reg")
        self.path = self.root / "reg" / "registration.json"

    def test_every_registered_cell_is_scheduled_once_with_its_position(self):
        cells = cr.schedule(self.registration)
        self.assertEqual(3 * 4, len({c["Cell"] for c in cells}))
        by_position = collections.Counter((c["Arm"], c["Position"]) for c in cells)
        self.assertEqual(3 * 3, len(by_position), "every arm appears in every position")

    def test_launch_is_refused_without_the_printed_projection_digest(self):
        with self.assertRaisesRegex(ValueError, "Launch refused"):
            cr.run(self.path, self.root / "state", lambda *a: {}, confirm="yes", log=lambda _: None)
        projection = cr.project(self.registration, mean_tokens_per_call=150)
        self.assertEqual((12, 48, 12000, 7200), (projection["Cells"], projection["MaxModelCalls"],
                                                 projection["MaxModelTokens"], projection["ExpectedModelTokens"]))
        self.assertAlmostEqual(0.2, cr.projection_error(projection, 6000))

    def test_a_killed_campaign_resumes_the_in_flight_cell_and_finishes_each_cell_once(self):
        state = self.root / "state"
        state.mkdir()
        confirm = cr.project(self.registration)["ProjectionSha256"]
        kill_on = cr.schedule(self.registration)[5]["Cell"]
        argv = [sys.executable, "-c", CHILD, str(Path(__file__).parent), str(self.path), str(state), kill_on, confirm]
        self.assertEqual(9, subprocess.run(argv, timeout=60).returncode)
        (state / "cells.jsonl").open("ab").write(b'{"Event": "fini')  # a torn final write
        self.assertEqual(0, subprocess.run(argv, timeout=60).returncode)
        calls = [json.loads(line) for line in (state / "calls.jsonl").read_text().splitlines()]
        self.assertEqual([[kill_on, False], [kill_on, True]], [c for c in calls if c[0] == kill_on])
        self.assertEqual(12 + 1, len(calls), "only the in-flight cell ran twice")
        rows = json.loads((state / "raw.json").read_text())
        self.assertEqual(12, len(rows))
        self.assertEqual([True], [r["Resumed"] for r in rows if r["Resumed"]])
        self.assertTrue(all(r["ThrottleSeconds"] == 0.5 for r in rows))
        report = campaign_registration.analyze(campaign_registration.load_registration(self.path), rows)
        self.assertTrue(report["OpenEvolveGate"])


if __name__ == "__main__":
    unittest.main()