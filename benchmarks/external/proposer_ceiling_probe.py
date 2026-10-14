"""Can the proposer solve this task at all, under a prompt that tells it exactly what not to do?

#167's settled finding is that feedback cannot promote a program that was never proposed. That is only
the interesting conclusion if the proposer COULD have proposed one. If the task is beyond the model,
both the #167 and #166 ablations measured a floor, and that qualifies both.

Five independent attempts, each a single call with the examples, the held-out warning, and an explicit
instruction not to store the answers.
"""
import json, os, subprocess, sys

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


solved = 0
for attempt in range(5):
    answer = ask().strip().strip("`").strip()
    try:
        ok = all(eval(answer, {"__builtins__": {"abs": abs, "pow": pow}}, {"x": x})
                 == ((x * x) ^ (x + 11)) % 89 for x in PROBE)
    except Exception as error:
        ok = "error: " + type(error).__name__
    solved += 1 if ok is True else 0
    print(json.dumps({"attempt": attempt, "generalises": ok, "answer": answer[:90]}), flush=True)

print("recovered the rule in %d of 5 single-call attempts" % solved)
