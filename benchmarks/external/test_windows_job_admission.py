"""Windows job admission contracts, required by the Windows CI lane."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

import alphaevolve_pilot as pilot


@unittest.skipUnless(sys.platform == "win32", "Windows job admission")
class WindowsJobAdmissionTests(unittest.TestCase):
    def run_program(self, body):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "candidate.py"
            path.write_text("def construct():\n" + "".join("    " + line + "\n" for line in body.splitlines()), encoding="utf-8")
            return pilot.construction(path, sys.executable)

    def test_candidate_waits_until_job_assignment(self):
        assign = pilot._Job.assign

        def delayed(job, process):
            with self.assertRaises(subprocess.TimeoutExpired):
                process.wait(timeout=2)
            assign(job, process)

        with mock.patch.object(pilot._Job, "assign", delayed):
            self.assertEqual(([1.0], None), self.run_program("return [1.0]"))

    def test_assignment_failure_reaps_the_unassigned_child(self):
        processes = []
        popen = pilot.subprocess.Popen

        def capture(*args, **kwargs):
            process = popen(*args, **kwargs)
            processes.append(process)
            return process

        try:
            with mock.patch.object(pilot.subprocess, "Popen", side_effect=capture), \
                    mock.patch.object(pilot._Job, "assign", side_effect=OSError("assignment refused")):
                with self.assertRaisesRegex(OSError, "assignment refused"):
                    self.run_program("import time\ntime.sleep(30)\nreturn [1.0]")
            self.assertIsNotNone(processes[0].poll(), "an unassigned child survived cleanup")
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=10)
