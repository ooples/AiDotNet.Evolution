# In-container supervisor for ContainerExecutionEngine. It is the container's PID 1 and runs one candidate at a time,
# framed on standard input and output: a 4-byte big-endian length, then UTF-8 JSON.
#   ready:    {"ready", "pid", "uid", "seccomp", "no_new_privs", "cap_eff", "hardened"}
#   request:  {"source", "stdin", "file", "argv", "time_limit", "cpu_seconds", "max_stdout", "max_stderr"}
#             argv null runs a Python candidate in a forked child of this interpreter; otherwise argv is exec'd.
#   response: {"exit", "stdout", "stderr", "stdout_truncated", "stderr_truncated", "timed_out", "memory_exceeded",
#              "cpu_exceeded", "clean"}
# Between candidates every other process in the container is killed and the workdir is emptied; "clean" reports
# whether both succeeded, and the engine replaces the container when it is false.
#
# PID 1 of a PID namespace receives no signal from inside the namespace unless it installed a handler for it, so the
# candidate (same user) cannot kill the supervisor; the only handler Python installs, SIGINT, is reset below. The
# supervisor also makes itself undumpable, so a same-user candidate cannot open /proc/1/fd to read requests or forge
# replies.
import json
import os
import select
import signal
import struct
import sys
import threading
import time

WORK = "/work"
MEMORY_EXIT = 125


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
    return (text, False) if len(text) <= limit else (text[:limit], True)


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
        return MEMORY_EXIT
    except BaseException:
        traceback.print_exc()
        return 1


def child(request, path, in_read, out_write, err_write):
    code = 1
    try:
        os.setsid()
        os.dup2(in_read, 0)
        os.dup2(out_write, 1)
        os.dup2(err_write, 2)
        # fork() copies every descriptor, including the protocol's; close them so the candidate cannot reach them.
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


def sweep():
    """Kills every process in the container except this one; True once none is left."""
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        try:
            os.kill(-1, signal.SIGKILL)  # every process this user may signal, which is all of them but PID 1
        except ProcessLookupError:
            reap()
            return True
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


def run(request):
    path = os.path.join(WORK, request["file"])
    with open(path, "w", encoding="utf-8") as source:
        source.write(request["source"])
    oom_before = oom_kills()
    in_read, in_write = os.pipe()
    out_read, out_write = os.pipe()
    err_read, err_write = os.pipe()
    pid = os.fork()
    if pid == 0:
        child(request, path, in_read, out_write, err_write)
    for fd in (in_read, out_write, err_write):
        os.close(fd)
    writer = threading.Thread(target=feed, args=(in_write, request["stdin"].encode("utf-8")), daemon=True)
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
    while True:
        waited, exit_status = os.waitpid(pid, os.WNOHANG)
        if waited:
            status = exit_status
            while open_fds and drain(0):
                pass
            break
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            timed_out = True
            break
        if open_fds:
            drain(min(remaining, 0.05))
        else:
            time.sleep(min(remaining, 0.001))

    clean = sweep()
    if status is None:
        try:
            _, status = os.waitpid(pid, 0)
        except ChildProcessError:
            status = 9  # already reaped by the sweep; it was killed
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
    memory = memory or code == MEMORY_EXIT
    cpu = signaled and os.WTERMSIG(status) == signal.SIGXCPU
    stdout = b"".join(chunks[out_read]).decode("utf-8", "replace")
    stderr = b"".join(chunks[err_read]).decode("utf-8", "replace")
    return code, stdout, stderr, timed_out, memory, cpu, clean


def main():
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    requests = sys.stdin.buffer
    replies = os.fdopen(os.dup(1), "wb")
    null = os.open(os.devnull, os.O_RDWR)
    os.dup2(null, 1)
    os.dup2(null, 2)
    hardened = harden()
    write_frame(replies, {
        "ready": True, "pid": os.getpid(), "uid": os.getuid(), "hardened": hardened,
        "seccomp": status_field("Seccomp"), "no_new_privs": status_field("NoNewPrivs"), "cap_eff": status_field("CapEff"),
        "clean": wipe(),
    })
    while True:
        request = read_frame(requests)
        if request is None:
            return
        code, stdout, stderr, timed_out, memory, cpu, clean = run(request)
        stdout, stdout_cut = bound(stdout, request["max_stdout"])
        stderr, stderr_cut = bound(stderr, request["max_stderr"])
        write_frame(replies, {
            "exit": code, "stdout": stdout, "stderr": stderr,
            "stdout_truncated": stdout_cut, "stderr_truncated": stderr_cut,
            "timed_out": timed_out, "memory_exceeded": memory, "cpu_exceeded": cpu, "clean": clean,
        })


main()
