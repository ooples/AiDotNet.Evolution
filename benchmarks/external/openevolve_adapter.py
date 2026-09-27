"""Run the pinned, unmodified OpenEvolve controller against the shared local broker."""
from __future__ import annotations

import argparse
import asyncio
import json
import os
from pathlib import Path
import subprocess

from program_broker import request
from program_controls import CONTROLLED_SYSTEM, CONTROLLED_USER

REVISION = "411fb59c886c18704caaffb611e17cf9e7d824d2"


class BrokerModel:
    def __init__(self, configuration):
        self.model = configuration.name

    async def generate(self, prompt, **kwargs):
        return await self.generate_with_context(kwargs.get("system_message", ""), [{"role": "user", "content": prompt}])

    async def generate_with_context(self, system_message, messages, **kwargs):
        return request(os.environ["EVOLUTION_BROKER_ENDPOINT"], os.environ["EVOLUTION_BROKER_CAPABILITY"],
                       "model", {"system": system_message, "messages": messages})


def init_broker_model(configuration):
    # A module-level callable survives upstream's Windows spawn/config serialization.
    return BrokerModel(configuration)


def evaluate(program_path):
    with Path(program_path).open("r", encoding="utf-8") as source:
        code = source.read(64 * 1024 + 1)
    result = request(os.environ["EVOLUTION_BROKER_ENDPOINT"], os.environ["EVOLUTION_BROKER_CAPABILITY"],
                     "evaluate", {"code": code})
    return {"combined_score": result["quality"] if result["status"] == "valid" else -1e300}


NATIVE_MODES = ("recommended", "matched")


def latest_checkpoint(output):
    """(path, iteration) of the newest COMPLETE OpenEvolve checkpoint, or None.

    Upstream writes a checkpoint non-atomically (program files, then metadata.json last), so
    a kill can leave the newest one torn; resuming from it silently loads 0 programs and
    "succeeds" with nothing evolved. A checkpoint counts only if its metadata parses, names
    its own iteration, and every program it references has a readable file.
    """
    saved = sorted(Path(output, "checkpoints").glob("checkpoint_*"), key=lambda p: int(p.name.split("_")[1]), reverse=True)
    for path in saved:
        iteration = int(path.name.split("_")[1])
        try:
            metadata = json.loads((path / "metadata.json").read_text(encoding="utf-8"))
            ids = {program for island in metadata["islands"] for program in island} | set(metadata["archive"])
            if metadata["last_iteration"] != iteration or not ids:
                continue
            for program in ids:
                json.loads((path / "programs" / f"{program}.json").read_text(encoding="utf-8"))
        except (OSError, ValueError, KeyError, TypeError):
            continue
        return str(path), iteration
    return None


def native_config(record, endpoint, capability):
    """A built config record as an upstream Config whose native OpenAI client calls the broker shim."""
    from openevolve import Config
    if record.get("kind") not in NATIVE_MODES or not isinstance(record.get("config"), dict):
        raise ValueError("Not a built OpenEvolve comparison config")
    data = json.loads(json.dumps(record["config"]))
    data["llm"] = dict(data.get("llm") or {}, api_base=endpoint + "/v1", api_key=capability)
    config = Config.from_dict(data)
    models = config.llm.models + config.llm.evaluator_models
    if any(m.init_client is not None or m.api_base != endpoint + "/v1" or m.retries != 0 for m in models):
        raise ValueError("A model escaped the broker shim")
    return config


async def run(upstream, initial, output, model, iterations, seed, task, mode, selection_profile="default", config_record=None,
              resume=False):
    upstream = Path(upstream).resolve(strict=True)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=upstream, text=True).strip()
    changes = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=upstream, text=True)
    if actual != REVISION or changes.strip():
        raise ValueError("OpenEvolve checkout is not the exact clean pinned source")
    if (not 1 <= iterations <= 64 or not 0 <= seed < 2**32 or mode not in ("controlled", "native-bounded", *NATIVE_MODES) or
            selection_profile not in ("default", "best") or (mode in NATIVE_MODES) != (config_record is not None)
            or (resume and mode not in NATIVE_MODES)):
        raise ValueError("Invalid bounded OpenEvolve run")
    import openevolve
    from openevolve import Config, OpenEvolve
    from openevolve.config import LLMConfig, LLMModelConfig
    if Path(openevolve.__file__).resolve().parent.parent != upstream:
        raise ValueError("Installed OpenEvolve is not the declared source checkout")
    destination = Path(output)
    destination.mkdir(parents=True, exist_ok=resume)
    if mode in NATIVE_MODES:
        record = json.loads(Path(config_record).read_text(encoding="utf-8"))
        config = native_config(record, os.environ["EVOLUTION_BROKER_ENDPOINT"], os.environ["EVOLUTION_BROKER_CAPABILITY"])
        checkpoint = None
        if resume:
            # OpenEvolve's own checkpoint (saved every iteration); `iterations` is what remains.
            found = latest_checkpoint(destination)
            if found is None:
                raise ValueError("Nothing to resume: no complete OpenEvolve checkpoint was saved")
            checkpoint = found[0]
            if not 1 <= iterations <= config.max_iterations or config.random_seed != seed:
                raise ValueError("Resumed budget exceeds the declared run")
        elif config.max_iterations != iterations or config.random_seed != seed:
            raise ValueError("Config budget differs from the declared run")
        engine = OpenEvolve(str(initial), str(Path(__file__).resolve()), config, str(destination))
        best = await engine.run(iterations=iterations, checkpoint_path=checkpoint)
        result = dict(schema="openevolve-broker-run-v1", revision=actual, mode=mode, iterations=iterations, seed=seed,
                      models=record["models"], changes=record["changes"], differences=record.get("differences", []),
                      transport_limits=record["transport_limits"],
                      best=None if best is None else {"code": best.code, "metrics": best.metrics})
        (destination / "adapter-result.json").write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
        return result
    config = Config()
    if selection_profile == "best":
        config.database.exploration_ratio = 0.0
        config.database.exploitation_ratio = 0.0
        config.database.elite_selection_ratio = 1.0
    config.random_seed = seed
    config.max_iterations = iterations
    config.language = "python"
    config.diff_based_evolution = False
    config.max_code_length = 65536
    with Path(task).open(encoding="utf-8") as description_file:
        description = description_file.read(16385)
    if len(description.encode()) > 16384:
        raise ValueError("Task description exceeds 16 KiB")
    if mode == "controlled":
        templates = destination / "controlled-templates"
        templates.mkdir()
        (templates / "full_rewrite_user.txt").write_text(CONTROLLED_USER, encoding="utf-8")
        config.prompt.template_dir = str(templates.resolve())
        config.prompt.system_message = CONTROLLED_SYSTEM + "\nTask:\n" + description
        config.prompt.use_template_stochasticity = False
        config.prompt.num_top_programs = 0
        config.prompt.num_diverse_programs = 0
    else:
        native_system = (upstream / "openevolve/prompts/defaults/system_message.txt").read_text(encoding="utf-8")
        config.prompt.system_message = native_system + "\nTask:\n" + description
    config.llm = LLMConfig(models=[LLMModelConfig(name=model, init_client=init_broker_model)],
                           api_base=os.environ["EVOLUTION_BROKER_ENDPOINT"] + "/disabled-provider-api",
                           api_key="unused-local-broker", retries=0, timeout=300)
    config.evaluator.parallel_evaluations = 1
    config.evaluator.max_retries = 0
    config.evaluator.timeout = 300
    config.evaluator.cascade_evaluation = False
    config.evaluator.use_llm_feedback = False
    engine = OpenEvolve(str(initial), str(Path(__file__).resolve()), config, str(destination))
    best = await engine.run(iterations=iterations)
    result = dict(schema="openevolve-broker-run-v1", revision=actual, mode=mode,
                  model=model, iterations=iterations, seed=seed, selection_profile=selection_profile,
                  best=None if best is None else {"code": best.code, "metrics": best.metrics},
                  overrides={"diff_based_evolution": False, "parallel_evaluations": 1, "evaluator_retries": 0,
                             "llm_retries": 0, "cascade_evaluation": False, "llm_feedback": False},
                  limitations=["Native prompts/database defaults with disclosed resource and full-program overrides",
                               "Broker receipts, not upstream iteration count, determine actual work and success"])
    (destination / "adapter-result.json").write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument("--initial", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--iterations", type=int, required=True)
    parser.add_argument("--seed", type=int, required=True)
    parser.add_argument("--task", type=Path, required=True)
    parser.add_argument("--mode", choices=("controlled", "native-bounded", *NATIVE_MODES), required=True)
    parser.add_argument("--selection-profile", choices=("default", "best"), default="default")
    parser.add_argument("--config-record", type=Path, help="A built recommended/matched record (native modes only)")
    parser.add_argument("--resume", action="store_true", help="Resume a native-mode run from its latest checkpoint")
    args = parser.parse_args()
    asyncio.run(run(args.upstream, args.initial, args.output, args.model, args.iterations, args.seed, args.task, args.mode,
                    args.selection_profile, args.config_record, args.resume))


if __name__ == "__main__":
    main()
