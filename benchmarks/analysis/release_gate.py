"""US-12 release gate (#30) over a hash-frozen V1-05 campaign registration (#121, #122).

A release either declares no competitive claim, or names a registration committed BEFORE the
release revision plus a complete result bundle. The gate then re-derives every number itself:

- CUSTODY (#121). The registration's bytes are taken from its own earlier commit, must carry a
  valid self-hash, must be unchanged at the release revision, and must be byte-identical to the
  bundle's copy. Each family's opened test partition must hash to the sealed value, and those
  bytes must NOT exist anywhere in the repository tree at the registration commit, so the test
  tasks were not visible when the campaign was frozen.
- DECISION (#122). The bundle's report must regenerate byte-for-byte from its raw rows with
  `campaign_registration.analyze` (paired seeds, one-sided bootstrap, Holm across every family x
  OpenEvolve config, strict superiority, ties fail) at a familywise alpha of at most 0.05.
  Nothing here re-tests with another method.
- EQUAL SEARCH COST. Every row must report model calls and tokens within the shared registered
  budget, so no arm won by spending more.
- PRACTICAL EFFECT (#30). A family whose endpoint is a runtime `speedup` must also show a
  geometric-mean speedup of at least 1.10x over OpenEvolve's resulting programs, per config;
  a failed run has no speedup and so cannot satisfy it.
- SCOPE (#30). A claim is limited to the families it names, each of which must be supported.
  Every family, supported or not, stays in the assessment. A 1.x version requires the decisive
  `v1-gate` claim: every family x config superior, every speedup floor met, and AlphaEvolve's
  published values matched or beaten on a strict majority of the pre-declared problems.

Integrity checks prove consistency, not honest measurement; custody of the seal and the
campaign machine remain review responsibilities.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess

from campaign_registration import CONFIGS, OURS, SCHEMA as REGISTRATION_SCHEMA, analyze, published_gate, render, validate
from design import digest

CLAIM_SCHEMA = "evolution-release-claim-v2"
BUNDLE_SCHEMA = "evolution-release-bundle-v2"
ASSESSMENT_SCHEMA = "evolution-release-assessment-v2"
MINIMUM_GEOMEAN_SPEEDUP = 1.10
MAXIMUM_FAMILYWISE_ALPHA = 0.05
SPEEDUP_METRIC = "speedup"
MAX_ARTIFACT_BYTES = 32 * 1024 * 1024
MAX_BUNDLE_BYTES = 256 * 1024 * 1024
VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
SEGMENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")


class GateError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise GateError(message)


def text(value, name):
    require(isinstance(value, str) and 0 < len(value.strip()) <= 4096, f"{name} must be named")
    return value


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def strict_json(data, name):
    """Duplicate keys and NaN/Infinity are refused: either could hide a second value from review."""
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, f"Duplicate JSON key {key!r} in {name}")
            result[key] = value
        return result

    def nonfinite(token):
        raise GateError(f"Non-finite JSON number {token} in {name}")

    require(len(data) <= MAX_ARTIFACT_BYTES, f"{name} exceeds the inspection bound")
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=pairs, parse_constant=nonfinite)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise GateError(f"{name} is not valid UTF-8 JSON: {error}") from None


def relative_path(value, name):
    text(value, name)
    parts = value.split("/")
    require(all(SEGMENT.match(part) for part in parts), f"{name} must be a portable relative path without traversal")
    return value


def confined(root, relative, name):
    relative_path(relative, name)
    target = (root / relative).resolve(strict=True)
    require(target.is_relative_to(root.resolve()), f"{name} escapes its directory")
    return target


def git(repository, *args, binary=False):
    result = subprocess.run(["git", "-C", str(repository), *args], capture_output=True)
    require(result.returncode == 0, f"git {' '.join(args)} failed: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout if binary else result.stdout.decode().strip()


def major(version):
    match = VERSION.match(version or "")
    require(match is not None, f"Release version {version!r} is not a semantic version")
    return int(match.group(1))


def frozen_registration(data):
    """Same freeze check as campaign_registration.load_registration, applied to committed bytes."""
    registration = strict_json(data, "registration")
    require(isinstance(registration, dict), "Registration must be an object")
    claimed = registration.pop("RegistrationSha256", None)
    require(registration.get("Schema") == REGISTRATION_SCHEMA and claimed == digest(registration),
            "Registration was changed after freezing")
    validate(registration["Spec"])
    seeds = registration.get("Seeds")
    require(isinstance(seeds, list) and len(seeds) == registration["Spec"]["SeedCount"] and len(set(seeds)) == len(seeds)
            and all(type(seed) is int and 0 <= seed < 2**32 for seed in seeds), "Registration seeds are not the frozen draw")
    registration["RegistrationSha256"] = claimed
    return registration


def read_bundle(bundle):
    manifest = strict_json(confined(bundle, "manifest.json", "manifest").read_bytes(), "manifest.json")
    require(isinstance(manifest, dict) and set(manifest) == {"schema", "artifacts"} and manifest["schema"] == BUNDLE_SCHEMA,
            f"Bundle manifest must be exactly schema {BUNDLE_SCHEMA} plus artifacts")
    artifacts = manifest["artifacts"]
    require(isinstance(artifacts, dict) and 0 < len(artifacts) <= 10_000, "Bundle must inventory its artifacts")
    contents, total = {}, 0
    for name, expected in artifacts.items():
        require(name != "manifest.json", "The manifest cannot list itself")
        require(isinstance(expected, str) and HEX64.match(expected) is not None, f"Artifact {name} needs a SHA-256")
        path = confined(bundle, name, f"artifact {name}")
        require(path.is_file(), f"Artifact {name} is missing")
        data = path.read_bytes()
        total += len(data)
        require(len(data) <= MAX_ARTIFACT_BYTES and total <= MAX_BUNDLE_BYTES, "Bundle exceeds the inspection bound")
        require(sha256(data) == expected, f"Artifact digest mismatch: {name}")
        contents[name] = data
    present = {path.relative_to(bundle).as_posix() for path in bundle.rglob("*") if path.is_file()} - {"manifest.json"}
    require(present == set(contents), f"Bundle files and manifest differ: {sorted(present ^ set(contents))[:5]}")
    return contents


def partition_was_visible(repository, commit, data):
    """True when some blob in the registration commit's tree has exactly these bytes."""
    size, wanted = str(len(data)), sha256(data)
    for line in git(repository, "ls-tree", "-r", "-l", commit).splitlines():
        fields = line.partition("\t")[0].split()
        if len(fields) == 4 and fields[1] == "blob" and fields[3] == size:
            if sha256(git(repository, "cat-file", "blob", fields[2], binary=True)) == wanted:
                return True
    return False


def geometric_mean_speedups(registration, rows, family):
    """Geometric mean of ours/theirs per config; None when any paired run failed or is non-positive."""
    cells = {(row["Family"], row["Arm"], row["Seed"]): row for row in rows}
    result = {}
    for config in family["Configs"]:
        logs = []
        for seed in registration["Seeds"]:
            ours, theirs = cells[(family["Family"], OURS, seed)], cells[(family["Family"], f"openevolve-{config}", seed)]
            if ours["Status"] != "completed" or theirs["Status"] != "completed" or ours["Value"] <= 0 or theirs["Value"] <= 0:
                logs = None
                break
            logs.append(math.log(ours["Value"] / theirs["Value"]))
        result[config] = None if logs is None else math.exp(math.fsum(logs) / len(logs))
    return result


def assess_bundle(claim, root, repository):
    commit = claim["registration_commit"]
    require(isinstance(commit, str) and HEX40.match(commit) is not None, "registration_commit must be a full commit SHA")
    path = relative_path(claim["registration_path"], "registration_path")
    require(path.startswith("release/registrations/") and path.endswith(".json"),
            "The registration must be committed under release/registrations/")
    head = git(repository, "rev-parse", "HEAD")
    require(commit != head, "The registration and its result cannot first appear in the same commit")
    require(subprocess.run(["git", "-C", str(repository), "merge-base", "--is-ancestor", commit, head],
                           capture_output=True).returncode == 0,
            "The registration commit must precede the release revision")
    committed = git(repository, "show", f"{commit}:{path}", binary=True)
    require(git(repository, "show", f"HEAD:{path}", binary=True) == committed, "The registration changed after it was committed")
    registration = frozen_registration(committed)
    spec = registration["Spec"]
    require(spec["FamilywiseAlpha"] <= MAXIMUM_FAMILYWISE_ALPHA,
            f"A release claim needs a familywise alpha of at most {MAXIMUM_FAMILYWISE_ALPHA} (95% level)")

    bundle = confined(root, relative_path(claim["bundle"], "bundle") + "/manifest.json", "bundle").parent
    contents = read_bundle(bundle)
    required = {"registration.json", "raw.json", "report.json", "pilot.json", "power-analysis.json"}
    required |= {f"partitions/{family['Family']}.json" for family in spec["Families"]}
    if spec["Published"] is not None:
        required.add("published.json")
    require(set(contents) >= required, f"Bundle is missing {sorted(required - set(contents))}")
    require(contents["registration.json"] == committed, "Bundle registration differs from the committed registration")

    task_set = {}
    for family in spec["Families"]:
        name = family["Family"]
        sealed = contents[f"partitions/{name}.json"]
        require(sha256(sealed) == spec["TestPartitionHashes"][name], f"Opened {name} test partition does not match its seal")
        require(not partition_was_visible(repository, commit, sealed),
                f"The {name} test partition was in the repository when the registration was frozen")
        task_set[name] = spec["TestPartitionHashes"][name]

    rows = strict_json(contents["raw.json"], "raw.json")
    require(isinstance(rows, list), "raw.json must be the runner's row list")
    budget = spec["Budget"]
    for row in rows:
        require(isinstance(row, dict), "Every raw row must be an object")
        for field, cap in (("ModelCalls", budget["ModelCalls"]), ("ModelTokens", budget["ModelTokens"])):
            value = row.get(field)
            require(type(value) is int and 0 <= value <= cap,
                    f"Row {row.get('Family')}/{row.get('Arm')}/{row.get('Seed')} {field} is unreported or exceeds the shared budget")
    report = analyze(registration, rows)
    require(render(report) == contents["report.json"], "report.json does not regenerate byte-for-byte from raw.json")

    families = []
    for family in spec["Families"]:
        endpoints = [e for e in report["Endpoints"] if e["Family"] == family["Family"]]
        superior = all(e["Superior"] for e in endpoints)
        speedups, floor = None, True
        if family["Endpoint"]["Metric"] == SPEEDUP_METRIC:
            require(family["Endpoint"]["Direction"] == "maximize", "A speedup endpoint must be maximized")
            speedups = geometric_mean_speedups(registration, rows, family)
            floor = all(value is not None and value >= MINIMUM_GEOMEAN_SPEEDUP for value in speedups.values())
        families.append(dict(Family=family["Family"], Supported=superior and floor, HolmSuperiorAllConfigs=superior,
                             GeometricMeanSpeedup=speedups, MeetsSpeedupFloor=floor if speedups is not None else None,
                             Endpoints=endpoints))

    published = None
    if spec["Published"] is not None:
        published = published_gate(registration, strict_json(contents["published.json"], "published.json"))

    supported = {f["Family"] for f in families if f["Supported"]}
    names = [f["Family"] for f in spec["Families"]]
    if claim["claim"] == "v1-gate":
        eligible = bool(report["OpenEvolveGate"] and supported == set(names) and published is not None and published["Gate"]
                        and all(list(f["Configs"]) == list(CONFIGS) for f in spec["Families"]))
        claimed = names
    else:
        claimed = claim["families"]
        require(isinstance(claimed, list) and claimed and all(isinstance(c, str) for c in claimed)
                and len(set(claimed)) == len(claimed) and set(claimed) <= set(names),
                "families must name registered families, once each")
        eligible = set(claimed) <= supported
    return dict(eligible=eligible, status="supported" if eligible else "not-supported", claimed_families=claimed,
                registration_sha256=registration["RegistrationSha256"], registration_commit=commit,
                report=dict(benchmark=claim["benchmark"], campaign=spec["Campaign"], task_set=task_set,
                            models=spec["Models"], budget=budget, seeds=len(registration["Seeds"]),
                            hardware=claim["hardware"], bundle=claim["bundle"], artifacts=sorted(contents)),
                openevolve_gate=report["OpenEvolveGate"], families=families, published=published)


def release(root, repository, version):
    """Assess release/claim.json for `version`. Absence of a claim file is the absence of a claim."""
    root, repository = Path(root).resolve(), Path(repository).resolve()
    one_point_oh = major(version) >= 1
    claim_path = root / "claim.json"
    if not claim_path.exists():
        claim = dict(schema=CLAIM_SCHEMA, claim="none", reason="No release/claim.json at this revision.")
    else:
        claim = strict_json(claim_path.read_bytes(), "claim.json")
    require(isinstance(claim, dict) and claim.get("schema") == CLAIM_SCHEMA, f"claim.json must use {CLAIM_SCHEMA}")
    base = dict(schema=ASSESSMENT_SCHEMA, version=version, claim=claim.get("claim"))
    if claim.get("claim") == "none":
        require(set(claim) == {"schema", "claim", "reason"}, "A no-claim release carries only its reason")
        text(claim["reason"], "reason")
        if one_point_oh:
            return dict(base, eligible=False, status="1.x-requires-v1-gate",
                        reason="Version 1.0 and later ship only on the decisive campaign (#122): declare a v1-gate claim.")
        return dict(base, eligible=True, status="no-competitive-claim", reason=claim["reason"])
    require(claim.get("claim") in ("supported-families", "v1-gate"), "claim must be none, supported-families or v1-gate")
    keys = {"schema", "claim", "benchmark", "hardware", "registration_commit", "registration_path", "bundle"}
    require(set(claim) == keys | ({"families"} if claim["claim"] == "supported-families" else set()),
            f"A {claim['claim']} claim must carry exactly its declared fields")
    text(claim["benchmark"], "benchmark")
    text(claim["hardware"], "hardware")
    assessment = dict(base, **assess_bundle(claim, root, repository))
    if one_point_oh and claim["claim"] != "v1-gate":
        assessment.update(eligible=False, status="1.x-requires-v1-gate")
    return assessment


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--release", type=Path, required=True, help="Directory holding claim.json and its bundles")
    parser.add_argument("--repository", type=Path, default=Path("."), help="Git checkout of the release revision")
    parser.add_argument("--version", required=True, help="Version being released, for example 0.4.0")
    parser.add_argument("--output", type=Path, required=True, help="New file for the assessment")
    args = parser.parse_args()
    with args.output.open("x", encoding="utf-8", newline="\n") as output:
        try:
            assessment = release(args.release, args.repository, args.version)
        # ANY failure is an assessment, never an empty file: a crafted bundle can raise more than the expected four
        # (OverflowError from a huge ratio, ZeroDivisionError from analyze), and an uncaught one left the published
        # release-evidence.json empty. The gate still fails closed; now it also says why.
        except Exception as error:  # noqa: BLE001 -- recorded in the assessment, and the exit code is non-zero

            assessment = dict(schema=ASSESSMENT_SCHEMA, version=args.version, eligible=False, status="invalid-evidence",
                              error=f"{type(error).__name__}: {error}")
        json.dump(assessment, output, indent=2, allow_nan=False)
        output.write("\n")
    print(json.dumps({key: assessment[key] for key in ("version", "claim", "eligible", "status", "error") if key in assessment}))
    raise SystemExit(0 if assessment["eligible"] else 1)


if __name__ == "__main__":
    main()