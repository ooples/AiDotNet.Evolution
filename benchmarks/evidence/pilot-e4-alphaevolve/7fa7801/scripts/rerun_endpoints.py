"""Re-run every returned program's construction twice more and score it, one at a time on an idle machine."""
import json, sys, time
from pathlib import Path

sys.path.insert(0, r"F:/evo-pilot-main/benchmarks/external")
import alphaevolve_pilot as ap
import alphaevolve_tasks as tasks

PY = r"F:/evo-pilot/venv/Scripts/python.exe"
programs = Path(r"F:/evo-pilot-main/benchmarks/evidence/pilot-e4-alphaevolve/7fa7801/programs")
out_path = Path(sys.argv[1])
results = json.loads(out_path.read_text()) if out_path.is_file() else {}
for repeat in range(2):
    for program in sorted(programs.glob("*.py")):
        key = f"{program.stem}#{repeat}"
        if key in results:
            continue
        problem = program.stem.split("__")[0]
        started = time.time()
        heights, reason = ap.construction(program, PY)
        value = None if heights is None else float(tasks.score(problem, heights)["combined_score"])
        results[key] = dict(value=value, reason=reason, seconds=round(time.time() - started, 1))
        tmp = out_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(results, indent=1))
        tmp.replace(out_path)
        print(key, value, results[key]["seconds"], flush=True)
print("DONE", flush=True)
