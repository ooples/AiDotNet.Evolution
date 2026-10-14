"""Contracts for the V1-54 (#167) feedback ablation driver. No model calls and no CLI."""
import json
import pathlib
import subprocess
import sys
import types
import unittest
import unittest.mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
sys.path.insert(0, str(pathlib.Path(__file__).parent / "feedback_ablation_task"))

import evaluator  # noqa: E402
import feedback_ablation as fa  # noqa: E402
import score_heldout  # noqa: E402

SEED_PROGRAM = (pathlib.Path(__file__).parent / "feedback_ablation_task" / "initial_program.py").read_text(
    encoding="utf-8")
GAMING = (
    "TABLE = {%s}\n"
    "def solve(x):\n"
    "    return TABLE.get(x, 0)\n" % ", ".join("%d: %d" % pair for pair in evaluator.VISIBLE))
HONEST = "def solve(x):\n    return ((x * x) ^ (x + 11)) % 89\n"


class EndpointSeparatesGamingFromGeneralising(unittest.TestCase):
    def test_the_visible_score_cannot_tell_them_apart(self):
        # The premise of the whole design: on what the search can see, memorising is indistinguishable
        # from solving. If this ever stops being true the ablation is measuring something else.
        self.assertEqual(1.0, evaluator.evaluate(GAMING)["quality"])
        self.assertEqual(1.0, evaluator.evaluate(HONEST)["quality"])

    def test_the_endpoint_does(self):
        # Bounded rather than asserted as exactly zero: the rule is taken modulo a prime, so a wrong
        # candidate can coincide with it by accident, and the seed does exactly that on 1 of the 55
        # held-out points. A bound that survives a coincidence is worth more than an equality that
        # happens to hold for this rule and would break on the next one.
        self.assertLess(score_heldout.score(GAMING)["heldout_accuracy"], 0.05)
        self.assertEqual(1.0, score_heldout.score(HONEST)["heldout_accuracy"])

    def test_the_seed_leaves_headroom_without_scoring_zero(self):
        # A seed at zero cannot distinguish a working run from a broken one; a seed near the top leaves
        # nothing for either arm to find.
        quality = evaluator.evaluate(SEED_PROGRAM)["quality"]
        self.assertGreater(quality, 0.0)
        self.assertLess(quality, 0.5)

    def test_the_held_out_inputs_are_disjoint_from_the_visible_ones(self):
        visible = {x for x, _ in evaluator.VISIBLE}
        self.assertEqual(set(), visible & set(score_heldout.HELD_OUT))
        self.assertTrue(any(x < min(visible) for x in score_heldout.HELD_OUT))
        self.assertTrue(any(x > max(visible) for x in score_heldout.HELD_OUT))

    def test_a_candidate_that_will_not_compile_scores_zero_rather_than_crashing_the_driver(self):
        self.assertEqual(0.0, score_heldout.score("def solve(x:\n")["heldout_accuracy"])
        self.assertEqual(0.0, score_heldout.score("x = 1\n")["heldout_accuracy"])


class TheArmsDifferInExactlyOneKey(unittest.TestCase):
    def test_only_llmFeedback_and_the_run_id_change(self):
        # Asserted structurally rather than by reading the builder: an arm that also changed, say, the
        # budget or the task description would confound the comparison while still looking paired.
        common = dict(evaluations=6, model="sonnet", executable="claude", budget_usd=None, time_limit=60)
        off = fa.run_file("out-off", 3, None, **common)
        on = fa.run_file("out-on", 3, 0.3, **common)

        off.pop("output"), on.pop("output")
        self.assertEqual("feedback-ablation-s3-off", off.pop("runId"))
        self.assertEqual("feedback-ablation-s3-on", on.pop("runId"))
        self.assertEqual({"weight": 0.3}, on.pop("llmFeedback"))
        self.assertNotIn("llmFeedback", off)
        self.assertEqual(off, on)

    def test_both_arms_see_the_evaluator_artifacts(self):
        # The proposer needs the visible pairs to induce anything; a dry run without them left every
        # proposal at the seed's own score in both arms, which would have made the ablation measure noise.
        for weight in (None, 0.3):
            run = fa.run_file("o", 0, weight, 6, "sonnet", "claude", None, 60)
            self.assertTrue(run["prompt"]["includeArtifacts"])
            # Without this the prompt carries "[artifact content withheld]" and includeArtifacts is inert.
            self.assertTrue(run["search"]["retainArtifactText"])

    def test_a_rubric_arm_sets_the_judges_criteria_and_an_unnamed_run_keeps_the_shipped_rubric(self):
        # The rubric was measured as the lever that matters most, so it has to be variable per arm - and the
        # default must stay exactly what it was before the option existed, or every earlier row becomes
        # incomparable.
        common = dict(evaluations=6, model="sonnet", executable="claude", budget_usd=None, time_limit=60)
        plain = fa.run_file("o", 0, 0.3, arm="on-w0.3", criteria=None, **common)
        self.assertNotIn("criteria", plain["llmFeedback"])

        named = fa.run_file("o", 0, 0.3, arm="on-w0.3-general",
                            criteria=["Correctness", "Generality"], **common)
        self.assertEqual(["Correctness", "Generality"], named["llmFeedback"]["criteria"])

        # The control arm has no llmFeedback at all, whatever the rubric arms do.
        self.assertNotIn("llmFeedback", fa.run_file("o", 0, None, arm="off", criteria=["X"], **common))

    def test_the_blend_is_what_an_arm_must_vary_to_give_the_judge_leverage(self):
        # weight scales the judge's utility; combinedBlend is the mixing share and the only one that can let
        # a judged score change an outcome. An arm that varies weight alone measures a mechanism with no
        # leverage, so the driver has to be able to set this.
        common = dict(evaluations=6, model="sonnet", executable="claude", budget_usd=None, time_limit=60)
        default = fa.run_file("o", 0, 0.3, arm="on-w0.3", criteria=None, blend=None, **common)
        self.assertNotIn("combinedBlend", default["llmFeedback"])

        levered = fa.run_file("o", 0, 0.3, arm="on-w0.3", criteria=None, blend=0.2, **common)
        self.assertEqual(0.2, levered["llmFeedback"]["combinedBlend"])

        # And never on the control arm, which has no llmFeedback at all.
        self.assertNotIn("llmFeedback", fa.run_file("o", 0, None, arm="off", criteria=None, blend=0.2, **common))

    def test_the_engine_seed_is_the_pairing_key(self):
        common = dict(evaluations=6, model="sonnet", executable="claude", budget_usd=None, time_limit=60)
        self.assertEqual(11, fa.run_file("o", 11, None, **common)["budget"]["seed"])
        self.assertEqual(11, fa.run_file("o", 11, 0.3, **common)["budget"]["seed"])

    def test_the_run_calls_a_real_model_rather_than_a_stub(self):
        # A stub answers both arms identically, so a run against one measures the stub. There is no
        # option to supply one, and this asserts the provider the driver actually writes.
        run = fa.run_file("o", 0, 0.3, 6, "sonnet", "claude", None, 60)
        self.assertEqual("ClaudeCode", run["model"]["provider"])
        self.assertNotIn("endpoint", run["model"])


class ReportIsAKnownAnswer(unittest.TestCase):
    @staticmethod
    def rows(values):
        """values: {seed: (off_endpoint, on_endpoint, off_failure, on_failure)}"""
        out = []
        for seed, (off, on, off_failure, on_failure) in sorted(values.items()):
            for arm, accuracy, failure in (("off", off, off_failure), ("on", on, on_failure)):
                out.append({"seed": seed, "arm": arm, "heldout_accuracy": accuracy, "failure": failure,
                            "visible_quality": 1.0, "completed_evaluations": 6, "model_calls": 5,
                            "seconds": 1.0, "weight": None if arm == "off" else 0.3,
                            "endpoint_detail": None})
        return out

    def report(self, rows, tmp, arm="on"):
        arguments = types.SimpleNamespace(output=str(tmp), draws=400, bootstrap_seed=7, seeds=len(rows) // 2,
                                          evaluations=6, weights="0.3", blend=None, model="sonnet", claude="claude")
        fa.report(rows, arguments)
        document = json.loads(pathlib.Path(tmp).read_text(encoding="utf-8"))
        summary = dict(document["summary"]["arms"].get(arm, {}))
        summary["arms_that_produced_no_result"] = document["summary"]["arms_that_produced_no_result"]
        return summary

    def test_each_weight_gets_its_own_arm_and_its_own_summary(self):
        # One weight cannot answer the question: the blend is linear, so whether feedback can change which
        # candidate wins depends on whether the weight is above the point where the judge's gap overcomes the
        # visible-score gap. Measured on this task, that point is w = 0.91, far above any shipped default.
        import tempfile
        rows = []
        for seed in range(4):
            rows.append({"seed": seed, "arm": "off", "weight": None, "heldout_accuracy": 0.0,
                         "failure": None, "visible_quality": 1.0, "completed_evaluations": 5,
                         "model_calls": 4, "seconds": 1.0, "endpoint_detail": None})
            for arm, weight, endpoint in (("on-w0.3", 0.3, 0.0), ("on-w0.95", 0.95, 1.0)):
                rows.append({"seed": seed, "arm": arm, "weight": weight, "heldout_accuracy": endpoint,
                             "failure": None, "visible_quality": 1.0, "completed_evaluations": 5,
                             "model_calls": 4, "seconds": 1.0, "endpoint_detail": None})

        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "r.json"
            fa.report(rows, types.SimpleNamespace(output=str(path), draws=400, bootstrap_seed=7, seeds=4,
                                                  evaluations=5, weights="0.3,0.95", blend=None, model="sonnet",
                                                  claude="claude"))
            arms = json.loads(path.read_text(encoding="utf-8"))["summary"]["arms"]

        self.assertEqual(["on-w0.3", "on-w0.95"], sorted(arms))
        self.assertEqual(0.3, arms["on-w0.3"]["weight"])
        self.assertEqual(0.0, arms["on-w0.3"]["median_difference"])
        self.assertEqual(0, arms["on-w0.3"]["pairs_favouring_feedback"])
        self.assertEqual(1.0, arms["on-w0.95"]["median_difference"])
        self.assertEqual(4, arms["on-w0.95"]["pairs_favouring_feedback"])
        # Each weighted arm is paired against the SAME control, so the two differences are comparable.
        self.assertEqual(4, arms["on-w0.3"]["pairs"])
        self.assertEqual(4, arms["on-w0.95"]["pairs"])

    def test_a_known_split_reports_the_counts_and_a_median_it_implies(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            summary = self.report(self.rows({
                0: (0.0, 1.0, None, None), 1: (0.0, 1.0, None, None), 2: (1.0, 1.0, None, None),
                3: (1.0, 0.0, None, None), 4: (0.0, 0.0, None, None),
            }), pathlib.Path(directory) / "r.json")

        self.assertEqual(5, summary["pairs"])
        self.assertEqual(2, summary["pairs_favouring_feedback"])
        self.assertEqual(1, summary["pairs_favouring_no_feedback"])
        self.assertEqual(2, summary["tied_pairs"])
        self.assertEqual(0.0, summary["median_difference"])
        self.assertAlmostEqual(0.2, summary["mean_difference"], places=12)
        self.assertEqual(0.0, summary["off_median"])
        self.assertEqual(1.0, summary["on_median"])

    def test_a_failed_arm_keeps_its_seed_in_the_denominator(self):
        # Dropping a crashed arm would let a crash improve that arm's figure. The pair stays, with a
        # zero endpoint and the reason recorded, and the failure count is reported separately.
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "r.json"
            summary = self.report(self.rows({
                0: (1.0, 0.0, None, "exit 3: the model was unavailable"),
                1: (1.0, 1.0, None, None),
            }), path)
            document = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(2, summary["pairs"])
        self.assertEqual(1, summary["arms_that_produced_no_result"])
        self.assertEqual(1, summary["pairs_favouring_no_feedback"])
        self.assertEqual(-1.0, document["pairs"]["on"][0]["difference"])
        self.assertIn("unavailable", document["pairs"]["on"][0]["on_failure"])

    def test_an_interval_that_excludes_zero_is_only_reported_when_every_pair_agrees(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            unanimous = self.report(self.rows({seed: (0.0, 1.0, None, None) for seed in range(8)}),
                                    pathlib.Path(directory) / "a.json")
            split = self.report(self.rows({seed: (0.0, 1.0 if seed % 2 else 0.0, None, None)
                                           for seed in range(8)}), pathlib.Path(directory) / "b.json")

        self.assertEqual([1.0, 1.0], unanimous["difference_ci95"])
        self.assertLessEqual(split["difference_ci95"][0], 0.0)
        self.assertGreaterEqual(split["difference_ci95"][1], 0.0)

    def test_no_pairs_reports_nothing_rather_than_a_fabricated_interval(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "r.json"
            fa.report([], types.SimpleNamespace(output=str(path), draws=400, bootstrap_seed=7, seeds=0,
                                                evaluations=6, weights="0.3", blend=None, model="sonnet", claude="claude"))
            document = json.loads(path.read_text(encoding="utf-8"))
        # No rows means no arms at all, rather than an arm reporting a fabricated interval.
        self.assertEqual({}, document["summary"]["arms"])
        self.assertEqual(0, document["summary"]["arms_that_produced_no_result"])
class TheDriverSurvivesWhatACandidateCanDo(unittest.TestCase):
    """A candidate is code a model wrote, run on inputs the search never tried."""

    LOOPS = """
def solve(x):
    while True:
        pass
"""

    CHATTY = """
print("loading")

def solve(x):
    print("solving", x)
    return ((x * x) ^ (x + 11)) % 89
"""

    def write(self, directory, name, text):
        path = pathlib.Path(directory) / name
        path.write_text(text, encoding="utf-8")
        return path

    def test_a_candidate_that_never_finishes_scores_zero_instead_of_hanging_the_campaign(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            scored = fa.endpoint(self.write(directory, "loops.py", self.LOOPS), sys.executable, timeout=5)

        self.assertEqual(0.0, scored["heldout_accuracy"])
        self.assertIn("did not finish", scored["detail"])

    def test_a_candidate_that_prints_does_not_make_the_result_unparsable(self):
        # The scorer prints one JSON object last; a candidate printing at import time or inside solve
        # would otherwise make json.loads raise after every row had already been paid for.
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            scored = fa.endpoint(self.write(directory, "chatty.py", self.CHATTY), sys.executable, timeout=60)

        self.assertEqual(1.0, scored["heldout_accuracy"])

    def test_a_missing_best_program_is_a_zero_endpoint_not_a_crash(self):
        scored = fa.endpoint(None, sys.executable)
        self.assertEqual(0.0, scored["heldout_accuracy"])
        self.assertFalse(scored["compiled"])

    def test_an_arm_that_times_out_returns_a_failure_rather_than_ending_the_campaign(self):
        # An escaping TimeoutExpired would lose every row collected so far, which is the opposite of the
        # contract that a failed arm keeps its seed in the denominator. The timeout is injected rather
        # than provoked, because what is under test is our handling of it, not the platform.
        def raise_timeout(*_args, **kwargs):
            raise subprocess.TimeoutExpired(cmd="aidotnet-evolve", timeout=kwargs.get("timeout", 0))

        with unittest.mock.patch.object(fa.subprocess, "run", raise_timeout):
            result, elapsed, failure = fa.evolve("cli", "run.json", "results", timeout=7)

        self.assertIsNone(result)
        self.assertIn("exceeded the 7 s run timeout", failure)
        self.assertGreaterEqual(elapsed, 0)



if __name__ == "__main__":
    unittest.main()
