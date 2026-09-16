import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from warm_sandbox import WarmDockerSandbox, kernel_resources, CandidateExited


class KernelCounterTests(unittest.TestCase):
    def test_missing_peak_cannot_become_zero(self):
        from types import SimpleNamespace
        with patch("warm_sandbox.docker", return_value=SimpleNamespace(stdout=b'{"cpu_usec":3}')):
            with self.assertRaises(RuntimeError):
                kernel_resources("fixture")

    def test_terminal_candidate_is_distinct_from_infrastructure_failure(self):
        from types import SimpleNamespace
        with patch("warm_sandbox.docker", side_effect=[RuntimeError("exec failed"),
                   SimpleNamespace(stdout=b'[{"State":{"Running":false}}]')]):
            with self.assertRaises(CandidateExited):
                kernel_resources("fixture")
        with patch("warm_sandbox.docker", side_effect=[RuntimeError("exec failed"),
                   SimpleNamespace(stdout=b'[{"State":{"Running":true}}]')]):
            with self.assertRaises(RuntimeError):
                kernel_resources("fixture")


@unittest.skipUnless(os.environ.get("EVOLUTION_SANDBOX_IMAGE"), "Explicit local Docker image required")
class WarmContainerTests(unittest.TestCase):
    def setUp(self):
        parent = os.environ.get("EVOLUTION_SANDBOX_EVIDENCE")
        if parent:
            Path(parent).mkdir(parents=True,exist_ok=True)
        self.root = Path(tempfile.mkdtemp(prefix="warm-proof-",dir=parent))
        self.sandbox = WarmDockerSandbox(os.environ["EVOLUTION_SANDBOX_IMAGE"],self.root / "receipts",seconds=5)

    def run_source(self, code, problems=None):
        return self.sandbox.run(code,{"class":"Solver","problems":[{"x":7}] if problems is None else problems},phase="adversarial")

    def test_input_is_withheld_and_kernel_metrics_are_not_candidate_fields(self):
        row = self.run_source("""import os,time
assert not os.path.exists('/work/request.json')
time.sleep(.2)
class Solver:
 def solve(self,p):
  x=bytearray(20*1024*1024)
  return {'answer':p['x']+1,'fake_cpu':0,'fake_peak':0,'uid':os.getuid()}
""")
        self.assertEqual("completed",row["status"])
        self.assertEqual(8,row["output"][0]["answer"])
        self.assertEqual(65534,row["output"][0]["uid"])
        self.assertGreater(row["startup_seconds"],.2)
        self.assertLess(row["elapsed_seconds"],row["startup_seconds"])
        self.assertGreater(row["resources"]["cpu_seconds_through_response"],0)
        self.assertGreater(row["resources"]["peak_bytes_through_response"],20*1024*1024)
        self.assertFalse(row["unknown_work"])

    def test_nonreading_candidate_cannot_block_host_stdin_indefinitely(self):
        started = time.monotonic()
        row = self.run_source("import time\nprint('{\"ready\":true}',flush=True)\ntime.sleep(30)",[{"x":"a"*1000000}])
        self.assertEqual("timeout",row["status"])
        self.assertLess(time.monotonic()-started,20)
        self.assertFalse(row["unknown_work"])

    def test_preprinted_response_cannot_forge_fresh_nonce(self):
        row = self.run_source("import time\nprint('{\"ready\":true}',flush=True)\nprint('{\"nonce\":\"fake\",\"outputs\":[8]}',flush=True)\ntime.sleep(30)")
        self.assertEqual("invalid-output",row["status"])
        self.assertIsNone(row["elapsed_seconds"])

    def test_malformed_readiness_is_candidate_failure_and_admission_stays_open(self):
        row = self.run_source("print('not json',flush=True)\nclass Solver: pass")
        self.assertEqual("invalid-output",row["status"])
        self.assertFalse(self.sandbox.failed)
        good = self.run_source("class Solver:\n def solve(self,p): return p['x']")
        self.assertEqual("completed",good["status"])

    def test_no_container_privileges_or_writable_kernel_counters(self):
        row = self.run_source("""import os
class Solver:
 def solve(self,p):
  blocked=[]
  for path in ('/sys/fs/cgroup/memory.peak','/work/worker.py','/etc/probe'):
   try: open(path,'w').write('0')
   except OSError: blocked.append(path)
  try: os.setuid(0)
  except OSError: blocked.append('root')
  return blocked
""")
        self.assertEqual("completed",row["status"])
        self.assertEqual(4,len(row["output"][0]))

    def test_probe_failure_closes_admission_and_keeps_unknown_work(self):
        with patch("warm_sandbox.kernel_resources",side_effect=RuntimeError("kernel counter missing")):
            with self.assertRaises(RuntimeError):
                self.run_source("class Solver:\n def solve(self,p): return 8")
        self.assertTrue(self.sandbox.failed)
        self.assertTrue(self.sandbox.rows[-1]["unknown_work"])
        with self.assertRaises(RuntimeError):
            self.run_source("class Solver: pass")


if __name__ == "__main__":
    unittest.main()
