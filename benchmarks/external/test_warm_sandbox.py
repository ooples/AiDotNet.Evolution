import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from warm_sandbox import (WarmDockerSandbox, kernel_resources, CandidateExited, COUNTER_FILES,
                          host_cgroup_version)


class KernelCounterTests(unittest.TestCase):
    def test_cgroup_v1_counters_convert_nanoseconds_and_keep_the_peak(self):
        from types import SimpleNamespace
        with patch("warm_sandbox.docker", return_value=SimpleNamespace(stdout=b"2500999\n7340032\n")):
            self.assertEqual(dict(cpu_usec=2500, memory_peak_bytes=7340032), kernel_resources("fixture", "1"))

    def test_cgroup_v1_rejects_an_unexpected_shape_and_a_zero_peak(self):
        from types import SimpleNamespace
        for raw in (b"2500999\n", b"1\n2\n3\n", b"usage_usec 5\n7340032\n", b"2500999\n0\n", b"-1\n7340032\n"):
            with patch("warm_sandbox.docker", return_value=SimpleNamespace(stdout=raw)):
                with self.assertRaises(RuntimeError, msg=raw):
                    kernel_resources("fixture", "1")

    def test_v2_identity_is_byte_identical_and_only_v1_is_marked(self):
        # The identity is copied into digested, compared manifests; a v2 change would
        # silently invalidate every existing v2 digest.
        import tempfile as _tempfile
        from docker_sandbox import DockerSandbox

        def base_init(self, image, evidence, **_):
            self.identity, self.root = {"schema": "evolution-docker-evaluator-v1"}, Path(evidence)
            self.root.mkdir(parents=True)

        for version in ("2", "1"):
            with patch.object(DockerSandbox, "__init__", base_init), \
                    patch("warm_sandbox.host_cgroup_version", return_value=version):
                box = WarmDockerSandbox("sha256:" + "0" * 64, Path(_tempfile.mkdtemp()) / "e")
            if version == "2":
                self.assertNotIn("cgroup_version", box.identity)
                self.assertEqual("cgroup-v2 cumulative CPU and lifetime memory.peak, INCLUDING probes/startup",
                                 box.identity["resource_metric"])
            else:
                self.assertEqual("1", box.identity["cgroup_version"])
                self.assertIn("cgroup-v1", box.identity["resource_metric"])

    def test_host_cgroup_version_comes_from_the_daemon_and_refuses_unknowns(self):
        from types import SimpleNamespace
        for reported, expected in ((b"1\n", "1"), (b"2\n", "2")):
            with patch("warm_sandbox.docker", return_value=SimpleNamespace(stdout=reported)) as call:
                self.assertEqual(expected, host_cgroup_version())
                self.assertEqual("info", call.call_args.args[0])
        with patch("warm_sandbox.docker", return_value=SimpleNamespace(stdout=b"3\n")):
            with self.assertRaises(RuntimeError):
                host_cgroup_version()

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
        # Permission/protocol assertions use the production timeout. Only the
        # deadline-specific adversary needs the shorter five-second allowance.
        seconds = 5 if self._testMethodName == "test_nonreading_candidate_cannot_block_host_stdin_indefinitely" else 15
        self.sandbox = WarmDockerSandbox(os.environ["EVOLUTION_SANDBOX_IMAGE"],self.root / "receipts",seconds=seconds)

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
        # The counter paths are the ones THIS host's probe reads. A fixed v2 path on a v1
        # host does not exist, so the write fails for the wrong reason and proves nothing.
        counters = COUNTER_FILES[self.sandbox.cgroup_version]
        for path in counters:
            self.assertEqual("exists", self.run_source(
                f"import os\nclass Solver:\n def solve(self,p): return 'exists' if os.path.exists({path!r}) else 'missing'"
            )["output"][0], path)
        row = self.run_source(f"""import os
class Solver:
 def solve(self,p):
  blocked=[]
  for path in {tuple(counters) + ('/work/worker.py','/etc/probe')!r}:
   try: open(path,'w').write('0')
   except OSError: blocked.append(path)
  try: os.setuid(0)
  except OSError: blocked.append('root')
  return blocked
""")
        self.assertEqual("completed",row["status"])
        self.assertEqual(len(counters) + 3,len(row["output"][0]))

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
