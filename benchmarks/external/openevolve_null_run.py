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
import os

def evaluate(program_path):
    with open(program_path, encoding="utf-8") as f:
        n = len(f.read())
    # One line per call, in a file of the worker process that ran it. A single shared file would lose lines: on Windows
    # append is a seek then a write, so two processes appending at once can overwrite each other.
    path = os.path.join(os.environ["OE_NULL_EVALUATION_LOG"], str(os.getpid()) + ".log")
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
        # The process id keeps proposals from different pool workers distinct: each worker has its own counter.
        return f"```python\ndef solve(x):\n    v = {os.getpid()}_{self.calls}\n    return x + v\n```"


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
    evaluation_log = work / "evaluations"
    evaluation_log.mkdir()
    os.environ["OE_NULL_EVALUATION_LOG"] = str(evaluation_log)  # inherited by the worker pool
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
    # Counting relies on every program staying in the database, so the run must fit its population.
    if iterations >= config.database.population_size:
        raise SystemExit(f"iterations must be below population_size ({config.database.population_size})")
    engine = OpenEvolve(str(work / "initial.py"), str(work / "evaluator.py"), config, str(work / "out"))
    with TreeMemory() as memory:
        started = time.perf_counter()
        await engine.run(iterations=iterations)
        seconds = time.perf_counter() - started
    # Measured, not requested. Counted as evaluator calls, not surviving programs: OpenEvolve deletes a program that
    # loses its cell once it is orphaned, so the database undercounts completed work. The initial program's call is
    # excluded, since OpenEvolve evaluates it outside the iterations.
    added = sum(len(log.read_text(encoding="utf-8").splitlines()) for log in evaluation_log.glob("*.log")) - 1
    print(json.dumps(dict(System="openevolve", Requested=iterations, Evaluations=added, Workers=workers, Islands=islands,
                          Seconds=seconds, PeakWorkingSetBytes=memory.peak)))



if __name__ == "__main__":
    import logging
    logging.disable(logging.CRITICAL)
    try:
        asyncio.run(main(Path(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])))
    finally:
        # OpenEvolve can leave pool workers running after run() returns; once this process exits they are orphans the
        # harness cannot find, and they keep burning CPU under the next measurement. Stop them here, while they are ours.
        import psutil
        workers = psutil.Process().children(recursive=True)
        for worker in workers:
            try:
                worker.kill()
            except psutil.NoSuchProcess:
                pass
        psutil.wait_procs(workers, timeout=30)