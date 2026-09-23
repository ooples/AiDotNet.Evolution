"""V1-30: one pinned-OpenEvolve run with a null LLM and a null evaluator, in one fresh process.

Both are in-process (no broker, no HTTP), so measured time is OpenEvolve's own orchestration:
database sampling, prompt building, its process pool and result handling. The LLM returns a
valid full-rewrite program at once; the evaluator scores it with constant-cost arithmetic.
Usage: python openevolve_null_run.py <upstream> <iterations> <workers> <islands>
"""
import asyncio
import json
import os
from pathlib import Path
import sys
import tempfile
import time

EVALUATOR = """
def evaluate(program_path):
    with open(program_path, encoding="utf-8") as f:
        n = len(f.read())
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
        return f"```python\ndef solve(x):\n    v = {self.calls % 1000}\n    return x + v\n```"


def init_null_llm(configuration):
    return NullLLM(configuration)


class TreeMemory:
    """Peak resident memory of this process AND its workers, sampled every 50 ms: OpenEvolve runs a
    process pool, so a parent-only figure would understate it."""
    def __init__(self):
        import threading
        import psutil
        self._root, self.peak, self._stop = psutil.Process(), 0, threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _sample(self):
        import psutil
        total = 0
        for process in [self._root] + self._root.children(recursive=True):
            try:
                total += process.memory_info().rss
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        self.peak = max(self.peak, total)

    def _run(self):
        while not self._stop.wait(0.05):
            self._sample()

    def __enter__(self):
        self._sample(); self._thread.start(); return self

    def __exit__(self, *_):
        self._stop.set(); self._thread.join(); self._sample()


async def main(upstream, iterations, workers, islands):
    sys.path.insert(0, str(upstream))
    from openevolve import Config, OpenEvolve
    from openevolve.config import LLMConfig, LLMModelConfig
    work = Path(tempfile.mkdtemp(prefix="oe-null-"))
    (work / "initial.py").write_text("def solve(x):\n    return x + 1\n", encoding="utf-8")
    (work / "evaluator.py").write_text(EVALUATOR, encoding="utf-8")
    config = Config()
    config.max_iterations = iterations
    config.random_seed = 42
    config.language = "python"
    config.diff_based_evolution = False
    config.checkpoint_interval = 10 ** 9
    config.log_level = "ERROR"
    config.database.num_islands = islands
    config.database.in_memory = True
    config.evaluator.parallel_evaluations = workers
    config.evaluator.cascade_evaluation = False
    config.evaluator.max_retries = 0
    config.evaluator.timeout = 60
    config.llm = LLMConfig(models=[LLMModelConfig(name="null", init_client=init_null_llm)], retries=0, timeout=60,
                           api_base="http://127.0.0.1:9/unused", api_key="unused")
    engine = OpenEvolve(str(work / "initial.py"), str(work / "evaluator.py"), config, str(work / "out"))
    with TreeMemory() as memory:
        started = time.perf_counter()
        await engine.run(iterations=iterations)
        seconds = time.perf_counter() - started
    print(json.dumps(dict(System="openevolve", Evaluations=iterations, Workers=workers, Islands=islands, Seconds=seconds,
                          PeakWorkingSetBytes=memory.peak)))


if __name__ == "__main__":
    import logging
    logging.disable(logging.CRITICAL)
    asyncio.run(main(Path(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])))