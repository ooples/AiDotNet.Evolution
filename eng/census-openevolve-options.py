"""Counts OpenEvolve's declared options per area, so a parity claim can be checked against them.

    python eng/census-openevolve-options.py <openevolve-checkout> [--docs docs/parity]

V1-19 (#124) cannot ship until every parity story's metrics table carries a measured value, and the
parity stories count "OpenEvolve <area> options with an equivalent + test". That number has to come
from OpenEvolve's own config, not from memory: when the docs and the story targets were compared for
the first time, four of six disagreed - in both directions - because they were counted against
different inventories.

This reads the pinned checkout's dataclasses and prints, per area, the upstream option count beside
the parity doc's option-row count. Private fields (leading underscore) and nested config objects
(`llm`, `prompt`, `database`, `evaluator`) are excluded: the first are not options and the second are
the areas themselves.
"""
import argparse
import pathlib
import re

AREAS = {
    "models": ("LLMModelConfig", "OPENEVOLVE_MODELS.md"),
    "prompts": ("PromptConfig", "OPENEVOLVE_PROMPTS.md"),
    "archive": ("DatabaseConfig", "OPENEVOLVE_ARCHIVE.md"),
    "run-controls": ("Config", "OPENEVOLVE_RUN_CONTROLS.md"),
    "evaluator": ("EvaluatorConfig", None),
}
# Artifacts are not one class: OpenEvolve spreads them across three.
ARTIFACT_FIELDS = {
    "PromptConfig": ("include_artifacts", "max_artifact_bytes", "artifact_security_filter"),
    "DatabaseConfig": ("artifacts_base_path", "artifact_size_threshold", "cleanup_old_artifacts",
                       "artifact_retention_days", "max_snapshot_artifacts"),
    "EvaluatorConfig": ("enable_artifacts", "max_artifact_storage"),
}
NESTED = {"llm", "prompt", "database", "evaluator"}
FIELD = re.compile(r"^    (\w+)\s*:\s*[^=\n]+", re.M)


def fields(source, class_name):
    """The option names a dataclass declares, excluding privates and nested config objects."""
    parts = re.split(r"\n@dataclass\s*\nclass\s+(\w+)", source)
    for index in range(1, len(parts), 2):
        if parts[index] != class_name:
            continue
        body = parts[index + 1].split("\n@dataclass")[0]
        return [name for name in (match.group(1) for match in FIELD.finditer(body))
                if not name.startswith("_") and name not in NESTED]
    raise KeyError("No dataclass named " + class_name)


def doc_rows(path):
    """Rows whose first cell is a backticked OpenEvolve option, which is what the docs enumerate."""
    if path is None or not path.is_file():
        return None
    return sum(1 for line in path.read_text(encoding="utf-8").split("\n") if re.match(r"^\|\s*`", line))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("checkout")
    parser.add_argument("--docs", default="docs/parity")
    arguments = parser.parse_args()
    source = (pathlib.Path(arguments.checkout) / "openevolve" / "config.py").read_text(encoding="utf-8")
    docs = pathlib.Path(arguments.docs)

    print("%-14s %-18s %8s %9s" % ("area", "upstream class", "upstream", "doc rows"))
    for area, (class_name, doc) in AREAS.items():
        upstream = fields(source, class_name)
        rows = doc_rows(docs / doc) if doc else None
        print("%-14s %-18s %8d %9s" % (area, class_name, len(upstream),
                                       "-" if rows is None else rows))
    artifacts = sum(len(names) for names in ARTIFACT_FIELDS.values())
    print("%-14s %-18s %8d %9s" % ("artifacts", "three classes", artifacts,
                                   doc_rows(docs / "OPENEVOLVE_ARTIFACTS.md")))


if __name__ == "__main__":
    main()
