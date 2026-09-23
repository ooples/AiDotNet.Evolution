"""Config-builder contracts. The upstream-example checks need EVOLUTION_OPENEVOLVE_CHECKOUT."""
import copy
import os
from pathlib import Path
import re
import unittest

import yaml

import openevolve_configs as oc

FAMILIES = ("algotune", "alphaevolve_math_problems")


def undo(config, changes):
    """Reapply each recorded `before` value; a verbatim build must then equal its source."""
    restored = copy.deepcopy(config)
    for change in reversed(changes):
        if change["path"] == "llm.api_base/api_key":
            continue
        match = re.fullmatch(r"llm\.(models|evaluator_models)\[(\d+)\]\.name", change["path"])
        if match:
            restored["llm"][match.group(1)][int(match.group(2))]["name"] = change["before"]
            continue
        node, keys = restored, change["path"].split(".")
        for key in keys[:-1]:
            node = node[key]
        if change["before"] == "<absent>":
            node.pop(keys[-1])
        else:
            node[keys[-1]] = change["before"]
    return restored


class RecommendedTests(unittest.TestCase):
    def examples(self):
        upstream = os.environ.get("EVOLUTION_OPENEVOLVE_CHECKOUT")
        if not upstream:
            self.skipTest("Set EVOLUTION_OPENEVOLVE_CHECKOUT to the pinned checkout")
        return [path for family in FAMILIES for path in sorted((Path(upstream) / "examples" / family).rglob("config.yaml"))]

    def test_every_family_example_is_verbatim_except_declared_changes(self):
        examples = self.examples()
        self.assertGreaterEqual(len(examples), 24)
        for path in examples:
            with self.subTest(example=path.name, parent=path.parent.name):
                source = yaml.safe_load(path.read_text(encoding="utf-8"))
                record = oc.recommended(path, iterations=8, seed=3)
                for key in ("api_base", "api_key"):
                    source["llm"].pop(key, None)
                self.assertEqual(source, undo(record["config"], record["changes"]))
                self.assertNotIn("api_key", record["config"]["llm"])
                self.assertTrue(set(record["models"]) <= {"haiku", "sonnet", "opus"})
                self.assertEqual({"model-swap", "budget", "forced"}, {c["source"] for c in record["changes"]})

    def test_undeclared_model_is_refused_and_source_is_not_mutated(self):
        source = {"llm": {"models": [{"name": "some-new-model", "weight": 1.0}]}}
        with self.assertRaisesRegex(ValueError, "no declared Claude tier"):
            oc.recommended(source, iterations=1, seed=0)
        self.assertEqual("some-new-model", source["llm"]["models"][0]["name"])
        with self.assertRaisesRegex(ValueError, "no models"):
            oc.recommended({"llm": {}}, iterations=1, seed=0)

    def test_primary_model_form_is_swapped(self):
        record = oc.recommended({"llm": {"primary_model": "gpt-4o", "primary_model_weight": 1.0}}, iterations=1, seed=0)
        self.assertEqual(("opus", ["opus"]), (record["config"]["llm"]["primary_model"], record["models"]))


class MatchedTests(unittest.TestCase):
    def test_rig_features_map_and_differences_are_declared(self):
        record = oc.matched({"haiku": 0.6, "opus": 0.4}, "Task", iterations=5, seed=1)
        database = record["config"]["database"]
        self.assertEqual((1, 64, 6, ["complexity"]), (database["num_islands"], database["feature_bins"],
                                                     database["migration_interval"], database["feature_dimensions"]))
        self.assertEqual((1.0, 0.0, 0.0), (database["exploration_ratio"], database["exploitation_ratio"],
                                           database["elite_selection_ratio"]))
        self.assertFalse(record["config"]["diff_based_evolution"])
        self.assertEqual(3, record["config"]["prompt"]["num_diverse_programs"])
        self.assertEqual(["haiku", "opus"], record["models"])
        self.assertGreaterEqual(len(record["differences"]), 3)

    def test_unmapped_features_and_models_are_refused(self):
        for models, engine in (({"gpt-4": 1.0}, None), ({"haiku": 0}, None),
                               ({"haiku": 1.0}, dict(oc.RIG_ENGINE, surrogate=True)),
                               ({"haiku": 1.0}, dict(oc.RIG_ENGINE, selection="tournament"))):
            with self.subTest(models=models, engine=engine), self.assertRaises(ValueError):
                oc.matched(models, "Task", iterations=1, seed=0, engine=engine)

    def test_rig_engine_matches_the_assignments_in_program_cs(self):
        source = (Path(__file__).parents[1] / "EvolutionComparison" / "Program.cs").read_text(encoding="utf-8")
        engine = oc.RIG_ENGINE
        for pattern in (rf"^\s*IslandCount = {engine['islands']},$", rf"^\s*InspirationCount = {engine['inspirations']},$",
                        rf"^\s*MigrationInterval = {engine['migration_interval']},$",
                        r'new EvolutionDescriptorDefinition\("length", 0, 65536, 64\)'):
            self.assertRegex(source, re.compile(pattern, re.MULTILINE))
        self.assertEqual(("length", 64, (0, 65536)), (engine["archive_descriptor"], engine["archive_bins"], engine["archive_range"]))


if __name__ == "__main__":
    unittest.main()