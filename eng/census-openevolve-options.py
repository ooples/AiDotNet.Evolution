"""Diffs OpenEvolve's own config against the catalog that claims to classify every key of it.

    python eng/census-openevolve-options.py <openevolve-checkout>

`OpenEvolveConfigCatalog` says it carries every key of OpenEvolve 0.3.2's `Config`, and
`OpenEvolveImportTests.Every_openevolve_config_key_is_classified_exactly_once` pins the catalog to a
**hand-transcribed** list of 103 names. That guards the catalog against drifting from the list; it
cannot guard the list against drifting from OpenEvolve, because nothing reads the checkout. Bump the
pinned ref and a new upstream option would satisfy every test in the repository while being refused
at runtime for no stated reason.

So this derives the key set from the checkout and diffs it against the catalog, which is the half
that was missing:

- an upstream key the catalog does not classify is a parity gap, and the reason to re-run this after
  bumping `411fb59`;
- a catalog key upstream does not expose is either a removed option or - as with
  `llm._manual_queue_dir` - a private field counted as one.

Nested config objects are expanded the way the catalog names them (`llm.`, `prompt.`, `database.`,
`evaluator.`, `evolution_trace.`), and per-model options are reached through `llm.` because that is
where a run file sets them.
"""
import argparse
import pathlib
import re
import sys

FIELD = re.compile(r"^    (?!else\b|elif\b|return\b|if\b)([a-z_][a-z0-9_]*)\s*:\s*[A-Za-z_\[\"]", re.M)
NESTED = {
    "llm": "LLMConfig",
    "prompt": "PromptConfig",
    "database": "DatabaseConfig",
    "evaluator": "EvaluatorConfig",
    "evolution_trace": "EvolutionTraceConfig",
}
CATALOG = pathlib.Path("src/AiDotNet.Evolution.Cli/OpenEvolve/OpenEvolveConfigCatalog.cs")


def dataclass_fields(source, name):
    """The field names one dataclass declares, or None when there is no such class."""
    parts = re.split(r"\n@dataclass\s*\nclass\s+(\w+)", source)
    for index in range(1, len(parts), 2):
        if parts[index] == name:
            body = parts[index + 1].split("\n@dataclass")[0]
            return [match.group(1) for match in FIELD.finditer(body)]
    return None


def upstream_keys(source):
    """Every option a run file can set, dotted as the catalog names them."""
    keys = set()
    for field in dataclass_fields(source, "Config") or []:
        nested = NESTED.get(field)
        children = dataclass_fields(source, nested) if nested else None
        if children is None:
            keys.add(field)
            continue
        keys.update(field + "." + child for child in children)
    # A model's own options are set under `llm` in a run file, so that is where they are counted.
    for field in dataclass_fields(source, "LLMModelConfig") or []:
        if not field.startswith("_"):
            keys.add("llm." + field)
    return keys


def catalog_keys(path):
    text = path.read_text(encoding="utf-8")
    return set(re.findall(r'new\("([^"]+)",\s*OpenEvolveKeyDisposition', text))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("checkout")
    parser.add_argument("--catalog", default=str(CATALOG))
    arguments = parser.parse_args()
    source = (pathlib.Path(arguments.checkout) / "openevolve" / "config.py").read_text(encoding="utf-8")
    upstream, catalog = upstream_keys(source), catalog_keys(pathlib.Path(arguments.catalog))

    unclassified = sorted(upstream - catalog)
    extra = sorted(catalog - upstream)
    print("upstream options: %d   catalog keys: %d" % (len(upstream), len(catalog)))
    print("unclassified upstream options: %d%s"
          % (len(unclassified), (" -> " + ", ".join(unclassified)) if unclassified else ""))
    print("catalog keys not in upstream:  %d%s"
          % (len(extra), (" -> " + ", ".join(extra)) if extra else ""))
    # An unclassified upstream option is the failure this exists to catch. A catalog key upstream no
    # longer exposes is reported but not fatal: a private field counted as an option is untidy, not
    # a parity gap, and refusing it would make the tidy-up a breaking change.
    return 1 if unclassified else 0


if __name__ == "__main__":
    sys.exit(main())
