"""V1-72: one pinned-OpenEvolve run with a null LLM and evaluator at a fixed population size, in one fresh process.

Usage: python openevolve_scaling_run.py <upstream> <iterations> <population_size>
Evaluations are counted from per-process log files (concurrent appends to one shared file lose lines on Windows),
excluding the initial program's evaluation, which OpenEvolve performs outside the iterations.
"""
import asyncio
import json
import os
from pathlib import Path
import sys
import tempfile
import time

EVALUATOR = """
import os

def evaluate(program_path):
    with open(program_path, encoding="utf-8") as f:
        n = len(f.read())
    path = os.path.join(os.environ["OE_SCALING_EVALUATION_LOG"], str(os.getpid()) + ".log")
    with open(path, "a", encoding="utf-8") as log:
        log.write("1\\n")
    return {"combined_score": float(n % 97) / 97.0}
"""


class NullLLM:
    def __init__(self, configuration):
        self.model = configuration.name
        self.calls = 0

    async def generate(self, prompt, **kwargs):
        return await self.generate_with_context(kwargs.get("system_message", ""), [{"role": "user", "content": prompt}])

    async def generate_with_context(self, system_message, messages, **kwargs):
        self.calls += 1
        # The process id keeps proposals from different pool workers distinct.
        return f"```python\ndef solve(x):\n    v = {os.getpid()}_{self.calls}\n    return x + v\n```"


def init_null_llm(configuration):
    return NullLLM(configuration)


async def main(upstream, iterations, population):
    sys.path.insert(0, str(upstream))
    from openevolve import Config, OpenEvolve
    from openevolve.config import LLMConfig, LLMModelConfig
    work = Path(tempfile.mkdtemp(prefix="oe-scaling-"))
    (work / "initial.py").write_text("def solve(x):\n    return x + 1\n", encoding="utf-8")
    (work / "evaluator.py").write_text(EVALUATOR, encoding="utf-8")
    evaluation_log = work / "evaluations"
    evaluation_log.mkdir()
    os.environ["OE_SCALING_EVALUATION_LOG"] = str(evaluation_log)  # inherited by the worker pool
    config = Config()
    config.max_iterations = iterations
    config.random_seed = 42
    config.language = "python"
    config.diff_based_evolution = False
    config.checkpoint_interval = 10 ** 9
    config.log_level = "ERROR"
    config.database.num_islands = 1
    config.database.in_memory = True
    config.database.population_size = population
    config.database.archive_size = min(config.database.archive_size, population)
    config.evaluator.parallel_evaluations = 1
    config.evaluator.cascade_evaluation = False
    config.evaluator.max_retries = 0
    config.evaluator.timeout = 60
    config.llm = LLMConfig(models=[LLMModelConfig(name="null", init_client=init_null_llm)], retries=0, timeout=60,
                           api_base="http://127.0.0.1:9/unused", api_key="unused")
    engine = OpenEvolve(str(work / "initial.py"), str(work / "evaluator.py"), config, str(work / "out"))
    started = time.perf_counter()
    await engine.run(iterations=iterations)
    seconds = time.perf_counter() - started
    evaluations = sum(len(log.read_text(encoding="utf-8").splitlines()) for log in evaluation_log.glob("*.log")) - 1
    print(json.dumps(dict(System="openevolve", Iterations=iterations, Population=population, Evaluations=evaluations,
                          Programs=len(engine.database.programs), Seconds=seconds)))


if __name__ == "__main__":
    import logging
    logging.disable(logging.CRITICAL)
    try:
        asyncio.run(main(Path(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])))
    finally:
        # OpenEvolve can leave pool workers running after run() returns; stop them while they are still our children.
        import psutil
        workers = psutil.Process().children(recursive=True)
        for worker in workers:
            try:
                worker.kill()
            except psutil.NoSuchProcess:
                pass
        psutil.wait_procs(workers, timeout=30)
