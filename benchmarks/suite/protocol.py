"""Frozen, family-disjoint suite plans. No evaluator or model is invoked here."""
from __future__ import annotations

import hashlib
import json
import re
import secrets
from pathlib import Path

CATALOG = Path(__file__).with_name("catalog-v1.json")
PARTITIONS = ("development", "selection", "final")
METHODS = ("RandomSearch", "HillClimb", "FixedMapElites", "AdaptiveMapElites", "UniformPortfolioMapElites", "DiagonalCma")


def configuration(value):
    if set(value) != {"schema", "numeric_methods", "program_solver"} or value["schema"] != "aidotnet-suite-configuration-v1" or value["program_solver"] != "upstream-reference":
        raise ValueError("Unknown solver configuration")
    methods = value["numeric_methods"]
    if not isinstance(methods, list) or not 3 <= len(methods) <= 6 or len(set(methods)) != len(methods) or not set(methods) <= set(METHODS) or not {"RandomSearch", "HillClimb"} <= set(methods):
        raise ValueError("Select registered numeric methods while retaining both fixed controls")
    return value


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def digest(value):
    return hashlib.sha256(encode(value)).hexdigest()


def read_json(path, maximum=1024 * 1024):
    path = Path(path)
    if path.stat().st_size > maximum:
        raise ValueError("JSON input exceeds its bound")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON property")
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite JSON")))


def write_new(path, value):
    # Exclusive creation: never silently replace a plan, selection or final receipt.
    with Path(path).open("xb") as output:
        output.write(encode(value))


def catalog(path=CATALOG):
    value = read_json(path)
    if value.get("schema") != "aidotnet-benchmark-suite-v1" or value.get("partitions") != list(PARTITIONS):
        raise ValueError("Unknown suite schema")
    if not re.fullmatch("[0-9a-f]{40}", value.get("upstream_revision", "")):
        raise ValueError("Pin an upstream revision")
    if not 10 <= len(value.get("algotune", [])) <= 20 or len(value.get("numeric", [])) != 9:
        raise ValueError("Require the numeric panel and 10–20 application tasks")
    tasks = value["numeric"] + value["algotune"]
    if len({task["id"] for task in tasks}) != len(tasks):
        raise ValueError("Duplicate task identity")
    families = {}
    for task in tasks:
        if task["partition"] not in PARTITIONS or not re.fullmatch("[a-z0-9_-]+", task["id"]):
            raise ValueError("Invalid task or partition")
        if not re.fullmatch("[a-z0-9-]+", str(task.get("family", ""))):
            raise ValueError("Invalid task family")
    for task in value["algotune"]:
        # The pinned blob, class and scale are load-bearing: the blob is the only check that the
        # upstream source is unmodified, and the scale is passed straight to generate_problem.
        if not re.fullmatch("[0-9a-f]{40}", str(task.get("blob", ""))):
            raise ValueError("Pin an exact upstream Git blob for every application task")
        if not re.fullmatch("[A-Za-z_][A-Za-z0-9_]*", str(task.get("class", ""))):
            raise ValueError("Name an exact upstream solver class")
        if type(task.get("scale")) is not int or not 1 <= task["scale"] <= 1024:
            raise ValueError("Bound the application task scale to 1..1024")
        previous = families.setdefault(task["family"].casefold(), task["partition"])
        if previous != task["partition"]:
            raise ValueError("Semantic family leaked across partitions")
    for partition in PARTITIONS:
        for panel in (value["numeric"], value["algotune"]):
            if len({task["family"] for task in panel if task["partition"] == partition}) < 2:
                raise ValueError("Each panel partition requires multiple families")
    return value


def prepare(directory, *, instances=2, replicates=2, budget=64, source_revision, smoke=False):
    if type(instances) is not int or not 1 <= instances <= 16 or type(replicates) is not int or not 1 <= replicates <= 16:
        raise ValueError("Bound instances and replicates to 1..16")
    if type(budget) is not int or not 8 <= budget <= 4096 or instances * replicates * budget * 18 > 2_000_000:
        raise ValueError("Invalid evaluation budget")
    if not re.fullmatch("[0-9a-f]{40}", source_revision):
        raise ValueError("Record an exact source revision")
    definition = catalog()
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    roots = {partition: secrets.token_hex(32) for partition in PARTITIONS}
    plan = {"schema": "aidotnet-suite-plan-v1", "mode": "contract-smoke" if smoke else "registered",
            "catalog": definition, "catalog_hash": digest(definition), "instances": instances,
            "replicates": replicates, "budget": budget, "source_revision": source_revision,
            "commitments": {key: hashlib.sha256(bytes.fromhex(root)).hexdigest() for key, root in roots.items()}}
    plan["plan_hash"] = digest(plan)
    write_new(directory / "plan.json", plan)
    # Keep final-private.json outside model/config-selection inputs. Filesystem owners can bypass
    # this convention; it is an auditable workflow, not access control or a cryptographic sandbox.
    for partition, root in roots.items():
        write_new(directory / f"{partition}-private.json", {"plan_hash": plan["plan_hash"], "root": root})
    return plan


def load_plan(directory):
    value = read_json(Path(directory) / "plan.json")
    claimed = value.get("plan_hash")
    payload = {key: item for key, item in value.items() if key != "plan_hash"}
    if value.get("schema") != "aidotnet-suite-plan-v1" or digest(payload) != claimed or value.get("catalog_hash") != digest(catalog()):
        raise ValueError("Plan or catalogue changed after registration")
    if value.get("catalog") != catalog():
        raise ValueError("Plan catalogue differs")
    return value


def freeze(directory, configuration_file, selection_report):
    directory = Path(directory)
    plan = load_plan(directory)
    report = read_json(selection_report, 32 * 1024 * 1024)
    if report.get("plan_hash") != plan["plan_hash"] or report.get("partition") != "selection" or report.get("status") != "completed":
        raise ValueError("Freeze requires a completed selection report from this plan")
    selected = configuration(read_json(configuration_file))
    frozen = {"plan_hash": plan["plan_hash"], "configuration": selected, "configuration_hash": digest(selected),
              "selection_report_hash": digest(report), "runtime_contract": report["runtime_contract"]}
    write_new(directory / "frozen.json", frozen)
    return frozen


def materialize(directory, partition, *, claim_final=False):
    directory = Path(directory)
    if partition not in PARTITIONS:
        raise ValueError("Unknown partition")
    plan = load_plan(directory)
    selected = {"schema": "aidotnet-suite-configuration-v1", "numeric_methods": list(METHODS), "program_solver": "upstream-reference"}
    configuration_hash = digest(selected)
    if partition == "final" and plan["mode"] != "contract-smoke":
        frozen = read_json(directory / "frozen.json")
        selected = configuration(frozen.get("configuration", {}))
        if frozen.get("plan_hash") != plan["plan_hash"] or frozen.get("configuration_hash") != digest(selected):
            raise ValueError("Final evaluation requires the frozen configuration")
        if not claim_final:
            raise ValueError("Final instances are only materialized during their single admitted execution")
        configuration_hash = frozen["configuration_hash"]
        # Consume before exposing seeds or invoking evaluators; failures do not reset holdout access.
        write_new(directory / "final-consumed.json", frozen)
    private = read_json(directory / f"{partition}-private.json")
    if private.get("plan_hash") != plan["plan_hash"] or not re.fullmatch("[0-9a-f]{64}", private.get("root", "")):
        raise ValueError("Invalid private instance root")
    root = bytes.fromhex(private["root"])
    if hashlib.sha256(root).hexdigest() != plan["commitments"][partition]:
        raise ValueError("Private instance root violates commitment")

    def seed(*parts):
        return int.from_bytes(hashlib.sha256(root + encode(parts)).digest()[:4], "big")

    panels = {}
    for name in ("numeric", "algotune"):
        panels[name] = [{"id": task["id"], "seed": seed(name, task["id"], instance),
                         "search_seeds": [seed("search", instance, replicate) for replicate in range(plan["replicates"])]}
                        for task in plan["catalog"][name] if task["partition"] == partition
                        for instance in range(plan["instances"])]
    return plan, configuration_hash, panels, selected
