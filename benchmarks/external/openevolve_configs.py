"""OpenEvolve's two comparison configurations, built only from declared rules.

RECOMMENDED is OpenEvolve's own shipped example config, verbatim, with only the model
names swapped for the campaign's Claude models through TIERS. MATCHED mirrors the feature
set our engine arm actually runs (RIG_ENGINE). Both then receive the same FORCED
overrides, which exist because every arm shares one sequential, metered broker. Every
change from the source is returned as a record, so a report can show exactly how each
config differs from what OpenEvolve ships, and from our arm.
"""
from __future__ import annotations

import copy
from pathlib import Path

import yaml

# Provider model -> Claude alias, by capability tier: fast/small models -> haiku,
# flagship/reasoning models -> opus, Claude models keep their own tier. A name that is not
# listed is refused, so a new example cannot silently reach an undeclared mapping.
TIERS = {
    "google/gemini-2.5-flash": "haiku", "gemini-2.5-flash": "haiku",
    "google/gemini-2.5-flash-lite": "haiku", "gemini-2.5-flash-lite": "haiku",
    "gemini-2.5-flash-lite-preview-06-17": "haiku", "gemini-flash-lite-latest": "haiku",
    "google/gemini-2.0-flash-001": "haiku", "gemini-2.0-flash": "haiku",
    "gpt-4.1-nano": "haiku", "qwen/qwen3-8b": "haiku",
    "google/gemini-2.5-pro": "opus", "gemini-3-pro-preview": "opus",
    "gpt-4": "opus", "gpt-4o": "opus", "o3": "opus", "deepseek-reasoner": "opus",
    "claude-sonnet-4-5-20250929": "sonnet", "sonnet": "sonnet", "haiku": "haiku", "opus": "opus",
}

# (path, value, reason). Applied identically to both configs.
FORCED = (
    (("llm", "retries"), 0, "A failed call closes broker admission; a retry would be work the budget did not admit"),
    (("llm", "timeout"), 300, "Matches the transport timeout, so OpenEvolve never abandons a call the broker still meters"),
    (("evaluator", "parallel_evaluations"), 1, "The broker admits one evaluation at a time for every arm"),
    (("evaluator", "max_retries"), 0, "A retried evaluation would be unmetered work"),
    (("evaluator", "cascade_evaluation"), False, "The broker exposes one-stage evaluation to every arm"),
    (("evaluator", "use_llm_feedback"), False, "LLM feedback is extra model work our arm does not receive"),
    (("language",), "python", "The reference worker language for every arm"),
    (("checkpoint_interval",), 1, "Checkpoint every iteration so a killed run resumes losing at most the in-flight "
                                  "iteration; saving a checkpoint does not change the search"),
)
# Declared, not applied: true of both systems because they share the transport.
TRANSPORT_LIMITS = ("Sampling fields (temperature, top_p, max_tokens, seed) reach the shim and are recorded but not "
                    "honoured: the subscription CLI exposes none, so both systems sample at the CLI default",)

# The feature set of our engine arm in benchmarks/EvolutionComparison/Program.cs.
# test_openevolve_configs asserts these against the literal assignments there.
RIG_ENGINE = dict(archive_descriptor="length", archive_bins=64, archive_range=(0, 65536), islands=1,
                  migration_interval=0, inspirations=3, full_rewrite=True, selection="uniform")


def _set(config, path, value, changes, reason, source):
    node = config
    for key in path[:-1]:
        node = node.setdefault(key, {})
    before = node.get(path[-1], "<absent>")
    if before != value:
        changes.append(dict(path=".".join(path), source=source, before=before, after=value, reason=reason))
    node[path[-1]] = value


def _swap_models(config, changes):
    llm = config.setdefault("llm", {})
    for key in ("models", "evaluator_models"):
        for index, model in enumerate(llm.get(key) or []):
            _swap(model, "name", f"llm.{key}[{index}].name", changes)
    for key in ("primary_model", "secondary_model"):
        if llm.get(key) is not None:
            _swap(llm, key, f"llm.{key}", changes)
    if not llm.get("models") and not llm.get("primary_model"):
        raise ValueError("Recommended config declares no models to swap")


def _swap(node, key, label, changes):
    if node[key] not in TIERS:
        raise ValueError(f"Model {node[key]!r} has no declared Claude tier")
    changes.append(dict(path=label, source="model-swap", before=node[key], after=TIERS[node[key]],
                        reason="The only permitted change to a recommended config"))
    node[key] = TIERS[node[key]]


def _finish(config, changes, iterations, seed):
    if type(iterations) is not int or not 1 <= iterations <= 100_000 or type(seed) is not int or not 0 <= seed < 2**32:
        raise ValueError("Invalid campaign budget or seed")
    _set(config, ("max_iterations",), iterations, changes, "Campaign budget, identical for every arm", "budget")
    _set(config, ("random_seed",), seed, changes, "Paired seed, identical for every arm", "budget")
    for path, value, reason in FORCED:
        _set(config, path, value, changes, reason, "forced")
    config.setdefault("llm", {}).pop("api_key", None)
    config["llm"].pop("api_base", None)
    changes.append(dict(path="llm.api_base/api_key", source="forced", before="<provider>", after="<loopback shim>",
                        reason="Every call goes through the shared metered transport"))
    models = [model["name"] for model in config["llm"].get("models") or []] or [config["llm"]["primary_model"]]
    return dict(config=config, changes=changes, models=sorted(set(models)), transport_limits=list(TRANSPORT_LIMITS))


def recommended(example, *, iterations, seed):
    """OpenEvolve's shipped config (a path or parsed dict), model names swapped, forced overrides applied."""
    source = yaml.safe_load(Path(example).read_text(encoding="utf-8")) if isinstance(example, (str, Path)) else example
    if not isinstance(source, dict):
        raise ValueError("Example config is not a mapping")
    config, changes = copy.deepcopy(source), []
    _swap_models(config, changes)
    record = _finish(config, changes, iterations, seed)
    record.update(kind="recommended", source=str(example) if isinstance(example, (str, Path)) else "<dict>")
    return record


def matched(models, system_message, *, iterations, seed, engine=None):
    """OpenEvolve configured with our arm's features; each non-equivalence is declared."""
    engine = dict(RIG_ENGINE if engine is None else engine)
    if not isinstance(models, dict) or not models or any(name not in TIERS.values() or not 0 < weight <= 1
                                                        for name, weight in models.items()):
        raise ValueError("Matched models must be Claude aliases with weights in (0, 1]")
    unknown = set(engine) - set(RIG_ENGINE)
    if unknown:
        raise ValueError(f"Engine features without a declared OpenEvolve mapping: {sorted(unknown)}")
    changes, differences = [], []
    config = {"llm": {"models": [{"name": name, "weight": weight} for name, weight in sorted(models.items())]},
              "diff_based_evolution": not engine["full_rewrite"], "max_code_length": engine["archive_range"][1],
              "database": {"num_islands": engine["islands"], "feature_dimensions": ["complexity"],
                           "feature_bins": engine["archive_bins"]},
              "prompt": {"system_message": system_message, "num_top_programs": 0,
                         "num_diverse_programs": engine["inspirations"], "use_template_stochasticity": False}}
    if engine["migration_interval"] == 0:
        # OpenEvolve has no "never"; an interval beyond the budget is the same behaviour.
        config["database"]["migration_interval"] = iterations + 1
    else:
        config["database"]["migration_interval"] = engine["migration_interval"]
    if engine["selection"] == "uniform":
        config["database"].update(exploration_ratio=1.0, exploitation_ratio=0.0, elite_selection_ratio=0.0)
    elif engine["selection"] == "best":
        config["database"].update(exploration_ratio=0.0, exploitation_ratio=0.0, elite_selection_ratio=1.0)
    else:
        raise ValueError(f"Selection {engine['selection']!r} has no declared OpenEvolve mapping")
    differences.append(f"Archive: ours bins {engine['archive_descriptor']} over fixed {engine['archive_range']} into "
                       f"{engine['archive_bins']}; OpenEvolve bins 'complexity' (code length) over the observed range")
    differences.append("Inspirations: ours samples from the archive; OpenEvolve's diverse programs come from its island")
    differences.append("Prompt wording: each system renders its own template around the same task and parent")
    record = _finish(config, changes, iterations, seed)
    record.update(kind="matched", engine=engine, differences=differences)
    return record