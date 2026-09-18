import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from warm_budget import CampaignBudget, container_observations, model_observations, requirements, validate_limits


class CampaignBudgetTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "journal.jsonl"
        self.budget = CampaignBudget(self.path, dict(model_calls=1,search_containers=1,confirmation_containers=2), "plan")

    def test_reservation_is_persisted_before_work_and_settlement_once(self):
        def work():
            events = [json.loads(line) for line in self.path.read_text().splitlines()]
            self.assertEqual("reserve", events[-1]["event"])
            self.assertEqual(1, self.budget.snapshot()["pending"]["model_calls"])
            return dict(cost_units=17,cost_metric="reported_input_plus_output_tokens")
        self.budget.run("model", work, resource="model_calls", measure=model_observations)
        snapshot = self.budget.snapshot()
        self.assertEqual(1,snapshot["spent"]["model_calls"])
        self.assertEqual(0,snapshot["pending"]["model_calls"])
        self.assertEqual(17,snapshot["rows"][0]["observations"]["reported_tokens"])
        self.assertEqual(3,len(self.path.read_text().splitlines()))

    def test_denial_never_invokes_work(self):
        self.budget.run("model", lambda: None, resource="model_calls")
        with patch("builtins.print") as work:
            with self.assertRaisesRegex(ValueError,"refused"):
                self.budget.run("model", work, resource="model_calls")
            work.assert_not_called()
        self.assertEqual(1,self.budget.snapshot()["denied"])

    def test_search_cannot_spend_confirmation_reserve(self):
        self.budget.run("search", lambda: None, resource="search_containers")
        with self.assertRaises(ValueError):
            self.budget.run("search", lambda: None, resource="search_containers")
        self.budget.run("audit", lambda: None, resource="confirmation_containers")
        self.assertEqual(1,self.budget.snapshot()["spent"]["confirmation_containers"])

    def test_unknown_work_is_charged_and_closes_every_stage(self):
        def fail():
            raise TimeoutError("producer timeout")
        with self.assertRaises(TimeoutError):
            self.budget.run("search", fail, resource="search_containers")
        self.assertEqual(1,self.budget.snapshot()["spent"]["search_containers"])
        self.assertEqual("unknown",self.budget.snapshot()["rows"][0]["status"])
        with self.assertRaises(ValueError):
            self.budget.run("setup",lambda: None)

    def test_invalid_usage_cannot_return_usable_result(self):
        with self.assertRaises(ValueError):
            self.budget.run("model",lambda: {"cost_units":True},resource="model_calls",measure=model_observations)
        self.assertTrue(self.budget.snapshot()["closed"])

    def test_tokens_are_observed_not_claimed_as_enforceable_cap(self):
        self.budget.run("model",lambda: dict(cost_units=10**9,cost_metric="reported_input_plus_output_tokens"),
                        resource="model_calls",measure=model_observations)
        self.assertEqual(10**9,self.budget.snapshot()["rows"][0]["observations"]["reported_tokens"])

    def test_concurrent_admission_accounts_inflight(self):
        entered, release = threading.Event(), threading.Event()
        def work():
            entered.set()
            if not release.wait(5):
                raise TimeoutError()
        worker = threading.Thread(target=lambda: self.budget.run("model",work,resource="model_calls"))
        worker.start()
        try:
            self.assertTrue(entered.wait(5))
            with self.assertRaises(ValueError):
                self.budget.run("model",lambda: self.fail("dispatched"),resource="model_calls")
        finally:
            release.set()
            worker.join(5)
        self.assertFalse(worker.is_alive())
        self.assertEqual(1,self.budget.snapshot()["spent"]["model_calls"])

    def test_existing_journal_cannot_be_replayed(self):
        with self.assertRaises(FileExistsError):
            CampaignBudget(self.path,self.budget.limits,"plan")

    def test_reservation_write_failure_prevents_dispatch(self):
        with patch.object(self.budget,"_append",side_effect=OSError("disk full")), patch("builtins.print") as work:
            with self.assertRaises(OSError):
                self.budget.run("setup",work)
            work.assert_not_called()

    def test_counts_include_repeated_diagnostics_and_both_audits(self):
        rows = [dict(tracks=[["controlled","one-shot"],["controlled","aidotnet"]])]
        self.assertEqual(dict(model_calls=5,search_containers=30,confirmation_containers=28),requirements(rows,4,3))

    def test_limits_reject_boolean_negative_missing_and_unknown_resources(self):
        for limits in ({}, {**self.budget.limits,"model_calls":True}, {**self.budget.limits,"model_calls":-1},
                       {**self.budget.limits,"tokens":10}):
            with self.assertRaises(ValueError):
                validate_limits(limits)

    def test_missing_kernel_measurement_is_not_zero(self):
        with self.assertRaises(ValueError):
            container_observations(dict(unknown_work=True,resource_status="unknown"))

    def test_measurement_nan_and_negative_are_rejected(self):
        for value in (float("nan"), -1):
            with tempfile.TemporaryDirectory() as root:
                budget = CampaignBudget(Path(root)/"j",self.budget.limits,"plan")
                with self.assertRaises(ValueError):
                    budget.run("setup",lambda:None,measure=lambda _:dict(cpu=value))
                self.assertTrue(budget.snapshot()["closed"])

    def test_snapshot_cannot_mutate_ledger(self):
        snapshot = self.budget.snapshot()
        snapshot["limits"]["model_calls"] = 100
        self.assertEqual(1,self.budget.limits["model_calls"])


if __name__ == "__main__":
    unittest.main()
