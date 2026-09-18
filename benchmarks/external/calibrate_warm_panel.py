"""Development-only, no-provider endpoint calibration; never opens final families."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess

from docker_sandbox import encode
from warm_panel import DEFINITIONS, prepare_task
from warm_sandbox import WarmDockerSandbox


def run(output, upstream, image, multiplier=1):
    if type(multiplier) is not int or multiplier not in (1,4,16):
        raise ValueError("Invalid preflight workload multiplier")
    root = Path(output)
    root.mkdir(parents=True, exist_ok=False)
    tasks = [t for t, definition in DEFINITIONS.items() if definition["partition"] == "development"]
    implementation = Path(__file__).parent
    files = [*implementation.glob("*.py"), *implementation.joinpath("sandbox").glob("*.py"),
             implementation / "sandbox" / "Dockerfile", implementation.parent / "suite" / "catalog-v1.json"]
    artifacts = {str(p.resolve()): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    plan = dict(schema="warm-endpoint-calibration-v2", tasks=tasks, seeds=[1101,1103], samples=3, multiplier=multiplier,
                artifacts_sha256=artifacts, image=image,
                upstream_revision=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=upstream, text=True).strip(),
                claim="none", provider_calls=0, purpose="endpoint qualification, not competitor efficacy")
    (root / "plan.json").write_bytes(encode(plan))
    sandbox = WarmDockerSandbox(image, root / "sandbox")
    result = dict(plan=plan, rows=[dict(task=t, method=m, status="not-run", samples=[], median_seconds=None)
                                  for t in tasks for m in ("original", "echo-calibration")], status="running", claim="none")
    (root / "report.json").write_bytes(encode(result))
    try:
        for task_id in tasks:
            task = prepare_task(upstream, task_id, partition="development", seeds=plan["seeds"],
                                scale=DEFINITIONS[task_id]["scale"] * multiplier)
            name = task["metadata"]["class"]
            for method, code in (("original", task["initial"]), ("echo-calibration", f"class {name}:\n def solve(self,p): return p\n")):
                item = next(r for r in result["rows"] if r["task"] == task_id and r["method"] == method)
                item.update(status="running", metadata=task["metadata"])
                rows = item["samples"]
                (root / "report.json").write_bytes(encode(result))
                for _ in range(plan["samples"]):
                    row = sandbox.run(code, {"class": name, "problems": task["cases"]}, phase="adversarial")
                    valid = row["status"] == "completed" and (task["validate"](row["output"]) if method == "original" else True)
                    rows.append(dict(receipt_id=row["id"], status=row["status"], valid=valid,
                                     warm_seconds=row["elapsed_seconds"], startup_seconds=row["startup_seconds"], resources=row["resources"]))
                    (root / "report.json").write_bytes(encode(result))
                    if not valid:
                        item["status"] = "failed"
                        raise RuntimeError("Endpoint calibration failed: " + task_id + "/" + method)
                item.update(status="completed", median_seconds=statistics.median(r["warm_seconds"] for r in rows))
                (root / "report.json").write_bytes(encode(result))
                print(json.dumps(dict(task=task_id,method=method,seconds=item["median_seconds"])),flush=True)
        result["status"] = "completed"
    except Exception as error:
        result.update(status="failed", error=type(error).__name__ + ": " + str(error)[:300])
    finally:
        for item in result["rows"]:
            if item["status"] == "running":
                item["status"] = "failed"
        result["attempted_containers"] = len(sandbox.rows)
        result["unknown_work"] = sum(r["unknown_work"] for r in sandbox.rows)
        (root / "report.json").write_bytes(encode(result))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("output", "upstream", "image"):
        parser.add_argument("--"+name,required=True)
    parser.add_argument("--multiplier",type=int,default=1)
    value = run(**vars(parser.parse_args()))
    print(json.dumps({k:value[k] for k in ("status","attempted_containers","unknown_work")}))
    raise SystemExit(0 if value["status"] == "completed" else 1)
