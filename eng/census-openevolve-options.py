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
import hashlib
import json
import pathlib
import re
import subprocess
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


# A parse that finds nothing must not read as "nothing to classify". An empty or renamed
# `config.py`, or a decorator style this regex does not match, would otherwise make the upstream set
# empty - and an empty set has no unclassified members, so the census would exit 0 having measured
# nothing at all (review caught it). Every class it depends on is required by name, and the derived
# surface has to clear a floor: 411fb59 declares 102 options, and a parse that found a tenth of that
# is a broken parse rather than a shrunken OpenEvolve.
REQUIRED_CLASSES = ("Config", "LLMModelConfig", *NESTED.values())
MINIMUM_OPTIONS = 50


def upstream_keys(source):
    """Every option a run file can set, dotted as the catalog names them. Refuses a parse that found nothing."""
    for name in REQUIRED_CLASSES:
        fields = dataclass_fields(source, name)
        if not fields:
            raise ValueError(
                "The checkout's openevolve/config.py declares no readable `%s` dataclass, so the "
                "upstream option set cannot be derived. Check the ref and the decorator style before "
                "reading any count from this." % name)
    keys = set()
    for field in dataclass_fields(source, "Config"):
        nested = NESTED.get(field)
        children = dataclass_fields(source, nested) if nested else None
        if children is None:
            keys.add(field)
            continue
        keys.update(field + "." + child for child in children)
    # A model's own options are set under `llm` in a run file, so that is where they are counted.
    for field in dataclass_fields(source, "LLMModelConfig"):
        if not field.startswith("_"):
            keys.add("llm." + field)
    if len(keys) < MINIMUM_OPTIONS:
        raise ValueError(
            "Derived only %d upstream options, below the %d floor: this is a parse that went wrong, "
            "not an OpenEvolve that shrank." % (len(keys), MINIMUM_OPTIONS))
    return keys


def catalog_keys(path):
    text = path.read_text(encoding="utf-8")
    keys = set(re.findall(r'new\("([^"]+)",\s*OpenEvolveKeyDisposition', text))
    # The same trap on the other side: a catalog this regex cannot read would make every upstream
    # option look unclassified, which is loud, or - paired with an empty upstream - silent.
    if len(keys) < MINIMUM_OPTIONS:
        raise ValueError(
            "Read only %d keys from %s, below the %d floor: the catalog's shape changed and this "
            "regex no longer sees it." % (len(keys), path, MINIMUM_OPTIONS))
    return keys


def checkout_ref(checkout):
    """Which OpenEvolve this was parsed from. A census that cannot say is worth little later."""
    try:
        described = subprocess.run(["git", "-C", str(checkout), "describe", "--tags", "--always"],
                                   capture_output=True, text=True, timeout=30, check=False)
        return described.stdout.strip() or None
    except (OSError, subprocess.SubprocessError):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("checkout")
    parser.add_argument("--catalog", default=str(CATALOG))
    # The census needs a checkout, so nothing offline - a test, a release report - can re-derive its
    # numbers. Writing the result out makes the derived set itself the artifact: whoever reads a
    # parity claim gets the key lists it was computed from, not a count somebody typed.
    parser.add_argument("--output", help="write the derived key sets to this file as JSON")
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
    if arguments.output:
        with open(arguments.output, "x", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps({
                "Schema": "openevolve-config-census-v1",
                "Checkout": pathlib.Path(arguments.checkout).name,
                "Ref": checkout_ref(arguments.checkout),
                "ConfigSha256": hashlib.sha256(source.encode("utf-8")).hexdigest(),
                "Catalog": arguments.catalog.replace(chr(92), "/"),
                "UpstreamOptions": sorted(upstream),
                "CatalogKeys": sorted(catalog),
                "Unclassified": unclassified,
                "CatalogNotUpstream": extra,
            }, indent=1) + chr(10))
        print("wrote " + arguments.output)

    # An unclassified upstream option is the failure this exists to catch. A catalog key upstream no
    # longer exposes is reported but not fatal: a private field counted as an option is untidy, not
    # a parity gap, and refusing it would make the tidy-up a breaking change.
    return 1 if unclassified else 0


if __name__ == "__main__":
    sys.exit(main())
