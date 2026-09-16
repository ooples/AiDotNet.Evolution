"""Opt-in cheap-to-full search evaluation; frozen rejection audits never feed search."""
import copy
import math
import random
import statistics

from program_controls import candidate_hash
from warm_confirmation import assess, confirm, policy as noise_policy
from warm_study_design import digest
from warm_evaluator import WarmEvaluator


def policy(enabled=False):
    if type(enabled) is not bool:
        raise ValueError("Screening opt-in must be explicit")
    return dict(schema="cheap-screen-v2", enabled=enabled, scale_divisor=1, baseline_samples=3,
                slow_ratio=4., minimum_seconds=.002, audit_candidates=2, audit_pairs=3)


def validate_policy(value):
    if value != policy(value.get("enabled")):
        raise ValueError("Unknown frozen screening policy")


def validate_registration(plan):
    validate_policy(plan["screening_policy"])
    if type(plan["screening_seed"]) is not int or not 0 <= plan["screening_seed"] < 2**64:
        raise ValueError("Predeclared independent screening audit seed required")
    if plan["screening_policy"]["enabled"]:
        seeds = plan["screening_instance_seeds"]
        reserved = [s for key in ("search_instance_seeds","diagnostic_instance_seeds","audit_instance_seeds") for s in plan[key]]
        if (not isinstance(seeds,list) or len(seeds) != 1 or type(seeds[0]) is not int
                or not 0 <= seeds[0] < 2**32 or seeds[0] in reserved):
            raise ValueError("Cheap inputs must be separate from full search and final acceptance inputs")


def reason(screen, baseline, frozen):
    if screen["status"] != "valid":
        return "incorrect-screen"
    # A deliberately conservative heuristic, NOT a confidence interval. Small
    # workload timing can reverse at full scale; rejection audits expose that.
    if (screen["duration_seconds"] > frozen["minimum_seconds"]
            and screen["duration_seconds"] > baseline["duration_seconds"] * frozen["slow_ratio"]):
        return "slow-screen"
    return "advance"


def combined(screen, full, decision):
    parts = [screen] + ([full] if full is not None else [])
    result = dict(full if full is not None else screen)
    if full is None:
        result.update(status="invalid", quality=-1e300)
    result.update(work_units=sum(p["work_units"] for p in parts),
                  fitness_sample_ids=list(full["sample_ids"]) if full is not None and full["status"] == "valid" else [],
                  evaluator_sha256=digest(["cheap-to-full-v1",*[p["evaluator_sha256"] for p in parts]]),
                  screening=dict(reason=decision, screen=copy.deepcopy(screen), full=copy.deepcopy(full)))
    for key in ("sample_ids", "budget_operation_ids", "resources", "startup_samples"):
        result[key] = [item for p in parts for item in p[key]]
    return result


def selected_rejects(identities, seed, owner, count):
    return sorted(set(identities), key=lambda identity:digest([seed,owner,identity]))[:count]


def selected_population(populations, seed, count):
    population = [(owner,identity) for owner,identities in populations.items() for identity in set(identities)]
    return sorted(population,key=lambda item:(digest([seed,*item]),item))[:count]


def audit_policy(tracks, frozen):
    # Across three partitions: half alpha for both classification tails over all
    # possible audits, half for sampling the rejected populations.
    value = noise_policy(tracks * frozen["audit_candidates"], frozen["audit_pairs"])
    value["alpha"] = .05 / (12 * tracks * frozen["audit_candidates"])
    return value


def classification(values, frozen):
    if values["original"]["status"] != "valid":
        raise ValueError("Rejection audit baseline invalid")
    if values["selected"]["status"] != "valid":
        return "not-useful"
    if assess(values,frozen)["confirmed"]:
        return "useful"
    reverse = dict(original=values["selected"],selected=values["original"])
    return "not-useful" if assess(reverse,frozen)["confirmed"] else "unresolved"


def audit_summary(population, audits, tracks):
    if not population:
        return dict(rejected=0, audited=0, useful=0, unresolved=0, lower=0., upper=0.)
    if not audits:
        return dict(rejected=len(population),audited=0,useful=0,unresolved=0,lower=0.,upper=1.)
    useful = sum(a["classification"] == "useful" for a in audits)
    unresolved = sum(a["classification"] == "unresolved" for a in audits)
    radius = 0 if len(audits) == len(population) else math.sqrt(math.log(12*tracks/.05)/(2*len(audits)))
    return dict(rejected=len(population),audited=len(audits),useful=useful,unresolved=unresolved,
                lower=max(0.,useful/len(audits)-radius),upper=min(1.,(useful+unresolved)/len(audits)+radius))


class ScreenedEvaluator:
    def __init__(self, initial, screen, full, auditor, frozen, *, tracks, seed):
        validate_policy(frozen)
        if (not frozen["enabled"] or screen.samples != 1 or auditor.samples != 1
                or screen.phase != "search" or full.phase != "search" or auditor.phase != "confirmation"
                or type(seed) is not int or not 0 <= seed < 2**64):
            raise ValueError("Invalid isolated screening evaluators")
        self.initial, self.screen, self.full, self.auditor = initial,screen,full,auditor
        self.baseline = WarmEvaluator(screen.sandbox,screen.class_name,screen.cases,screen.validate,
            identity=screen.manifest["identity"],samples=frozen["baseline_samples"],phase="search",
            budget=screen.budget,stage=screen.stage)
        self.policy, self.tracks, self.seed = copy.deepcopy(frozen),tracks,seed
        self.audit_policy = audit_policy(tracks,frozen)
        self.manifest = dict(identity=digest([screen.manifest,full.manifest,frozen]),
                             screen=screen.manifest,baseline=self.baseline.manifest,
                             full=full.manifest,auditor=auditor.manifest,policy=self.policy)
        self.states, self.frozen = {},False

    def __call__(self, code, *, owner):
        if self.frozen:
            raise ValueError("Search already frozen for rejection audit")
        if owner not in self.states and len(self.states) >= self.tracks:
            raise ValueError("Undeclared screen owner")
        state = self.states.setdefault(owner,dict(baseline=None,rejected={},events=[]))
        if len(state["events"]) >= 65 or (state["baseline"] is None and code != self.initial):
            raise ValueError("Require bounded evaluations starting with original")
        screen = (self.baseline if state["baseline"] is None else self.screen)(code,owner=owner)
        if screen["unknown_work"] is not False or screen["candidate_hash"] != candidate_hash(code):
            raise ValueError("Unreconciled screen")
        if state["baseline"] is None:
            if screen["status"] != "valid":
                raise ValueError("Original failed cheap correctness; no screening baseline")
            state["baseline"] = copy.deepcopy(screen)
        decision = "baseline" if code == self.initial else reason(screen,state["baseline"],self.policy)
        full = self.full(code,owner=owner) if decision in ("baseline","advance") else None
        if full is None:
            state["rejected"][candidate_hash(code)] = code
        result = combined(screen,full,decision)
        state["events"].append(copy.deepcopy(result))
        return result

    def finish(self):
        if self.frozen:
            raise ValueError("Rejection audits are one-use")
        self.frozen = True
        reports = {}
        # Freeze every owner's complete rejected population before the first audit.
        for owner,state in self.states.items():
            population = sorted(state["rejected"])
            reports[owner] = dict(population=population,selected=[],audits=[])
        for owner,identity in selected_population({o:r["population"] for o,r in reports.items()},self.seed,self.policy["audit_candidates"]):
            reports[owner]["selected"].append(identity)
        self.reports = reports  # partial evidence survives an interrupted audit
        for owner,row in reports.items():
            for identity in row["selected"]:
                seed = int(digest([self.seed,owner,identity])[:8],16)
                values,evidence = confirm(self.initial,self.states[owner]["rejected"][identity],self.auditor,
                                           self.audit_policy,seed=seed,owner=owner)
                row["audits"].append(dict(candidate_hash=identity,values=values,noise=evidence,
                                          classification=classification(values,self.audit_policy)))
            row["summary"] = audit_summary(row["population"],row["audits"],self.tracks)
        return copy.deepcopy(reports)


def audit_receipts(cell):
    for row in cell.get("screening",{}).values():
        for audit in row["audits"]:
            yield from audit["values"].values()


def validate_screening(report):
    plan = report["plan"]
    validate_registration(plan)
    frozen = plan["screening_policy"]
    validate_policy(frozen)
    tracks = sum(len(c["tracks"]) for c in plan["grid"])
    ap = audit_policy(tracks,frozen)
    journal = report["accounting"]["rows"]
    for index,cell in enumerate(report["rows"]):
        if not frozen["enabled"]:
            if cell.get("screening"):
                raise ValueError("Unexpected screening evidence")
            continue
        owners = {f"cell-{index:04d}/{t['mode']}/{t['method']}" for t in cell["search_runs"]}
        if set(cell["screening"]) != owners:
            raise ValueError("Missing screening owner")
        global_selection = selected_population({o:r["population"] for o,r in cell["screening"].items()},
                                               plan["screening_seed"],frozen["audit_candidates"])
        manifest = cell["screening_manifest"]
        if manifest["policy"] != frozen or manifest["identity"] != digest([manifest["screen"],manifest["full"],frozen]):
            raise ValueError("Changed screening evaluator policy")
        if manifest["baseline"] != dict(manifest["screen"],samples=frozen["baseline_samples"]):
            raise ValueError("Changed repeated screening baseline")
        for track in cell["search_runs"]:
            owner = f"cell-{index:04d}/{track['mode']}/{track['method']}"
            results = [r for r in track["receipts"] if r["operation"] == "evaluate" and r["status"] == "completed"]
            if not results:
                raise ValueError("Missing screen baseline")
            initial = results[0]["request"]["code"]
            baseline = results[0]["result"]["screening"]["screen"]
            if baseline["status"] != "valid":
                raise ValueError("Invalid screening baseline")
            rejected = []
            for event in results:
                code,receipt = event["request"]["code"],event["result"]
                detail = receipt["screening"]
                decision = "baseline" if code == initial else reason(detail["screen"],baseline,frozen)
                if (receipt != combined(detail["screen"],detail["full"],decision)
                        or (detail["full"] is not None) != (decision in ("baseline","advance"))):
                    raise ValueError("Screen decision or full-fidelity score changed")
                for part,stage in ((detail["screen"],"screen"),(detail["full"],"search")):
                    if part is not None:
                        binding = manifest[("baseline" if event is results[0] else "screen") if stage == "screen" else "full"]
                        if (part["candidate_hash"] != candidate_hash(code) or part["unknown_work"] is not False
                                or part["phase"] != "search" or part["input_sha256"] != binding["input_sha256"]
                                or part["evaluator_sha256"] != digest(binding)
                                or not part["sample_ids"] or len(part["sample_ids"]) != len(part["budget_operation_ids"])
                                or any(journal[i]["stage"] != stage or journal[i]["owner"] != owner for i in part["budget_operation_ids"])):
                            raise ValueError("Screen/full receipt ownership differs")
                        if part["status"] == "valid" and (len(part["samples"]) != binding["samples"]
                                or len(part["sample_ids"]) != binding["samples"]
                                or part["duration_seconds"] != statistics.median(part["samples"])
                                or part["quality"] != 1/part["duration_seconds"]):
                            raise ValueError("Screen/full measurements differ from reported fitness")
                if detail["full"] is None:
                    rejected.append(candidate_hash(code))
            row = cell["screening"][owner]
            population = sorted(set(rejected))
            selected = [identity for selected_owner,identity in global_selection if selected_owner == owner]
            if row["population"] != population or row["selected"] != selected or [a["candidate_hash"] for a in row["audits"]] != selected:
                raise ValueError("Changed rejection population or audit selection")
            for audit in row["audits"]:
                values = audit["values"]
                evidence = audit["noise"]
                binding = dict(single_sample_evaluator=manifest["auditor"],noise_policy=ap)
                if (audit["classification"] != classification(values,ap) or evidence["policy"] != ap
                        or evidence["evaluator_manifest"] != binding or evidence["evaluator_sha256"] != digest(binding)
                        or any(evidence[k] != v for k,v in assess(values,ap).items())
                        or values["selected"]["candidate_hash"] != audit["candidate_hash"]
                        or values["original"]["candidate_hash"] != candidate_hash(initial)):
                    raise ValueError("Changed rejection audit classification")
                rng = random.Random(int(digest([plan["screening_seed"],owner,audit["candidate_hash"]])[:8],16))
                orders = []
                for sample in range(frozen["audit_pairs"]):
                    order = ["original","selected"]
                    rng.shuffle(order)
                    orders.append(order)
                    if values[order[0]]["budget_operation_ids"][sample] >= values[order[1]]["budget_operation_ids"][sample]:
                        raise ValueError("Rejection audit dispatch order differs")
                if evidence["orders"] != orders:
                    raise ValueError("Rejection audit randomization differs")
                for part in values.values():
                    if (len(part["sample_ids"]) != frozen["audit_pairs"] or part["unknown_work"] is not False
                            or part["phase"] != "confirmation" or part["evaluator_sha256"] != digest(binding)
                            or part["input_sha256"] != manifest["full"]["input_sha256"]
                            or (part["status"] == "valid" and part["duration_seconds"] != statistics.median(part["samples"]))
                            or any(journal[i]["stage"] != "rejection-audit" or journal[i]["owner"] != owner for i in part["budget_operation_ids"])):
                        raise ValueError("Missing charged full rejection audit")
            if row["summary"] != audit_summary(population,row["audits"],tracks):
                raise ValueError("Changed false-rejection estimate")
