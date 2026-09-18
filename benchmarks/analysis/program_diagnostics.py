"""Retrospective loss diagnosis; never changes frozen scores or executes candidates."""
import argparse
import ast
import hashlib
import json
from pathlib import Path

from analyze import require
from design import digest, write_new
from program_report import read


def fingerprint(code):
    require(isinstance(code, str) and 0 < len(code.encode()) <= 65536, "Invalid candidate size.")
    exact = hashlib.sha256(code.encode()).hexdigest()
    try:
        structure = ast.dump(ast.parse(code), include_attributes=False)
    except (SyntaxError, RecursionError, ValueError):
        return exact, None
    return exact, hashlib.sha256(structure.encode()).hexdigest()


def diagnose(root):
    root = Path(root)
    plan, study = read(root / "registration.json"), read(root / "study.json")
    require(study.get("schema") == "evolution-program-study-v1" and study.get("claim") == "none" and
            plan.get("schema") == "evolution-program-estimation-registration-v1" and
            study.get("status") == "completed" and study["registration_sha256"] == digest(plan), "Require bound completed evidence.")
    result = []
    for block in plan["schedule"]:
        if block["phase"] != "confirmation":
            continue
        for task in block["tasks"]:
            comparison = read(root / block["id"] / task / "comparison.json")
            initial = (root / block["id"] / task / "initial.py").read_text(encoding="utf-8")
            require(comparison["seed"] == block["seed"] and fingerprint(initial)[0] == comparison["initial_program_hash"] ==
                    plan["tasks"][task]["source_sha256"], "Initial identity mismatch.")
            runs = {(r["mode"], r["method"]): r for r in comparison["runs"]}
            require(len(runs) == len(comparison["runs"]), "Duplicate run.")
            for mode in ("controlled", "native-bounded"):
                a, b = runs[mode, "aidotnet"], runs[mode, "openevolve"]
                ah, aa = fingerprint(a["selected_code"])
                bh, ba = fingerprint(b["selected_code"])
                require(ah == a["selected_hash"] and bh == b["selected_hash"], "Selected source hash mismatch.")
                details = {}
                for method, run in (("aidotnet", a), ("openevolve", b)):
                    receipts = run["receipts"]
                    evaluations = [r for r in receipts if r["operation"] == "evaluate"]
                    identities = [fingerprint(r["request"]["code"])[0] for r in evaluations]
                    models = [r for r in receipts if r["operation"] == "model"]
                    original_prompt = "Parent program:\n```python\n" + initial + "\n```"
                    details[method] = dict(model_calls=len(models), evaluated_sources=len(identities),
                        unique_evaluated_sources=len(set(identities)), repeated_evaluations=len(identities)-len(set(identities)),
                        later_original_parent_prompts=sum(any(m.get("content", "").startswith(original_prompt)
                            for m in r["request"]["messages"]) for r in models[1:]) if mode == "controlled" else None,
                        selected_hash=run["selected_hash"])
                result.append(dict(block=block["id"], task=task, mode=mode,
                    identical_source=ah == bh, identical_ast=aa is not None and aa == ba, methods=details))
    return dict(schema="program-loss-diagnostics-v1", claim="none", purpose="retrospective-development",
        source_study_sha256=hashlib.sha256((root / "study.json").read_bytes()).hexdigest(), pairs=result,
        limitations=["All original outcomes, losses and confidence intervals remain unchanged.",
                     "AST equality is a structural diagnostic, not proof of behavioral or performance equivalence.",
                     "Two model calls cannot establish population-search effectiveness; duplicate evaluations may amplify timing selection noise.",
                     "These inspected tasks/results are development evidence for future changes, not fresh confirmation."])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    value = diagnose(args.root)
    write_new(args.output, value)
    print(json.dumps(dict(pairs=len(value["pairs"]), identical_source=sum(r["identical_source"] for r in value["pairs"]),
                         identical_ast=sum(r["identical_ast"] for r in value["pairs"])) ))
