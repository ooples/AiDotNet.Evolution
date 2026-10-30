# In-container supervisor for ContainerExecutionEngine. It is the container's PID 1 and runs one candidate at a time,
# framed on standard input and output: a 4-byte big-endian length, then UTF-8 JSON.
#   ready:    {"ready", "pid", "uid", "seccomp", "no_new_privs", "cap_eff", "hardened"}
#   request:  {"source", "stdin", "file", "argv", "time_limit", "cpu_seconds", "max_stdout", "max_stderr"}
#             argv null runs a Python candidate in a forked child of the zygote (below); otherwise argv is exec'd.
#   response: {"exit", "stdout", "stderr", "stdout_truncated", "stderr_truncated", "timed_out", "memory_exceeded",
#              "cpu_exceeded", "clean"}
# Between candidates every other process in the container is killed and the workdir is emptied; "clean" reports
# whether both succeeded, and the engine replaces the container when it is false.
#
# PID 1 of a PID namespace receives no signal from inside the namespace unless it installed a handler for it, so the
# candidate (same user) cannot kill the supervisor; the only handler Python installs, SIGINT, is reset below. The
# supervisor also makes itself undumpable, so a same-user candidate cannot open /proc/1/fd to read requests or forge
# replies.
#
# Candidates are not forked from the supervisor: its memory holds every earlier request (sources, stdin that may be
# hidden test inputs) and output, and a fork copies all of it, so a later candidate could read an earlier one's data
# out of its own address space (E-2 review: 721 copies of a 64 KB stdin). They are forked from a zygote, a fresh
# interpreter (fork and exec of this same script) that never reads a request: each candidate receives its own request
# over a pipe of its own. The zygote is spared by the sweep, and replaced whenever a candidate could have tampered
# with it (it died, was stopped, or its limits, priority or affinity changed). A zygote stopped while a candidate runs is killed at once,
import array
import json
import os
import select
import signal
import socket
import struct
import sys
import threading
import time

WORK = "/work"
# A forked Python candidate that raises MemoryError ends by this signal rather than by an exit code, so a candidate that
# exits 125 (or any code) of its own accord is never read as out of memory. The candidate shares the process and could
# raise it itself, but that only misclassifies its own failure.
MEMORY_SIGNAL = signal.SIGUSR2
MEMORY_ERROR = object()


def read_exactly(stream, count):
    data = b""
    while len(data) < count:
        block = stream.read(count - len(data))
        if not block:
            return None
        data += block
    return data


def read_frame(stream):
    header = read_exactly(stream, 4)
    if header is None:
        return None
    (length,) = struct.unpack(">I", header)
    body = read_exactly(stream, length)
    return None if body is None else json.loads(body.decode("utf-8"))


def write_frame(stream, message):
    body = json.dumps(message).encode("utf-8")
    stream.write(struct.pack(">I", len(body)) + body)
    stream.flush()


def bound(text, limit):
    # In UTF-16 units, as .NET counts MaxStdOutChars; a cut never splits a surrogate pair.
    if len(text.encode("utf-16-le")) // 2 <= limit:
        return text, False
    units = 0
    for index, char in enumerate(text):
        units += 2 if ord(char) > 0xFFFF else 1
        if units > limit:
            return text[:index], True
    return text, False


def status_field(name):
    try:
        with open("/proc/self/status", encoding="ascii") as status:
            for line in status:
                if line.startswith(name + ":"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return None


def harden():
    try:
        import ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        return libc.prctl(4, 0, 0, 0, 0) == 0  # PR_SET_DUMPABLE, 0
    except Exception:
        return False


def oom_kills():
    # cgroup v2 names the container's own counter memory.events; cgroup v1 reports it in memory.oom_control.
    for path in ("/sys/fs/cgroup/memory.events", "/sys/fs/cgroup/memory/memory.oom_control"):
        try:
            with open(path, encoding="ascii") as events:
                for line in events:
                    parts = line.split()
                    if len(parts) == 2 and parts[0] == "oom_kill":
                        return int(parts[1])
        except (OSError, ValueError):
            continue
    return None


def max_descriptor():
    try:
        limit = os.sysconf("SC_OPEN_MAX")
    except (ValueError, OSError):
        limit = -1
    return limit if limit > 0 else 65536


def run_python(path, source):
    """Runs a Python candidate as __main__ in this (forked) interpreter and returns its exit code."""
    import traceback
    sys.argv = [path]
    try:
        exec(compile(source, path, "exec"), {"__name__": "__main__", "__builtins__": __builtins__, "__file__": path})
        return 0
    except SystemExit as stop:
        code = stop.code
        if code is None:
            return 0
        if isinstance(code, int):
            return code
        print(code, file=sys.stderr)
        return 1
    except MemoryError:
        return MEMORY_ERROR
    except BaseException:
        traceback.print_exc()
        return 1


def child(in_read, out_write, err_write, request_read):
    code = 1
    try:
        os.setsid()
        with os.fdopen(request_read, "rb") as source:
            request = json.loads(source.read().decode("utf-8"))
        path = os.path.join(WORK, request["file"])
        os.dup2(in_read, 0)
        os.dup2(out_write, 1)
        os.dup2(err_write, 2)
        # fork() copies every descriptor, including the zygote's socket; close them so the candidate cannot reach them.
        os.closerange(3, max_descriptor())
        try:
            with open("/proc/self/oom_score_adj", "w", encoding="ascii") as adj:
                adj.write("1000")  # the kernel's out-of-memory killer takes the candidate, not the supervisor
        except OSError:
            pass
        import resource
        if request["cpu_seconds"] > 0:
            resource.setrlimit(resource.RLIMIT_CPU, (request["cpu_seconds"], request["cpu_seconds"] + 1))
        os.chdir(WORK)
        argv = request["argv"]
        if argv:
            # Python ignores these two; an exec'd program must start with the defaults, as it would from a shell.
            signal.signal(signal.SIGPIPE, signal.SIG_DFL)
            signal.signal(signal.SIGXFSZ, signal.SIG_DFL)
            try:
                os.execvp(argv[0], argv)
            except OSError as error:
                os.write(2, ("cannot start " + argv[0] + ": " + str(error) + "\n").encode("utf-8", "replace"))
                os._exit(127)
        sys.stdin = os.fdopen(0, "r", encoding="utf-8")
        sys.stdout = os.fdopen(1, "w", encoding="utf-8", buffering=1)
        sys.stderr = os.fdopen(2, "w", encoding="utf-8", buffering=1)
        code = run_python(path, request["source"])
        sys.stdout.flush()
        sys.stderr.flush()
        if code is MEMORY_ERROR:
            signal.signal(MEMORY_SIGNAL, signal.SIG_DFL)
            signal.pthread_sigmask(signal.SIG_UNBLOCK, [MEMORY_SIGNAL])
            os.kill(os.getpid(), MEMORY_SIGNAL)
            code = 1  # not reached: the default action of the signal ends the process
    except BaseException:
        code = 1
    os._exit(code if 0 <= code <= 255 else 1)


def feed(fd, data):
    try:
        view = memoryview(data)
        while view:
            written = os.write(fd, view)
            view = view[written:]
    except OSError:
        pass  # the candidate exited or closed its input
    finally:
        try:
            os.close(fd)
        except OSError:
            pass


def reap():
    while True:
        try:
            pid, _ = os.waitpid(-1, os.WNOHANG)
        except ChildProcessError:
            return
        if pid == 0:
            return


def sweep(spare):
    """Kills every process in the container but this one and the zygote; True once none is left."""
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        others = [pid for pid in (int(name) for name in os.listdir("/proc") if name.isdigit()) if pid not in (1, os.getpid(), spare)]
        if not others:
            reap()
            return True
        for pid in others:
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except OSError:
                return False
        reap()
        time.sleep(0.001)
    return False


def wipe():
    """Empties the workdir, including anything a candidate made unreadable; True when it is empty."""
    import shutil

    def unlock(directory):
        # The candidate's user owns everything it made, so the supervisor (the same user) can always restore access.
        os.chmod(directory, 0o700)
        with os.scandir(directory) as entries:
            for entry in entries:
                if entry.is_dir(follow_symlinks=False):
                    unlock(entry.path)

    try:
        os.chmod(WORK, 0o700)
        for name in os.listdir(WORK):
            path = os.path.join(WORK, name)
            if os.path.isdir(path) and not os.path.islink(path):
                unlock(path)
                shutil.rmtree(path)
            else:
                os.unlink(path)
        return not os.listdir(WORK)
    except (OSError, RecursionError):
        return False  # a tree too deep or too locked to empty: the engine replaces the container


ZYGOTE = "--zygote"


def zygote_main(descriptor):
    """The zygote's loop: one fork per candidate, then that candidate's pid and exit status back to the supervisor."""
    harden()  # exec made it dumpable again
    channel = socket.socket(fileno=descriptor)
    import resource  # noqa: F401  imported once here, so every candidate's fork starts with it loaded
    import traceback  # noqa: F401
    while True:
        try:
            message, descriptors = receive_descriptors(channel)
        except OSError:
            os._exit(0)
        if not message or len(descriptors) != 4:
            os._exit(0)
        pid = os.fork()
        if pid == 0:
            channel.close()
            child(*descriptors)
        for fd in descriptors:
            os.close(fd)
        channel.sendall(struct.pack(">i", pid))
        try:
            _, status = os.waitpid(pid, 0)
        except ChildProcessError:
            status = 9
        channel.sendall(struct.pack(">i", status))


def send_descriptors(channel, descriptors):
    # SCM_RIGHTS by hand rather than socket.send_fds, which needs Python 3.9; any image's python3 runs this.
    channel.sendmsg([b"F"], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array("i", descriptors))])


def receive_descriptors(channel):
    descriptors = array.array("i")
    message, ancillary, _, _ = channel.recvmsg(1, socket.CMSG_LEN(16 * descriptors.itemsize))
    for level, kind, data in ancillary:
        if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
            descriptors.frombytes(data[:len(data) - len(data) % descriptors.itemsize])
    return message, list(descriptors)


def own_script():
    # Started as `python -u -c <script>`; the zygote is this script run again, so its memory starts empty.
    with open("/proc/self/cmdline", "rb") as cmdline:
        argv = cmdline.read().split(b"\0")
    return argv[argv.index(b"-c") + 1].decode("utf-8")


def zygote_identity(pid):
    """What a same-user candidate could change about the zygote: its state, priority, limits and affinity."""
    try:
        with open("/proc/%d/stat" % pid, encoding="ascii") as stat:
            fields = stat.read().rsplit(")", 1)[1].split()
        with open("/proc/%d/limits" % pid, encoding="ascii") as limits:
            limit_text = limits.read()
        with open("/proc/%d/status" % pid, encoding="ascii") as status:
            allowed = [line for line in status if line.startswith(("Cpus_allowed:", "Mems_allowed:"))]
    except OSError:
        return None
    # fields[0] is the state, [16] the nice value.
    return (fields[0], fields[16], limit_text, tuple(allowed))


class Zygote:
    def __init__(self):
        self.script = own_script()
        self.pid = 0
        self.channel = None
        self.identity = None

    @property
    def started(self):
        return self.pid != 0

    def start(self):
        ours, theirs = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
        pid = os.fork()
        if pid == 0:
            try:
                # Nothing of the supervisor's but the zygote's own socket crosses the exec, not even the request stream.
                quiet = os.open(os.devnull, os.O_RDWR)
                for standard in (0, 1, 2):
                    os.dup2(quiet, standard)
                os.set_inheritable(theirs.fileno(), True)  # sockets are close-on-exec by default
                os.closerange(3, theirs.fileno())
                os.closerange(theirs.fileno() + 1, max_descriptor())
                os.execv(sys.executable, [sys.executable, "-u", "-c", self.script, ZYGOTE, str(theirs.fileno())])
            finally:
                os._exit(127)
        theirs.close()
        self.pid, self.channel = pid, ours
        self.identity = None

    def stop(self):
        try:
            os.kill(self.pid, signal.SIGKILL)
        except OSError:
            pass
        try:
            os.waitpid(self.pid, 0)
        except ChildProcessError:
            pass
        self.channel.close()

    def check(self):
        """Replaces the zygote if it died or anything about it changed since it last forked a candidate cleanly."""
        identity = zygote_identity(self.pid)
        alive = identity is not None and identity[0] not in ("T", "t", "Z", "X", "x")
        if alive and (self.identity is None or identity == self.identity):
            self.identity = identity
            return
        self.stop()
        self.start()

    def stalled(self):
        identity = zygote_identity(self.pid)
        return identity is None or identity[0] in ("T", "t", "Z", "X", "x")

    def kill(self):
        try:
            os.kill(self.pid, signal.SIGKILL)
            os.waitpid(self.pid, 0)  # reaped, so its children are this process's before they are waited on
        except (ProcessLookupError, ChildProcessError):
            pass

    def spawn(self, descriptors):
        """Asks the zygote to fork a candidate; returns its pid, or None when the zygote is gone."""
        try:
            send_descriptors(self.channel, descriptors)
            return struct.unpack(">i", read_socket(self.channel, 4))[0]
        except (OSError, struct.error, TypeError):
            return None


def read_socket(channel, count):
    data = b""
    while len(data) < count:
        block = channel.recv(count - len(data))
        if not block:
            return None
        data += block
    return data


def run(request, zygote):
    path = os.path.join(WORK, request["file"])
    with open(path, "w", encoding="utf-8") as source:
        source.write(request["source"])
    oom_before = oom_kills()
    in_read, in_write = os.pipe()
    out_read, out_write = os.pipe()
    err_read, err_write = os.pipe()
    request_read, request_write = os.pipe()
    # The container's first candidate is forked from this process: its memory then holds no request but this one, so the
    # candidate inherits nothing it was not sent. A cold container runs only that one and never pays for a zygote.
    direct = not zygote.started
    if direct:
        pid = os.fork()
        if pid == 0:
            # This process's ends, or the request pipe never reaches end of file for the child that reads it.
            for fd in (in_write, out_read, err_read, request_write):
                os.close(fd)
            child(in_read, out_write, err_write, request_read)
    else:
        zygote.check()
        pid = zygote.spawn([in_read, out_write, err_write, request_read])
        if pid is None:  # the zygote failed between its check and this fork; one fresh zygote, then give up
            zygote.stop()
            zygote.start()
            pid = zygote.spawn([in_read, out_write, err_write, request_read])
    for fd in (in_read, out_write, err_write, request_read):
        os.close(fd)
    if pid is None:
        for fd in (in_write, out_read, err_read, request_write):
            os.close(fd)
        return 1, "", "the candidate could not be started\n", False, False, False, False
    payload = json.dumps({key: request[key] for key in ("source", "file", "argv", "cpu_seconds")}).encode("utf-8")
    stdin = request["stdin"].encode("utf-8")

    def write_all():
        feed(request_write, payload)
        feed(in_write, stdin)

    writer = threading.Thread(target=write_all, daemon=True)
    writer.start()

    chunks = {out_read: [], err_read: []}
    sizes = {out_read: 0, err_read: 0}
    caps = {out_read: request["max_stdout"] * 4 + 4, err_read: request["max_stderr"] * 4 + 4}
    deadline = time.monotonic() + request["time_limit"]
    open_fds = [out_read, err_read]
    timed_out = False
    status = None

    def drain(timeout):
        ready, _, _ = select.select(open_fds, [], [], timeout)
        for fd in ready:
            data = os.read(fd, 65536)
            if not data:
                open_fds.remove(fd)
                continue
            if sizes[fd] < caps[fd]:
                chunks[fd].append(data)
                sizes[fd] += len(data)
        return bool(ready)

    # Wait for the candidate to exit, not for its pipes to close: a descendant can hold them open after it finished.
    # The zygote reports the exit; a candidate that killed the zygote is this process's child instead, and is waited
    # on directly.
    channel = zygote.channel
    orphaned = direct  # a directly forked candidate is this process's child, waited on directly
    probed = time.monotonic()
    while True:
        if not orphaned and time.monotonic() - probed >= 0.02:
            probed = time.monotonic()
            if zygote.stalled():
                # A stopped zygote never reaps or reports; killing it hands the candidate to this process.
                zygote.kill()
                orphaned = True
        if orphaned:
            try:
                waited, exit_status = os.waitpid(pid, os.WNOHANG)
            except ChildProcessError:
                waited, exit_status = pid, 9
            if waited:
                status = exit_status
        else:
            ready, _, _ = select.select([channel], [], [], 0)
            if ready:
                reply = read_socket(channel, 4)
                if reply is None:
                    orphaned = True
                    continue
                status = struct.unpack(">i", reply)[0]
        if status is not None:
            while open_fds and drain(0):
                pass
            break
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            timed_out = True
            break
        if open_fds:
            drain(min(remaining, 0.05 if orphaned else 0.001))
        else:
            time.sleep(min(remaining, 0.001))

    clean = sweep(zygote.pid)
    if status is None and not orphaned:
        # Killed by the sweep: the zygote reaps it and reports.
        ready, _, _ = select.select([channel], [], [], 2)
        reply = read_socket(channel, 4) if ready else None
        status = struct.unpack(">i", reply)[0] if reply else 9
    if status is None:
        status = 9  # an orphan already reaped by the sweep; it was killed
    writer.join(timeout=1)
    for fd in (out_read, err_read):
        os.close(fd)
    clean = wipe() and clean and not writer.is_alive()

    signaled = os.WIFSIGNALED(status)
    code = -os.WTERMSIG(status) if signaled else os.WEXITSTATUS(status)
    oom_after = oom_kills()
    killed = signaled and os.WTERMSIG(status) == signal.SIGKILL and not timed_out
    if oom_before is not None and oom_after is not None:
        memory = oom_after > oom_before
    else:
        memory = killed  # no counter to read: nothing in the container but the OOM killer sends SIGKILL first
    # MEMORY_SIGNAL is the MemoryError report of a forked Python candidate only; an exec'd program may raise it itself.
    memory = memory or (not request["argv"] and signaled and os.WTERMSIG(status) == MEMORY_SIGNAL)
    cpu = signaled and os.WTERMSIG(status) == signal.SIGXCPU
    stdout = b"".join(chunks[out_read]).decode("utf-8", "replace")
    stderr = b"".join(chunks[err_read]).decode("utf-8", "replace")
    return code, stdout, stderr, timed_out, memory, cpu, clean


def main():
    if len(sys.argv) == 3 and sys.argv[1] == ZYGOTE:
        zygote_main(int(sys.argv[2]))
        return
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    requests = sys.stdin.buffer
    replies = os.fdopen(os.dup(1), "wb")
    null = os.open(os.devnull, os.O_RDWR)
    os.dup2(null, 1)
    os.dup2(null, 2)
    hardened = harden()
    zygote = Zygote()
    # A candidate runs as this user, so it can prlimit, renice or re-pin this process, and a zygote started later would
    # inherit the change. Nothing but a candidate changes them, so a difference costs the container.
    baseline = zygote_identity(os.getpid())[1:]
    write_frame(replies, {
        "ready": True, "pid": os.getpid(), "uid": os.getuid(), "hardened": hardened,
        "seccomp": status_field("Seccomp"), "no_new_privs": status_field("NoNewPrivs"), "cap_eff": status_field("CapEff"),
        "clean": wipe(),
    })
    while True:
        request = read_frame(requests)
        if request is None:
            return
        code, stdout, stderr, timed_out, memory, cpu, clean = run(request, zygote)
        identity = zygote_identity(os.getpid())
        clean = clean and identity is not None and identity[1:] == baseline
        stdout, stdout_cut = bound(stdout, request["max_stdout"])
        stderr, stderr_cut = bound(stderr, request["max_stderr"])
        write_frame(replies, {
            "exit": code, "stdout": stdout, "stderr": stderr,
            "stdout_truncated": stdout_cut, "stderr_truncated": stderr_cut,
            "timed_out": timed_out, "memory_exceeded": memory, "cpu_exceeded": cpu, "clean": clean,
        })
        if not zygote.started:
            zygote.start()  # after the reply, so the first candidate's latency does not include it


main()
