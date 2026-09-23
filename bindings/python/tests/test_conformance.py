"""The shared durable-worker conformance scenarios (conformance/durable-v1/scenarios.json), run through the
Python client against the real host. The C# endpoint and the TypeScript client run the same file."""
import copy
import json
import os
from pathlib import Path
import shutil
import tempfile
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from aidotnet_evolution import DurableWorkClient, DurableWorkError

ROOT = Path(__file__).resolve().parents[3]
SUITE = json.loads((ROOT / "conformance/durable-v1/scenarios.json").read_text(encoding="utf-8"))
DLL = os.environ.get("AIDOTNET_DURABLE_HOST_DLL") or str(ROOT / "src/AiDotNet.Evolution.Host/bin/Release/net10.0/aidotnet-evolution-host.dll")
HOST = os.environ.get("AIDOTNET_DURABLE_HOST_PATH")
TRANSPORT = {"host_path": HOST} if HOST else {"host_path": "dotnet", "host_args": [DLL]}


def substitute(value, directory, lease):
    if value == "$dir":
        return directory
    if value == "$config":
        return substitute(copy.deepcopy(SUITE["config"]), directory, lease)
    if value == "$job":
        return copy.deepcopy(SUITE["job"])
    if isinstance(value, str) and value.startswith("$lease"):
        node = lease
        for part in [p for p in value[len("$lease"):].split(".") if p]:
            node = node[part]
        return copy.deepcopy(node)
    if isinstance(value, dict):
        return {k: substitute(v, directory, lease) for k, v in value.items()}
    return value


def lookup(reply, path):
    node = reply
    for part in path.split("."):
        node = node.get(part) if isinstance(node, dict) else None
    return node


def call(client, op, args):
    """The client's typed return, re-expressed as the protocol reply fields the scenarios assert on."""
    if op == "enqueue":
        return {"enqueued": client.enqueue(args["job"])}
    if op == "claim":
        lease = client.claim(args["worker"])
        return {"available": lease is not None, "lease": lease}
    if op == "heartbeat":
        return {"status": client.heartbeat(args["identity"], args["workerId"])}
    if op == "cancel":
        return {"canceled": client.cancel(args["evaluationId"], args["attempt"])}
    if op == "commit":
        return {"disposition": client.commit(args)}
    if op == "result":
        return {"result": client.result(args["evaluationId"], args["attempt"])}
    if op == "delivery":
        return {"result": client.delivery(args["identity"], args["workerId"])}
    if op == "close":
        client.close()
        return {"closed": True}
    raise AssertionError("unmapped op " + op)


@unittest.skipUnless(HOST or Path(DLL).exists(), "Build src/AiDotNet.Evolution.Host (Release, net10.0) first")
class ConformanceTests(unittest.TestCase):
    def test_every_shared_scenario(self):
        for scenario in SUITE["scenarios"]:
            with self.subTest(scenario=scenario["name"]):
                directory = tempfile.mkdtemp(prefix="durable-conformance-")
                client, lease = None, None
                try:
                    for index, step in enumerate(scenario["steps"], 1):
                        args = {k: substitute(v, directory, lease) for k, v in step["args"].items()}
                        expect = step["expect"]
                        try:
                            if step["op"] == "open":
                                client = DurableWorkClient(args["config"], **TRANSPORT)
                                reply = client.status()
                            else:
                                reply = call(client, step["op"], args)
                        except (DurableWorkError, ValueError) as error:
                            self.assertIn("$error", expect, f"{scenario['name']} step {index} ({step['op']}) failed: {error}")
                            # A refusal must be the host's ok:false protocol error, not a dead or crashed host.
                            self.assertRegex(str(error), r"^\w+Exception: ", f"{scenario['name']} step {index}: not a protocol refusal: {error}")
                            continue
                        self.assertNotIn("$error", expect, f"{scenario['name']} step {index} ({step['op']}) should have been refused")
                        if step["op"] == "claim" and reply["lease"] is not None:
                            lease = reply["lease"]
                        for path, expected in expect.items():
                            self.assertEqual(expected, lookup(reply, path), f"{scenario['name']} step {index} ({step['op']}): {path}")
                finally:
                    if client is not None:
                        try:
                            client.close()
                        except DurableWorkError:
                            pass
                    shutil.rmtree(directory, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()