"""Scores every OpenEvolve example's own seed program with its own evaluator, under Linux.

    docker run --rm -v <upstream>:/src:ro -v eng:/probe:ro python:3.12-slim bash -c       "cp -r /src /upstream && pip install -q numpy scipy pyyaml requests dacite openai &&        pip install -q --no-deps -e /upstream && python /probe/probe-openevolve-examples.py"

The dependency set is the one benchmarks/external/requirements-program.txt installs in the
upstream-parity job, so a score here is a score that job could reproduce. OpenEvolveExampleSmokeTests
smokes exactly the examples this reports above zero; docs/parity/OPENEVOLVE_EXAMPLES.md records the
rest with the reason.

Run inside a container with the upstream checkout mounted at /upstream. Prints one line per
example: the example name, then either the seed's primary score or the reason it could not run.
A smoke test is only worth building for an example whose seed scores above zero, because the
evaluators return a zero score on error and a zero tells a smoke nothing.
"""
import importlib.util
import os
import sys
import traceback

ROOT = "/upstream/examples"
PRIMARY = ("combined_score", "composite_score", "score", "accuracy", "value", "fitness")


def seed_program(example_dir):
    for name in sorted(os.listdir(example_dir)):
        if name.startswith("init") and ("program" in name or "content" in name):
            return os.path.join(example_dir, name)
    return None


def evaluator(example_dir):
    for name in ("evaluator.py", "eval.py", "evaluator_stub.py"):
        path = os.path.join(example_dir, name)
        if os.path.isfile(path):
            return path
    return None


def primary(result):
    # OpenEvolve evaluators may return an EvaluationResult carrying .metrics, or a coroutine from an
    # async evaluate(). Unwrap both, or the probe reports a type name instead of a score.
    if hasattr(result, "metrics"):
        result = result.metrics
    elif hasattr(result, "send") and hasattr(result, "throw"):
        import asyncio
        # asyncio.run: get_event_loop() with no running loop is deprecated (3.12) and raises in 3.14.
        result = asyncio.run(result)
        if hasattr(result, "metrics"):
            result = result.metrics
    if not isinstance(result, dict):
        return "non-dict result: " + type(result).__name__
    for key in PRIMARY:
        if key in result:
            return "%s=%s" % (key, result[key])
    return "no primary key in " + ",".join(sorted(result)[:5])


def main():
    for example in sorted(os.listdir(ROOT)):
        directory = os.path.join(ROOT, example)
        if not os.path.isdir(directory):
            continue

        ev = evaluator(directory)
        seed = seed_program(directory)
        if ev is None or seed is None:
            print("%-32s SKIP no %s" % (example, "evaluator" if ev is None else "seed program"))
            continue

        # The evaluators read files beside themselves, so run from the example's own directory.
        previous = os.getcwd()
        os.chdir(directory)
        sys.path.insert(0, directory)
        try:
            spec = importlib.util.spec_from_file_location("ev_" + example, ev)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            if not hasattr(module, "evaluate"):
                print("%-32s SKIP no evaluate() in %s" % (example, os.path.basename(ev)))
                continue
            print("%-32s OK   %s" % (example, primary(module.evaluate(seed))))
        except BaseException:
            last = traceback.format_exc().strip().splitlines()[-1]
            print("%-32s FAIL %s" % (example, last[:140]))
        finally:
            sys.path.remove(directory)
            os.chdir(previous)
            sys.stdout.flush()


main()
