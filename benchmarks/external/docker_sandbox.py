"""Credential-free, bounded Linux containers; daemon timing and host-side correctness.

The trusted local Docker daemon/kernel are the security boundary. Never connect this
runner to a remote or untrusted daemon. Candidate-supplied clocks are not measurements.
"""
from __future__ import annotations

import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import threading
import time
import uuid

MAX_OUTPUT = 4 * 1024 * 1024
MAX_REQUEST = 2 * 1024 * 1024
IMAGE = re.compile(r"sha256:[0-9a-f]{64}\Z")
OWNER = "org.aidotnet.evolution.benchmark"


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def unique_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON field")
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Nonfinite JSON")))


def docker(*args, timeout=30, check=True):
    result = subprocess.run(["docker", *args], capture_output=True, timeout=timeout)
    if len(result.stdout) + len(result.stderr) > 8 * 1024 * 1024:
        raise RuntimeError("Docker control response exceeded bound")
    if check and result.returncode:
        raise RuntimeError("Docker control failed: " + result.stderr.decode(errors="replace")[:400])
    return result


class DockerSandbox:
    def __init__(self, image, evidence, *, seconds=15, memory_mib=512):
        if not IMAGE.fullmatch(image) or not 1 <= seconds <= 60 or not 128 <= memory_mib <= 1024:
            raise ValueError("Pin an immutable image and bounded resources")
        context = unique_json(docker("context", "inspect").stdout)[0]
        endpoint = context["Endpoints"]["docker"]["Host"]
        if not endpoint.startswith(("npipe://", "unix://")) or os.environ.get("DOCKER_HOST") or os.environ.get("DOCKER_TLS_VERIFY"):
            raise ValueError("A local trusted Docker endpoint without environment overrides is required")
        info = unique_json(docker("info", "--format", "{{json .}}").stdout)
        if info["OSType"] != "linux" or not any("seccomp" in option for option in info["SecurityOptions"]):
            raise ValueError("A Linux daemon with seccomp is required")
        metadata = unique_json(docker("image", "inspect", image).stdout)[0]
        if metadata["Id"] != image or metadata["Os"] != "linux":
            raise ValueError("Image identity/platform mismatch")
        self.image, self.seconds, self.memory_mib = image, seconds, memory_mib
        self.root = Path(evidence).resolve()
        self.root.mkdir(parents=True, exist_ok=False)
        self.identity = {
            "schema": "evolution-docker-evaluator-v1", "image": image, "architecture": metadata["Architecture"],
            "docker_server": info["ServerVersion"], "kernel": info["KernelVersion"], "os": info["OperatingSystem"],
            "security": info["SecurityOptions"], "cpuset": "0", "cpus": 1, "memory_mib": memory_mib,
            "pids": 32, "seconds": seconds, "metric": "daemon_start_to_finish_seconds",
            "includes": "Python startup, imports, all solve calls and serialization; NOT isolated solve-only latency",
            "worker_sha256": hashlib.sha256(Path(__file__).with_name("sandbox").joinpath("worker.py").read_bytes()).hexdigest()
        }
        (self.root / "environment.json").write_bytes(encode(self.identity))
        self.rows = []
        self._lock = threading.Lock()

    def command(self, name, bundle):
        # No host network, credentials, devices, daemon socket or writable host mount.
        return ["create", "--name", name, "--label", OWNER + "=" + name,
                "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges=true", "--pids-limit", "32",
                "--memory", f"{self.memory_mib}m", "--memory-swap", f"{self.memory_mib}m",
                "--cpus", "1", "--cpuset-cpus", "0", "--user", "65534:65534", "--ipc", "none",
                "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16777216", "--log-driver", "none",
                "--mount", f"type=bind,source={bundle},target=/work,readonly", self.image]

    def run(self, source, request, *, phase):
        if not self._lock.acquire(blocking=False):
            raise RuntimeError("Benchmark execution must be serial")
        try:
            return self._run(source, request, phase)
        finally:
            self._lock.release()

    def _run(self, source, request, phase):
        raw_source = source.encode("utf-8")
        raw_request = encode(request)
        if not raw_source or len(raw_source) > 65536 or len(raw_request) > MAX_REQUEST or phase not in ("search", "confirmation", "adversarial"):
            raise ValueError("Invalid bounded candidate/request/phase")
        run_id = "evolution-" + uuid.uuid4().hex
        directory = self.root / run_id
        bundle = directory / "input"
        bundle.mkdir(parents=True, exist_ok=False)
        worker = Path(__file__).with_name("sandbox") / "worker.py"
        (bundle / "worker.py").write_bytes(worker.read_bytes())
        (bundle / "candidate.py").write_bytes(raw_source)
        (bundle / "request.json").write_bytes(raw_request)
        row = {"id": run_id, "phase": phase, "source_sha256": hashlib.sha256(raw_source).hexdigest(),
               "request_sha256": hashlib.sha256(raw_request).hexdigest(), "image": self.image,
               "status": "dispatched", "unknown_work": True}
        self.rows.append(row)
        process, container = None, None
        collected = [bytearray(), bytearray()]
        overflow = threading.Event()
        threads = []
        started = time.monotonic()
        try:
            container = docker(*self.command(run_id, bundle)).stdout.decode().strip()
            if not re.fullmatch(r"[0-9a-f]{64}", container):
                raise RuntimeError("Invalid created-container identity")
            inspect = unique_json(docker("inspect", container).stdout)[0]
            self._verify_configuration(inspect, run_id, bundle)
            (directory / "configuration.json").write_bytes(encode(inspect))
            process = subprocess.Popen(["docker", "start", "--attach", container], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            def drain(stream, destination):
                while True:
                    block = stream.read(4096)
                    if not block:
                        return
                    room = MAX_OUTPUT - len(destination)
                    destination.extend(block[:room])
                    if len(block) > room:
                        overflow.set()
                        # Keep draining/discarding so the attached Docker client can exit after kill.
            for stream, destination in zip((process.stdout, process.stderr), collected):
                thread = threading.Thread(target=drain, args=(stream, destination), daemon=True)
                thread.start()
                threads.append(thread)
            dispatched = time.monotonic()
            while process.poll() is None and not overflow.is_set() and time.monotonic() - dispatched < self.seconds:
                time.sleep(0.02)
            if process.poll() is None or overflow.is_set():
                row["status"] = "output-limit" if overflow.is_set() else "timeout"
                docker("kill", container, check=False)
            process.wait(timeout=15)
            for thread in threads:
                thread.join(timeout=5)
            if any(thread.is_alive() for thread in threads):
                raise RuntimeError("Container output did not terminate")
            final = unique_json(docker("inspect", container).stdout)[0]
            state = final["State"]
            if state["Running"] or final["Id"] != container:
                raise RuntimeError("Missing terminal container receipt")
            begin = datetime.datetime.fromisoformat(state["StartedAt"].replace("Z", "+00:00"))
            end = datetime.datetime.fromisoformat(state["FinishedAt"].replace("Z", "+00:00"))
            elapsed = (end - begin).total_seconds()
            if not math.isfinite(elapsed) or elapsed <= 0:
                raise RuntimeError("Invalid daemon timing")
            row.update(unknown_work=False, elapsed_seconds=elapsed, state=state, container_id=container)
            if row["status"] == "dispatched":
                row["status"] = "completed" if state["ExitCode"] == 0 and not collected[1] else "candidate-failed"
            if row["status"] == "completed":
                try:
                    row["output"] = unique_json(bytes(collected[0]))
                except (ValueError, UnicodeError):
                    row["status"] = "invalid-output"
            return row
        except Exception as error:
            row.update(status="infrastructure-failed", error=type(error).__name__ + ": " + str(error)[:400])
            raise
        finally:
            # Only this create-new, inspected owner-labelled container may be removed. No global prune.
            cleanup_error = None
            try:
                if container is not None and re.fullmatch(r"[0-9a-f]{64}", container):
                    owned = unique_json(docker("inspect", container).stdout)[0]
                    if owned["Config"]["Labels"].get(OWNER) != run_id or owned["Id"] != container:
                        raise RuntimeError("Refusing to remove a container with changed ownership")
                    docker("rm", "--force", container)
                if process is not None:
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=10)
                    for thread in threads:
                        thread.join(timeout=5)
                    process.stdout.close()
                    process.stderr.close()
            except Exception as error:
                cleanup_error = error
                row.update(status="cleanup-failed", unknown_work=True, cleanup_error=type(error).__name__)
            finally:
                row["supervisor_seconds"] = time.monotonic() - started
                (directory / "stdout.bin").write_bytes(collected[0])
                (directory / "stderr.bin").write_bytes(collected[1])
                (directory / "receipt.json").write_bytes(encode(row))
            if cleanup_error:
                raise RuntimeError("Owned-container cleanup failed; stop the campaign") from cleanup_error

    def _verify_configuration(self, config, name, bundle):
        host, process = config["HostConfig"], config["Config"]
        sources = {str(bundle), str(bundle).replace("\\", "/")}
        if os.name == "nt":
            sources.add("/run/desktop/mnt/host/" + bundle.drive[0].lower() + str(bundle)[2:].replace("\\", "/"))
        if (config["Image"] != self.image or process["User"] != "65534:65534" or
                process["Labels"].get(OWNER) != name or host["NetworkMode"] != "none" or not host["ReadonlyRootfs"] or
                host["Privileged"] or host["CapAdd"] or host["CapDrop"] != ["ALL"] or
                "no-new-privileges=true" not in host["SecurityOpt"] or host["PidsLimit"] != 32 or
                host["Memory"] != self.memory_mib * 1024**2 or host["MemorySwap"] != host["Memory"] or
                host["NanoCpus"] != 1_000_000_000 or host["CpusetCpus"] != "0" or host["IpcMode"] != "none" or
                len(host["Mounts"]) != 1 or host["Mounts"][0]["Target"] != "/work" or not host["Mounts"][0]["ReadOnly"] or
                host["Mounts"][0]["Source"] not in sources):
            raise RuntimeError("Docker did not enforce the declared isolation configuration")
