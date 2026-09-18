"""Untrusted worker. Inputs arrive AFTER readiness; measurements stay outside it."""
import copy
import json
from pathlib import Path
import sys
import types

import runpy

Task = runpy.run_path('/work/worker_codec.py')['Task']
_codec = runpy.run_path('/work/wire_codec.py')
decode, encode = (_codec[k] for k in ('decode', 'encode'))


if __name__ == "__main__":
    package = types.ModuleType("AlgoTuneTasks")
    package.__path__ = []
    bridge = types.ModuleType("AlgoTuneTasks.base")
    bridge.Task = Task
    bridge.register_task = lambda name: lambda cls: cls
    sys.modules["AlgoTuneTasks"] = package
    sys.modules["AlgoTuneTasks.base"] = bridge
    metadata = json.loads(Path("/work/metadata.json").read_bytes())
    module = types.ModuleType("candidate")
    exec(compile(Path("/work/candidate.py").read_bytes(), "candidate.py", "exec"), module.__dict__)
    solver = getattr(module, metadata["class"])().solve
    print('{"ready":true}', flush=True)
    request = json.loads(sys.stdin.readline())
    outputs = [encode(solver(copy.deepcopy(decode(problem)))) for problem in request["problems"]]
    print(json.dumps({"nonce": request["nonce"], "outputs": outputs}, allow_nan=False, separators=(",", ":")), flush=True)
    # Keep the cgroup alive for a host-initiated, privileged READ-ONLY kernel probe.
    # None of the values printed by this process are trusted as measurements.
    sys.stdin.readline()
