"""Untrusted execution side: outputs only, NEVER an authoritative timing or correctness receipt."""
import base64
import copy
import json
from pathlib import Path
import sys
import types


def decode(value):
    if isinstance(value, dict):
        if set(value) == {"$bytes"}:
            return base64.b64decode(value["$bytes"], validate=True)
        return {key: decode(item) for key, item in value.items()}
    if isinstance(value, list):
        return [decode(item) for item in value]
    return value


def encode(value):
    if isinstance(value, bytes):
        return {"$bytes": base64.b64encode(value).decode("ascii")}
    if isinstance(value, dict):
        return {key: encode(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [encode(item) for item in value]
    return value


class Task:
    def __init__(self, **kwargs):
        self.task_name = type(self).__name__
        self.oracle = self.solve


if __name__ == "__main__":
    package = types.ModuleType("AlgoTuneTasks")
    package.__path__ = []
    bridge = types.ModuleType("AlgoTuneTasks.base")
    bridge.Task = Task
    bridge.register_task = lambda name: lambda cls: cls
    sys.modules["AlgoTuneTasks"] = package
    sys.modules["AlgoTuneTasks.base"] = bridge
    request = json.loads(Path("/work/request.json").read_text())
    module = types.ModuleType("candidate")
    exec(compile(Path("/work/candidate.py").read_bytes(), "candidate.py", "exec"), module.__dict__)
    solver = getattr(module, request["class"])().solve
    outputs = [encode(solver(copy.deepcopy(decode(problem)))) for problem in request["problems"]]
    print(json.dumps(outputs, allow_nan=False, separators=(",", ":")))
