import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from docker_sandbox import DockerSandbox, unique_json
from isolated_program_evaluator import IsolatedProgramEvaluator
from program_tasks import expected, problems


class ProtocolTests(unittest.TestCase):
    def test_infrastructure_failure_forbids_later_dispatch(self):
        sandbox = DockerSandbox.__new__(DockerSandbox)
        sandbox.failed = False
        sandbox._lock = threading.Lock()
        with patch.object(sandbox, "_run", side_effect=RuntimeError("cleanup failed")) as dispatch:
            with self.assertRaisesRegex(RuntimeError, "cleanup failed"):
                sandbox.run("source", {}, phase="search")
            with self.assertRaisesRegex(RuntimeError, "further execution is forbidden"):
                sandbox.run("source", {}, phase="search")
            self.assertEqual(1, dispatch.call_count)

    def test_rejects_duplicate_and_nonfinite_receipts(self):
        for raw in ('{"a":1,"a":2}', '[NaN]', '[Infinity]'):
            with self.assertRaises(ValueError):
                unique_json(raw)

    def test_requires_immutable_image_before_dispatch(self):
        with patch("docker_sandbox.docker") as dispatch:
            with self.assertRaises(ValueError):
                DockerSandbox("python:latest", "unused")
            dispatch.assert_not_called()

    def test_independent_oracles_cover_empty_and_adversarial_cases(self):
        import base64
        import hashlib
        self.assertEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hashlib.sha256(b"").hexdigest())
        self.assertEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hashlib.sha256(b"abc").hexdigest())
        for task in ("base64_encoding", "sha256_hashing"):
            for data in (b"", b"a", b"ab", b"abc", bytes(range(256))):
                cases = [{"plaintext": {"$bytes": base64.b64encode(data).decode()}}]
                answer = expected(task, cases)[0]
                value = next(iter(answer.values()))["$bytes"]
                actual = base64.b64decode(value)
                self.assertEqual(base64.b64encode(data) if task == "base64_encoding" else hashlib.sha256(data).digest(), actual)
        self.assertEqual([{"number_connected_components": 3}], expected("count_connected_components",
            [{"num_nodes": 5, "edges": [[0, 1], [1, 0], [2, 2], [3, 4]]}]))
        self.assertNotEqual(problems("sha256_hashing", 1), problems("sha256_hashing", 2))


@unittest.skipUnless(os.environ.get("EVOLUTION_SANDBOX_IMAGE"), "Explicit local Docker image required")
class ContainerTests(unittest.TestCase):
    def setUp(self):
        # Evidence deliberately retained, including rejection/timeout traces.
        parent = os.environ.get("EVOLUTION_SANDBOX_EVIDENCE")
        if parent:
            Path(parent).mkdir(parents=True, exist_ok=True)
        root = Path(tempfile.mkdtemp(prefix="us02-sandbox-proof-", dir=parent)) / "evidence"
        self.sandbox = DockerSandbox(os.environ["EVOLUTION_SANDBOX_IMAGE"], root, seconds=5)

    def run_source(self, code):
        return self.sandbox.run(code, {"class": "Solver", "problems": [{}]}, phase="adversarial")

    def test_deep_invalid_json_is_rejected_without_poisoning_infrastructure(self):
        result = self.run_source("print('[' * 10000 + ']' * 10000)\nclass Solver:\n def solve(self, p): return 0")
        self.assertEqual("invalid-output", result["status"])
        self.assertFalse(self.sandbox.failed)

    def test_kernel_enforces_nonroot_no_privileges_no_network_and_readonly_files(self):
        code = '''import os, socket
class Solver:
 def solve(self, problem):
  failures = []
  for path in ('/etc/evolution-probe', '/work/forbidden'):
   try: open(path, 'w').write('x')
   except OSError: failures.append(path)
  try: os.setuid(0)
  except OSError: failures.append('root')
  try: socket.create_connection(('1.1.1.1', 443), timeout=0.2)
  except OSError: failures.append('network')
  status = open('/proc/self/status').read()
  return {'uid':os.getuid(),'failures':failures,'secret':os.environ.get('EVOLUTION_TEST_SECRET'),
          'caps':next(x for x in status.splitlines() if x.startswith('CapEff:')),
          'nnp':next(x for x in status.splitlines() if x.startswith('NoNewPrivs:')),
          'seccomp':next(x for x in status.splitlines() if x.startswith('Seccomp:'))}
'''
        with patch.dict(os.environ, {"EVOLUTION_TEST_SECRET": "must-not-enter-container"}):
            result = self.run_source(code)
        self.assertEqual("completed", result["status"])
        value = result["output"][0]
        self.assertEqual(65534, value["uid"])
        self.assertEqual(["/etc/evolution-probe", "/work/forbidden", "root", "network"], value["failures"])
        self.assertIsNone(value["secret"])
        self.assertEqual("0000000000000000", value["caps"].split()[-1])
        self.assertEqual("1", value["nnp"].split()[-1])
        self.assertEqual("2", value["seccomp"].split()[-1])
        self.assertGreater(result["elapsed_seconds"], 0)

    def test_timeout_is_retained_and_container_is_removed(self):
        result = self.run_source("while True: pass")
        self.assertEqual("timeout", result["status"])
        self.assertFalse(result["unknown_work"])
        self.assertTrue((self.sandbox.root / result["id"] / "receipt.json").exists())

    def test_output_flood_is_bounded_and_rejected(self):
        result = self.run_source("print('x' * 5000000)\nclass Solver: pass")
        self.assertEqual("output-limit", result["status"])
        self.assertLessEqual((self.sandbox.root / result["id"] / "stdout.bin").stat().st_size, 4 * 1024 * 1024)

    def test_forged_timing_cannot_pass_host_oracle(self):
        evaluator = IsolatedProgramEvaluator(self.sandbox, "count_connected_components", 17, samples=1)
        receipt = evaluator("class CountConnectedComponents:\n def solve(self,p): return {'quality':1e99,'duration':0}\n")
        self.assertEqual("invalid", receipt["status"])
        self.assertGreater(receipt["duration_seconds"], 0)
        self.assertFalse(receipt["unknown_work"])

    def test_memory_limit_is_enforced(self):
        # Docker's container-level OOMKilled flag is not the kernel counter.
        # Keep a small fixture parent alive so it can read the kernel's own OOM
        # evidence after its allocation child is killed; exit 137 alone is not
        # proof. Both cgroup versions carry an oom_kill counter and the limit.
        result = self.run_source('''import subprocess
class Solver:
 def solve(self, problem):
  import os
  v2 = os.path.exists('/sys/fs/cgroup/memory.events')
  base = '/sys/fs/cgroup/' if v2 else '/sys/fs/cgroup/memory/'
  def events():
   return dict(line.split() for line in open(base + ('memory.events' if v2 else 'memory.oom_control')))
  before = int(events()['oom_kill'])
  maximum = int(open(base + ('memory.max' if v2 else 'memory.limit_in_bytes')).read())
  child = subprocess.run(['python', '-c', 'x = [bytearray(8*1024*1024) for _ in range(128)]'],
                         capture_output=True, timeout=3)
  return {'maximum':maximum, 'exit':child.returncode, 'oom_kills':int(events()['oom_kill'])-before}
''')
        self.assertEqual("completed", result["status"])
        self.assertEqual(self.sandbox.memory_mib * 1024 * 1024,result["output"][0]["maximum"])
        self.assertEqual(-9,result["output"][0]["exit"])
        self.assertGreaterEqual(result["output"][0]["oom_kills"],1)
        self.assertFalse(result["unknown_work"])

    def test_pid_limit_is_enforced(self):
        result = self.run_source('''import subprocess
class Solver:
 def solve(self, problem):
  children = []
  limited = False
  try:
   for i in range(64):
    children.append(subprocess.Popen(['python', '-c', 'import time; time.sleep(20)']))
  except OSError:
   limited = True
  finally:
   for child in children: child.terminate()
   for child in children: child.wait()
  return {'limited':limited,'started':len(children)}
''')
        self.assertEqual("completed", result["status"])
        self.assertTrue(result["output"][0]["limited"])
        self.assertLess(result["output"][0]["started"], 32)


if __name__ == "__main__":
    unittest.main()
