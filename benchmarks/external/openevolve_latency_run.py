"""V1-75: OpenEvolve's in-flight utilisation against a slow model, the same simulation as benchmarks/EvolutionLatency.

The LLM sleeps a log-normal latency (median 100 ms, p95 400 ms, capped at 2 s: the story's 10 s / 40 s scaled down
100x) and the evaluator is constant-cost arithmetic, so wall-clock is bounded by the model when the controller keeps
its workers busy. Each worker logs how long its model calls took; utilisation is the summed call time divided by
wall-clock times parallel_evaluations, and ideal is the summed call time divided by parallel_evaluations.
Usage: python openevolve_latency_run.py <upstream> <iterations> <workers>
"""
import asyncio
import json
import math
import os
from pathlib import Path
import random
import sys
import tempfile
import time

EVALUATOR = """
def evaluate(program_path):
    with open(program_path, encoding="utf-8") as f:
        n = len(f.read())
    return {"combined_score": float(n % 97) / 97.0}
"""

SIGMA = math.log(4) / 1.645


class SlowLLM:
    def __init__(self, configuration):
        self.model = configuration.name
        self.calls = 0
        self.random = random.Random(os.getpid())

    async def generate(self, prompt, **kwargs):
        return await self.generate_with_context(kwargs.get("system_message", ""), [{"role": "user", "content": prompt}])

    async def generate_with_context(self, system_message, messages, **kwargs):
        self.calls += 1
        latency = min(2.0, 0.1 * math.exp(self.random.gauss(0.0, 1.0) * SIGMA))
        started = time.perf_counter()
        await asyncio.sleep(latency)
        elapsed = time.perf_counter() - started
        # One file per worker process: concurrent appends to a shared file lose lines on Windows.
        path = os.path.join(os.environ["OE_LATENCY_LOG"], str(os.getpid()) + ".log")
        with open(path, "a", encoding="utf-8") as log:
            log.write(f"{elapsed}\n")
        return f"```python\ndef solve(x):\n    v = {os.getpid()}_{self.calls}\n    return x + v\n```"


def init_slow_llm(configuration):
    return SlowLLM(configuration)


async def main(upstream, iterations, workers):
    sys.path.insert(0, str(upstream))
    from openevolve import Config, OpenEvolve
    from openevolve.config import LLMConfig, LLMModelConfig
    work = Path(tempfile.mkdtemp(prefix="oe-latency-"))
    (work / "initial.py").write_text("def solve(x):\n    return x + 1\n", encoding="utf-8")
    (work / "evaluator.py").write_text(EVALUATOR, encoding="utf-8")
    latency_log = work / "latency"
    latency_log.mkdir()
    os.environ["OE_LATENCY_LOG"] = str(latency_log)  # inherited by the worker pool
    config = Config()
    config.max_iterations = iterations
    config.random_seed = 42
    config.language = "python"
    config.diff_based_evolution = False
    config.checkpoint_interval = 10 ** 9
    config.log_level = "ERROR"
    config.database.in_memory = True
    config.database.population_size = iterations + 100
    config.evaluator.parallel_evaluations = workers
    config.evaluator.cascade_evaluation = False
    config.evaluator.max_retries = 0
    config.evaluator.timeout = 60
    config.llm = LLMConfig(models=[LLMModelConfig(name="slow", init_client=init_slow_llm)], retries=0, timeout=60,
                           api_base="http://127.0.0.1:9/unused", api_key="unused")
    engine = OpenEvolve(str(work / "initial.py"), str(work / "evaluator.py"), config, str(work / "out"))
    started = time.perf_counter()
    await engine.run(iterations=iterations)
    wall = time.perf_counter() - started
    latencies = [float(line) for log in latency_log.glob("*.log") for line in log.read_text(encoding="utf-8").splitlines()]
    busy = sum(latencies)
    print(json.dumps(dict(System="openevolve", Proposals=len(latencies), Concurrency=workers,
                          MeanInFlight=busy / wall, InFlightUtilisation=busy / wall / workers, WallSeconds=wall,
                          IdealSeconds=busy / workers, WallOverIdeal=wall / (busy / workers))))


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
