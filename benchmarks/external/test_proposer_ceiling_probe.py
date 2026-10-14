"""The expression guard on the V1-54 (#167) proposer-ceiling probe. No model calls."""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import proposer_ceiling_probe as probe  # noqa: E402


def evaluates(text):
    """True/False as the probe would score it, or None when the guard refuses it."""
    tree = probe.arithmetic_in_x(text)
    if tree is None:
        return None
    compiled = compile(tree, "<answer>", "eval")
    return all(eval(compiled, {"__builtins__": probe.ALLOWED_CALLS}, {"x": x})
               == ((x * x) ^ (x + 11)) % 89 for x in probe.PROBE)


class TheGuardRefusesEverythingButArithmetic(unittest.TestCase):
    def test_restricted_builtins_were_not_a_sandbox(self):
        # What this guard replaces. Attribute access reaches every loaded class with no builtin
        # involved, so the previous eval of model output was escapable by a reply the probe would have
        # scored as a wrong answer. Asserted as the escape it was, against plain eval, so the reason the
        # guard exists cannot be mistaken for caution.
        escaped = eval("(1).__class__.__bases__[0]", {"__builtins__": {"abs": abs, "pow": pow}}, {"x": 1})
        self.assertIs(object, escaped)
        self.assertIsNone(probe.arithmetic_in_x("(1).__class__.__bases__[0]"))

    def test_an_attribute_a_call_or_a_subscript_is_refused(self):
        for text in ("(1).__class__", "__import__('os').system('x')", "TABLE.get(x, 0)",
                     "[y for y in (1, 2)][0]", "eval('1')", "x.__class__", "open('f')"):
            self.assertIsNone(probe.arithmetic_in_x(text), text)

    def test_a_name_other_than_x_is_refused(self):
        for text in ("y + 1", "TABLE", "x + y"):
            self.assertIsNone(probe.arithmetic_in_x(text), text)

    def test_the_rule_still_scores_as_recovered(self):
        # The guard must not change the measurement for a legitimate reply.
        self.assertTrue(evaluates("((x * x) ^ (x + 11)) % 89"))
        self.assertTrue(evaluates("(x*x ^ (x+11)) % 89"))

    def test_abs_and_pow_stay_callable_so_a_correct_reply_is_not_refused(self):
        # The evaluation this replaces exposed both, and "pow(x, 2) % 89" is a reply the model could
        # legitimately give. Refusing it would understate the ceiling the probe measures.
        self.assertTrue(evaluates("(pow(x, 2) ^ (x + 11)) % 89"))
        self.assertFalse(evaluates("abs(x) % 89"))

    def test_a_wrong_but_arithmetic_reply_still_scores_false_rather_than_refused(self):
        # The distinction the probe reports: "did not generalise" is a result, "refused" is not.
        self.assertFalse(evaluates("(x * x + 5) % 89"))
        self.assertFalse(evaluates("-x"))

    def test_a_non_integer_constant_is_refused(self):
        self.assertIsNone(probe.arithmetic_in_x("x * 1.5"))
        self.assertIsNone(probe.arithmetic_in_x("'89'"))


if __name__ == "__main__":
    unittest.main()
