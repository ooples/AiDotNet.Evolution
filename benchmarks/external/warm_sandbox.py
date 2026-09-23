"""Externally timed warm transactions with independently read cgroup resources.

The workload stays nonroot with all capabilities dropped. Host-initiated probes
run read-only as UID0, preventing the candidate UID from signalling or ptracing
them. Resource totals INCLUDE probe overhead; memory.peak is a cgroup lifetime
peak including initialization, not isolated candidate RSS. No privileged container
or host filesystem mount is added. A failed probe closes sandbox admission.

BOTH CGROUP VERSIONS, because the counters were v2-only and Docker Desktop on WSL2 can run
v1: there every warm evaluation raised "cat: /sys/fs/cgroup/cpu.stat: No such file" and
no campaign could run at all. v1 exposes the same two lifetime quantities --
cpuacct.usage (cumulative CPU, ns) and memory.max_usage_in_bytes (lifetime peak, page
cache included exactly as memory.peak includes it) -- so the metric is unchanged.
The version comes from the Docker DAEMON, never from inside the untrusted container, and
is recorded in the evidence identity so v1 and v2 measurements are never silently pooled.
"""
import datetime
import hashlib
import re
import secrets
import subprocess
import threading
import time
import uuid
from pathlib import Path

from docker_sandbox import DockerSandbox, MAX_OUTPUT, MAX_REQUEST, OWNER, docker, encode, unique_json

# Per cgroup version, the lifetime CPU and memory-peak counters the host probe reads.
PROBES = {
    "2": ["/bin/cat", "/sys/fs/cgroup/cpu.stat", "/sys/fs/cgroup/memory.peak"],
    "1": ["/bin/cat", "/sys/fs/cgroup/cpuacct/cpuacct.usage",
          "/sys/fs/cgroup/memory/memory.max_usage_in_bytes"],
}
COUNTER_FILES = {version: probe[1:] for version, probe in PROBES.items()}
# cgroup-v1 counters live under each controller's mount point, which is host layout:
# /sys/fs/cgroup/cpuacct, a co-mounted /sys/fs/cgroup/cpu,cpuacct, or anything else. They
# are therefore resolved from the container's own mount table, not assumed.
V1_COUNTERS = (("cpuacct", "cpuacct.usage"), ("memory", "memory.max_usage_in_bytes"))


def v1_counter_files(mountinfo):
    """The cgroup-v1 CPU and memory-peak files named by a /proc/self/mountinfo table."""
    points = {controller: [] for controller, _ in V1_COUNTERS}
    for line in mountinfo.splitlines():
        fields = line.split()
        if "-" not in fields:
            raise RuntimeError("Unreadable mountinfo line")
        separator = fields.index("-")
        if separator < 6 or len(fields) < separator + 4 or fields[separator + 1] != "cgroup":
            continue
        # Mount points escape whitespace and backslash as octal (\040); commas are literal.
        point = re.sub(r"\\([0-7]{3})", lambda match: chr(int(match.group(1), 8)), fields[4])
        options = set(fields[separator + 3].split(","))
        for controller in points:
            if controller in options:
                points[controller].append(point)
    files = []
    for controller, name in V1_COUNTERS:
        if len(set(points[controller])) != 1:
            raise RuntimeError(f"Expected exactly one cgroup-v1 {controller} mount, found {points[controller]}")
        files.append(points[controller][0].rstrip("/") + "/" + name)
    return files


def resolve_counter_files(container, version):
    if version != "1":
        return list(COUNTER_FILES[version])
    raw = probe_container(container, ["/bin/cat", "/proc/self/mountinfo"])
    try:
        return v1_counter_files(raw.decode("utf-8"))
    except UnicodeError as error:
        raise RuntimeError("Unreadable container mountinfo") from error


def host_cgroup_version():
    """The daemon's cgroup version. Refuses anything it cannot measure."""
    version = docker("info", "--format", "{{.CgroupVersion}}").stdout.decode("ascii").strip()
    if version not in PROBES:
        raise RuntimeError(f"Unsupported cgroup version {version!r}")
    return version


class CandidateExited(Exception):
    """A terminal candidate is not a Docker infrastructure outage."""


def probe_container(container, argv):
    try:
        return docker("exec", "--user", "0:0", container, *argv, timeout=10).stdout
    except RuntimeError:
        state = unique_json(docker("inspect", container).stdout)[0]["State"]
        if not state["Running"]:
            raise CandidateExited("Candidate exited before resource capture") from None
        raise


def kernel_resources(container, version="2", counter_files=None):
    raw = probe_container(container, PROBES[version] if counter_files is None else ["/bin/cat", *counter_files])
    try:
        lines = raw.decode("ascii").splitlines()
        if version == "1":
            # Exactly two single-integer files; anything else is a probe we do not understand.
            if len(lines) != 2:
                raise ValueError("Unexpected cgroup-v1 counter shape")
            cpu_ns, peak = (int(line) for line in lines)
            result = dict(cpu_usec=cpu_ns // 1000, memory_peak_bytes=peak)
        else:
            counters = dict(line.split() for line in lines[:-1])
            if len(counters) != len(lines) - 1:
                raise ValueError("Duplicate kernel counter")
            result = dict(cpu_usec=int(counters["usage_usec"]), memory_peak_bytes=int(lines[-1]))
    except (ValueError, KeyError, IndexError, UnicodeError) as error:
        raise RuntimeError(f"Missing or invalid cgroup-v{version} resource counters") from error
    if (not isinstance(result, dict) or set(result) != {"cpu_usec", "memory_peak_bytes"} or
            any(type(v) is not int or v < 0 for v in result.values()) or result["memory_peak_bytes"] == 0):
        raise RuntimeError(f"Missing or invalid cgroup-v{version} resource counters")
    return result


class WarmDockerSandbox(DockerSandbox):
    def __init__(self, image, evidence, *, seconds=15, memory_mib=512):
        super().__init__(image, evidence, seconds=seconds, memory_mib=memory_mib)
        self.cgroup_version = host_cgroup_version()
        # Resolved from the first container's mount table; v2 paths are fixed.
        self.counter_files = None if self.cgroup_version == "1" else list(COUNTER_FILES[self.cgroup_version])
        folder = Path(__file__).with_name("sandbox")
        self.identity.update(schema="evolution-warm-docker-v1", metric="host_request_to_response_seconds",
                             includes="Input transport, solve, copying and serialization; NOT isolated kernel time",
                             resource_metric="cgroup-v2 cumulative CPU and lifetime memory.peak, INCLUDING probes/startup",
                             worker_sha256=hashlib.sha256((folder / "warm_worker.py").read_bytes()).hexdigest(),
                             wire_sha256=hashlib.sha256((folder / "wire_codec.py").read_bytes()).hexdigest(),
                             codec_sha256=hashlib.sha256((folder / "worker.py").read_bytes()).hexdigest())
        # v2 IDENTITY IS LEFT BYTE-IDENTICAL: it is copied into manifests that are digested
        # and compared (warm_evaluator.py, warm_screening.py), so changing it would silently
        # change every existing v2 digest. Only a v1 host is marked, and differently.
        if self.cgroup_version == "1":
            self.identity.update(cgroup_version="1",
                                 resource_metric="cgroup-v1 cumulative cpuacct.usage and lifetime "
                                                 "memory.max_usage_in_bytes, INCLUDING probes/startup")
        (self.root / "environment.json").write_bytes(encode(self.identity))

    def command(self, name, bundle):
        command = super().command(name, bundle)
        command.insert(1, "--interactive")
        return command

    def _run(self, source, request, phase):
        raw_source, raw_request = source.encode(), encode(request)
        if (not raw_source or len(raw_source) > 65536 or len(raw_request) > MAX_REQUEST or
                set(request) != {"class", "problems"} or not isinstance(request["class"], str) or
                not isinstance(request["problems"], list) or phase not in ("search", "confirmation", "adversarial")):
            raise ValueError("Invalid bounded warm candidate/request/phase")
        run_id = "evolution-" + uuid.uuid4().hex
        directory = self.root / run_id
        bundle = directory / "input"
        bundle.mkdir(parents=True, exist_ok=False)
        folder = Path(__file__).with_name("sandbox")
        (bundle / "worker.py").write_bytes((folder / "warm_worker.py").read_bytes())
        (bundle / "worker_codec.py").write_bytes((folder / "worker.py").read_bytes())
        (bundle / "wire_codec.py").write_bytes((folder / "wire_codec.py").read_bytes())
        (bundle / "candidate.py").write_bytes(raw_source)
        (bundle / "metadata.json").write_bytes(encode({"class": request["class"]}))
        # Evidence is OUTSIDE the mounted input bundle, inaccessible to candidates.
        nonce = secrets.token_hex(32)
        payload = encode({"nonce": nonce, "problems": request["problems"]}) + b"\n"
        (directory / "request.json").write_bytes(raw_request)
        row = dict(id=run_id, phase=phase, image=self.image, status="dispatched", unknown_work=True,
                   source_sha256=hashlib.sha256(raw_source).hexdigest(), request_sha256=hashlib.sha256(raw_request).hexdigest(),
                   elapsed_seconds=None, startup_seconds=None, resources=None, resource_status="unmeasured")
        self.rows.append(row)
        container = process = None
        collected = [bytearray(), bytearray()]
        frames, threads = [], []
        signal, overflow = threading.Event(), threading.Event()
        lock = threading.Lock()
        started = time.monotonic()
        try:
            container = docker(*self.command(run_id, bundle)).stdout.decode().strip()
            if not re.fullmatch(r"[0-9a-f]{64}", container):
                raise RuntimeError("Invalid created-container identity")
            configuration = unique_json(docker("inspect", container).stdout)[0]
            self._verify_configuration(configuration, run_id, bundle)
            if not configuration["Config"]["OpenStdin"]:
                raise RuntimeError("Warm input channel not enforced")
            (directory / "configuration.json").write_bytes(encode(configuration))
            process = subprocess.Popen(["docker", "start", "--attach", "--interactive", container],
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            dispatched = time.monotonic()
            def drain(stream, destination, stdout):
                pending = bytearray()
                while True:
                    chunk = stream.read1(4096)
                    if not chunk:
                        signal.set()
                        return
                    arrived = time.monotonic()
                    with lock:
                        room = MAX_OUTPUT - len(destination)
                        destination.extend(chunk[:room])
                        if len(chunk) > room:
                            overflow.set()
                        if stdout and not overflow.is_set():
                            pending.extend(chunk)
                            while b"\n" in pending:
                                line, _, tail = pending.partition(b"\n")
                                pending[:] = tail
                                if len(frames) < 3:
                                    frames.append((bytes(line), arrived))
                                else:
                                    overflow.set()
                    signal.set()
            for stream, destination, stdout in ((process.stdout, collected[0], True), (process.stderr, collected[1], False)):
                thread = threading.Thread(target=drain, args=(stream, destination, stdout), daemon=True)
                thread.start()
                threads.append(thread)
            def frame(index):
                while time.monotonic() - dispatched < self.seconds and not overflow.is_set():
                    signal.clear()
                    with lock:
                        if len(frames) > index:
                            return frames[index]
                    if process.poll() is not None:
                        break
                    signal.wait(0.05)
                return None
            def parse(raw):
                try:
                    return unique_json(raw)
                except (ValueError, UnicodeError, RecursionError):
                    return None
            ready = frame(0)
            readiness = parse(ready[0]) if ready else None
            if ready is None:
                row["status"] = "output-limit" if overflow.is_set() else "timeout" if process.poll() is None else "candidate-failed"
            elif not isinstance(readiness, dict) or set(readiness) != {"ready"} or readiness["ready"] is not True:
                row["status"] = "invalid-output"
            else:
                row["startup_seconds"] = ready[1] - dispatched
                if self.counter_files is None:
                    self.counter_files = resolve_counter_files(container, self.cgroup_version)
                before = kernel_resources(container, self.cgroup_version, self.counter_files)
                row["kernel_before"] = before
                sent = time.monotonic()
                write_errors = []
                def send():
                    try:
                        process.stdin.write(payload)
                        process.stdin.flush()
                    except (OSError, ValueError) as error:
                        write_errors.append(type(error).__name__)
                    finally:
                        signal.set()
                writer = threading.Thread(target=send, daemon=True)
                writer.start()
                threads.append(writer)
                response = frame(1)
                if response is not None:
                    writer.join(timeout=max(0.1, self.seconds - (time.monotonic() - dispatched)))
                    if writer.is_alive() or write_errors:
                        response = None
                if response is None:
                    row["status"] = "output-limit" if overflow.is_set() else "timeout" if process.poll() is None else "candidate-failed"
                else:
                    value = parse(response[0])
                    if (response[1] <= sent or not isinstance(value, dict) or set(value) != {"nonce", "outputs"} or
                            value["nonce"] != nonce or not isinstance(value["outputs"], list)):
                        row["status"] = "invalid-output"
                    else:
                        row["elapsed_seconds"] = response[1] - sent
                        after = kernel_resources(container, self.cgroup_version, self.counter_files)
                        row["kernel_after"] = after
                        if after["cpu_usec"] < before["cpu_usec"] or after["memory_peak_bytes"] < before["memory_peak_bytes"]:
                            raise RuntimeError("Cgroup counters regressed")
                        row.update(output=value["outputs"], status="completed", resource_status="measured",
                                   resources=dict(cpu_seconds_through_response=after["cpu_usec"] / 1e6,
                                                  warm_cpu_seconds=(after["cpu_usec"] - before["cpu_usec"]) / 1e6,
                                                  peak_bytes_through_response=after["memory_peak_bytes"], observer_overhead_included=True))
                        process.stdin.write(b"\n")
                        process.stdin.flush()
                        try:
                            process.wait(timeout=max(0.1, self.seconds - (time.monotonic() - dispatched)))
                        except subprocess.TimeoutExpired:
                            row["status"] = "timeout"
            if process.poll() is None:
                docker("kill", container, check=False)
            process.wait(timeout=10)
            for thread in threads:
                thread.join(timeout=5)
            if any(thread.is_alive() for thread in threads):
                raise RuntimeError("Warm output channel did not terminate")
            state = unique_json(docker("inspect", container).stdout)[0]["State"]
            if state["Running"]:
                raise RuntimeError("Warm container did not terminate")
            if row["status"] == "completed" and (state["ExitCode"] != 0 or collected[1] or len(frames) != 2 or
                    bytes(collected[0]) != b"\n".join(f[0] for f in frames) + b"\n"):
                row["status"] = "invalid-output"
            begin = datetime.datetime.fromisoformat(state["StartedAt"].replace("Z", "+00:00"))
            end = datetime.datetime.fromisoformat(state["FinishedAt"].replace("Z", "+00:00"))
            row.update(unknown_work=False, state=state, lifecycle_seconds=(end - begin).total_seconds())
            return row
        except (CandidateExited, BrokenPipeError) as error:
            state = unique_json(docker("inspect", container).stdout)[0]["State"]
            if state["Running"]:
                row.update(status="infrastructure-failed", error=type(error).__name__)
                raise RuntimeError("Unexpected live-container protocol failure") from error
            begin = datetime.datetime.fromisoformat(state["StartedAt"].replace("Z", "+00:00"))
            end = datetime.datetime.fromisoformat(state["FinishedAt"].replace("Z", "+00:00"))
            row.update(status="candidate-failed", unknown_work=False, state=state,
                       lifecycle_seconds=(end - begin).total_seconds(), error=type(error).__name__)
            return row
        except (ValueError, UnicodeError, RecursionError) as error:
            # Invalid protocol is candidate failure, but still needs a terminal receipt.
            row.update(status="invalid-output", error=type(error).__name__)
            raise RuntimeError("Warm protocol failed; retain evidence and stop dispatch") from error
        except Exception as error:
            row.update(status="infrastructure-failed", error=type(error).__name__ + ": " + str(error)[:300])
            raise
        finally:
            try:
                if container is not None and re.fullmatch(r"[0-9a-f]{64}", container):
                    owned = unique_json(docker("inspect", container).stdout)[0]
                    if owned["Id"] != container or owned["Config"]["Labels"].get(OWNER) != run_id:
                        raise RuntimeError("Refusing cleanup after ownership changed")
                    docker("rm", "--force", container)
                if process is not None:
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=10)
                    for thread in threads:
                        thread.join(timeout=5)
                    for stream in (process.stdin, process.stdout, process.stderr):
                        try:
                            stream.close()
                        except BrokenPipeError:
                            pass
            except Exception:
                row.update(status="cleanup-failed", unknown_work=True)
                raise
            finally:
                row["supervisor_seconds"] = time.monotonic() - started
                (directory / "stdout.bin").write_bytes(collected[0])
                (directory / "stderr.bin").write_bytes(collected[1])
                (directory / "receipt.json").write_bytes(encode(row))
