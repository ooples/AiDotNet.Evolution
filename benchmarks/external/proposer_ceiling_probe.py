"""Can the proposer solve this task at all, under a prompt that tells it exactly what not to do?

#167's settled finding is that feedback cannot promote a program that was never proposed. That is only
the interesting conclusion if the proposer COULD have proposed one. If the task is beyond the model,
both the #167 and #166 ablations measured a floor, and that qualifies both.

Five independent attempts, each a single call with the examples, the held-out warning, and an explicit
instruction not to store the answers.
"""
import ast, json, os, subprocess, sys

VISIBLE = [(x, ((x * x) ^ (x + 11)) % 89) for x in (-4, -1, 2, 5, 9, 13)]
PROBE = (-7, 3, 8, 20, -15, 0, 30)

PROMPT = ("Here are input/output pairs of a deterministic integer function f:\n"
          + "\n".join("f(%d) = %d" % p for p in VISIBLE)
          + "\n\nRecover the rule. It will be tested on inputs far outside this range, so a lookup table "
            "of these six pairs scores zero. Do NOT store these answers. Reply with ONLY a python "
            "expression in x, no prose and no code fence.")


def ask():
    out = subprocess.run([os.environ.get("CLAUDE", "claude"), "-p", "--output-format", "json",
                          "--model", "sonnet", "--max-turns", "6"],
                         input=PROMPT, capture_output=True, text=True, timeout=400)
    return json.loads(out.stdout).get("result") or ""


# The answer is model output, so it is parsed rather than trusted. Restricting __builtins__ is not a
# sandbox: `(1).__class__.__bases__[0].__subclasses__()` reaches every loaded class through attribute
# access alone, with no builtin involved - verified against this probe's own evaluation before this guard
# existed. An arithmetic expression in x needs none of that, so the grammar is whitelisted instead:
# numbers, x, the arithmetic and bitwise operators, unary minus, and parentheses. Anything else - an
# attribute, a call, a subscript, a name that is not x - is refused as a non-answer rather than evaluated.
ALLOWED_NODES = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Constant, ast.Name, ast.Load, ast.Call,
                 ast.Add, ast.Sub, ast.Mult, ast.Div, ast.FloorDiv, ast.Mod, ast.Pow,
                 ast.BitXor, ast.BitOr, ast.BitAnd, ast.LShift, ast.RShift, ast.USub, ast.UAdd, ast.Invert)
# abs and pow stay callable because the evaluation this replaces exposed them, and "pow(x, 2) % 89" is a
# reply the model could legitimately give. Refusing it would turn a correct answer into a refusal and
# understate the ceiling this probe exists to measure. Attribute access is what escapes, and it is banned
# whether or not a call is involved.
ALLOWED_CALLS = {"abs": abs, "pow": pow}


def arithmetic_in_x(text):
    """The parsed expression when it is arithmetic in x alone, else None."""
    tree = ast.parse(text, mode="eval")
    for node in ast.walk(tree):
        if not isinstance(node, ALLOWED_NODES):
            return None
        if isinstance(node, ast.Call) and not (isinstance(node.func, ast.Name)
                                               and node.func.id in ALLOWED_CALLS
                                               and not node.keywords):
            return None
        if isinstance(node, ast.Name) and node.id != "x" and node.id not in ALLOWED_CALLS:
            return None
        if isinstance(node, ast.Constant) and not isinstance(node.value, int):
            return None
    return tree


def main():
    solved = 0
    for attempt in range(5):
        answer = ask().strip().strip("`").strip()
        try:
            tree = arithmetic_in_x(answer)
            if tree is None:
                ok = "refused: not an arithmetic expression in x"
            else:
                compiled = compile(tree, "<answer>", "eval")
                ok = all(eval(compiled, {"__builtins__": ALLOWED_CALLS}, {"x": x})
                         == ((x * x) ^ (x + 11)) % 89 for x in PROBE)
        except Exception as error:
            ok = "error: " + type(error).__name__
        solved += 1 if ok is True else 0
        print(json.dumps({"attempt": attempt, "generalises": ok, "answer": answer[:90]}), flush=True)

    print("recovered the rule in %d of 5 single-call attempts" % solved)
    return 0


# Guarded, so importing this module cannot run it. Without the guard the loop is module level: a test that
# imports the probe to exercise its expression guard would make five real model calls as a side effect of
# the import, which is a bill rather than a test.
if __name__ == "__main__":
    raise SystemExit(main())
