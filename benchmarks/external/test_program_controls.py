from unittest.mock import Mock
import unittest

from program_controls import candidate_hash, extract_program, run_control


class ProgramControlsTests(unittest.TestCase):
    def evaluate(self, code):
        return dict(candidate_hash=candidate_hash(code), status="valid", quality=float(code), work_units=1, unknown_work=False)

    def run_method(self, method, generate, evaluate=None, **limits):
        return run_control(method, "test fixture; code strings are scores, not executed", "1", generate,
                           evaluate or self.evaluate, model_calls=limits.get("model_calls", 3),
                           evaluations=limits.get("evaluations", 4), seconds=60)

    def test_one_shot_does_not_spend_unused_call_allowance(self):
        generate = Mock(return_value="```python\n2\n```")
        row = self.run_method("one-shot", generate)
        self.assertEqual("completed", row["status"])
        self.assertEqual(1, generate.call_count)
        self.assertEqual(2, len(row["evaluation_attempts"]))
        self.assertEqual(2, row["best"]["quality"])

    def test_single_parent_retains_best_after_regression(self):
        generate = Mock(side_effect=["```python\n3\n```", "```python\n2\n```", "```python\n4\n```"])
        row = self.run_method("single-parent", generate)
        self.assertEqual("completed", row["status"])
        self.assertEqual(row["model_attempts"][1]["parent_hash"], row["model_attempts"][2]["parent_hash"])
        self.assertEqual(4, row["best"]["quality"])
        self.assertEqual(4, row["observed_evaluator_work"])

    def test_invalid_response_still_consumes_model_attempt(self):
        generate = Mock(return_value="not fenced")
        row = self.run_method("single-parent", generate)
        self.assertEqual(3, generate.call_count)
        self.assertEqual(1, len(row["evaluation_attempts"]))
        self.assertTrue(all(attempt["status"] == "invalid-response" for attempt in row["model_attempts"]))

    def test_evaluation_cap_prevents_unusable_model_call(self):
        generate = Mock(return_value="```python\n2\n```")
        row = self.run_method("single-parent", generate, evaluations=2)
        self.assertEqual("evaluation-cap", row["status"])
        self.assertEqual(1, generate.call_count)

    def test_unknown_evaluation_or_model_work_closes_run(self):
        for generate, evaluate in ((Mock(side_effect=TimeoutError()), self.evaluate),
                                   (Mock(return_value="```python\n2\n```"), Mock(side_effect=TimeoutError()))):
            row = self.run_method("single-parent", generate, evaluate)
            self.assertEqual("failed", row["status"])
            self.assertTrue(row["unknown_work"])
            self.assertLessEqual(generate.call_count, 1)

    def test_incorrect_identity_or_nonfinite_quality_rejected(self):
        for changes in ({"candidate_hash": "0" * 64}, {"quality": float("nan")}, {"unknown_work": True}, {"work_units": -1}):
            def invalid(code):
                return dict(self.evaluate(code), **changes)
            row = self.run_method("single-parent", Mock(), invalid)
            self.assertEqual("failed", row["status"])
            self.assertEqual([], row["model_attempts"])

    def test_extractor_rejects_multiple_blocks_wrong_language_and_narrative(self):
        for response in ("```csharp\nx\n```", "x```python\ny\n```", "```python\na\n```\n```python\nb\n```", "```python\n\n```"):
            with self.assertRaises(ValueError):
                extract_program(response)


if __name__ == "__main__":
    unittest.main()
