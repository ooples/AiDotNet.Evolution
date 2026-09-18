"""Final gate: real numeric pilot, report, locked fresh campaign, one-use refusal."""
import argparse
import copy
import json
from pathlib import Path
import subprocess

from analyze import analyze, load_json, markdown, require
from design import freeze, plan_design, write_new
from presentation import html_report
from run_fixed import run, runtime_contract


def verify(dll, revision, output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    pilot_path = output / "pilot.json"
    with (output / "pilot-stdout.log").open("xb") as stdout, (output / "pilot-stderr.log").open("xb") as stderr:
        child = subprocess.run(["dotnet", str(Path(dll).resolve()), "2", "16", revision, str(pilot_path.resolve())],
                               stdout=stdout, stderr=stderr, timeout=180)
    require(child.returncode == 0, "Pilot runner failed; raw evidence retained.")
    campaign, _ = load_json(pilot_path, 256 * 1024 * 1024)
    plan = dict(SchemaVersion=2, Purpose="retrospective-development", SourceRevision=revision,
                Protocol=campaign["Protocol"], Tasks=[dict(Name=task, Scale=1, TargetLoss=.5)
                    for task in dict.fromkeys(r["Task"] for r in campaign["Runs"])],
                Methods=campaign["Methods"], SeedCount=2, Budget=16, PrimaryMethod="DiagonalCma",
                Comparators=[m for m in campaign["Methods"] if m != "DiagonalCma"],
                BootstrapSamples=200, BootstrapSeed=37, ResampleTasks=False, ConfidenceLevel=.95)
    write_new(output / "pilot-plan.json", plan)
    report = analyze(campaign, plan)
    write_new(output / "pilot-report.json", report)
    (output / "pilot-report.md").write_text(markdown(report), encoding="utf-8")
    (output / "pilot-report.html").write_text(html_report(report), encoding="utf-8")
    design = plan_design(campaign, plan, effect=.8)
    write_new(output / "design.json", design)
    registration = freeze(output / "registered", design, runtime_contract(dll))
    identity = registration["RegistrationSha256"]
    result = run(output / "registered", identity, dll)
    require(len(result["Runs"]) == 4 * 6 * design["RequiredRunsPerTaskMethod"], "Fixed schedule lost runs.")
    require(all(r["Status"] == "completed" for r in result["Runs"]), "Actual fixed campaign has failed runs.")
    before = (output / "registered" / "raw-campaign.json").read_bytes()
    try:
        run(output / "registered", identity, dll)
        raise AssertionError("Repeated consumption was accepted")
    except FileExistsError:
        require(before == (output / "registered" / "raw-campaign.json").read_bytes(), "Repeated consumption altered results")
    # Deliberately corrupt a COPY to verify every failed/missing run and dropped
    # curve remains visible without altering the successful campaign evidence.
    adverse = copy.deepcopy(campaign)
    adverse["Runs"].pop(0)
    adverse["Runs"][0]["Status"] = "failed"
    adverse["Runs"][1]["DroppedTraceRecords"] = 1
    adverse_report = analyze(adverse, plan)
    write_new(output / "adverse-input.json", adverse)
    write_new(output / "adverse-report.json", adverse_report)
    (output / "adverse-report.html").write_text(html_report(adverse_report), encoding="utf-8")
    require(len(adverse_report["Runs"]) == len(campaign["Runs"]), "Missing run disappeared")
    write_new(output / "verification.json", dict(Status="passed", PilotRuns=len(campaign["Runs"]),
              FixedRuns=len(result["Runs"]), FreshSeeds=len(registration["SearchSeeds"]),
              RepeatedConsumption="refused-without-changing-results", ConfirmatoryEligible=False))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evaluator", required=True, type=Path)
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = verify(args.evaluator, args.source_revision, args.output)
    print(json.dumps({"status": "passed", "fixed_runs": len(result["Runs"])}))
