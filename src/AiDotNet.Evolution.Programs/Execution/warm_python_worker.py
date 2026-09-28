# Warm Python worker for WarmPythonExecutionEngine. One request at a time, framed on standard input and output:
# a 4-byte big-endian length, then UTF-8 JSON.
#   request:  {"source", "stdin", "time_limit", "memory_bytes", "cpu_seconds", "max_stdout", "max_stderr", "fork"}
#   response: {"exit", "stdout", "stderr", "stdout_truncated", "stderr_truncated", "timed_out", "memory_exceeded",
#              "cpu_exceeded", "recycle"}
# fork=true runs each candidate in a forked child (its own process, limits and process group). fork=false runs it in
# this interpreter, in a fresh namespace; the engine recycles the worker after a timeout, a crash, or N candidates.
import io
import json
import os
import struct
import sys
import tempfile
import time
import traceback

MEMORY_EXIT = 125


def read_frame(stream):
    header = stream.read(4)
    if len(header) < 4:
        return None
    (length,) = struct.unpack(">I", header)
    return json.loads(stream.read(length).decode("utf-8"))


def write_frame(stream, message):
    body = json.dumps(message).encode("utf-8")
    stream.write(struct.pack(">I", len(body)) + body)
    stream.flush()


def bound(text, limit):
    return (text, False) if len(text) <= limit else (text[:limit], True)


def run_candidate(source, stdin_text):
    """Runs a candidate as __main__ and returns its exit code; output goes to the current sys.stdout/stderr."""
    sys.stdin = io.StringIO(stdin_text)
    try:
        exec(compile(source, "<candidate>", "exec"), {"__name__": "__main__", "__builtins__": __builtins__})
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


def max_descriptor():
    try:
        limit = os.sysconf("SC_OPEN_MAX")
    except (ValueError, OSError):
        limit = -1
    return limit if limit > 0 else 65536


def forked(request):
    import resource
    import select
    import signal

    out_read, out_write = os.pipe()
    err_read, err_write = os.pipe()
    workspace = tempfile.mkdtemp(prefix="warm-")
    pid = os.fork()
    if pid == 0:
        try:
            os.setsid()
            os.close(out_read)
            os.close(err_read)
            os.dup2(out_write, 1)
            os.dup2(err_write, 2)
            # fork() copies every open descriptor, whatever its inheritable flag, including the protocol's request
            # and reply pipes. Close all of them so a candidate cannot read later requests or forge its own reply.
            devnull = os.open(os.devnull, os.O_RDONLY)
            os.dup2(devnull, 0)
            os.closerange(3, max_descriptor())
            sys.stdout = os.fdopen(1, "w", encoding="utf-8", buffering=1)
            sys.stderr = os.fdopen(2, "w", encoding="utf-8", buffering=1)
            os.chdir(workspace)
            if request["memory_bytes"] > 0:
                resource.setrlimit(resource.RLIMIT_AS, (request["memory_bytes"], request["memory_bytes"]))
            if request["cpu_seconds"] > 0:
                resource.setrlimit(resource.RLIMIT_CPU, (request["cpu_seconds"], request["cpu_seconds"] + 1))
            code = run_candidate(request["source"], request["stdin"])
            sys.stdout.flush()
            sys.stderr.flush()
        except BaseException:
            code = 1
        os._exit(code if 0 <= code <= 255 else 1)

    os.close(out_write)
    os.close(err_write)
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

    # Wait for the candidate to exit, not for its pipes to close: a descendant that inherited them could hold them
    # open past a candidate that already finished, and that is not a timeout.
    while True:
        waited, exit_status = os.waitpid(pid, os.WNOHANG)
        if waited:
            status = exit_status
            # Whatever the candidate wrote before exiting is already in the pipes; take it without waiting on
            # descendants that may still hold them.
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
            # Both pipes are closed, so exit is imminent; poll briefly rather than add latency to every candidate.
            time.sleep(min(remaining, 0.001))
    # End the candidate's process group: the candidate itself on a timeout, and any descendants in either case.
    try:
        os.killpg(pid, signal.SIGKILL)
    except OSError:
        pass
    if status is None:
        _, status = os.waitpid(pid, 0)
    for fd in (out_read, err_read):
        os.close(fd)
    try:
        import shutil
        shutil.rmtree(workspace, ignore_errors=True)
    except Exception:
        pass
    signaled = os.WIFSIGNALED(status)
    code = -os.WTERMSIG(status) if signaled else os.WEXITSTATUS(status)
    stdout = b"".join(chunks[out_read]).decode("utf-8", "replace")
    stderr = b"".join(chunks[err_read]).decode("utf-8", "replace")
    return code, stdout, stderr, timed_out, code == MEMORY_EXIT, signaled and os.WTERMSIG(status) == signal.SIGXCPU


def reused(request):
    out, err = io.StringIO(), io.StringIO()
    saved = sys.stdout, sys.stderr, sys.stdin
    sys.stdout, sys.stderr = out, err
    try:
        code = run_candidate(request["source"], request["stdin"])
    finally:
        sys.stdout, sys.stderr, sys.stdin = saved
    return code, out.getvalue(), err.getvalue(), False, code == MEMORY_EXIT, False


def main():
    requests = sys.stdin.buffer
    # Keep the protocol stream private: candidates and anything they start must not write into it.
    replies = os.fdopen(os.dup(1), "wb")
    null = os.open(os.devnull, os.O_WRONLY)
    os.dup2(null, 1)
    write_frame(replies, {"ready": True, "pid": os.getpid()})
    while True:
        request = read_frame(requests)
        if request is None:
            return
        code, stdout, stderr, timed_out, memory, cpu = (forked if request["fork"] else reused)(request)
        stdout, stdout_cut = bound(stdout, request["max_stdout"])
        stderr, stderr_cut = bound(stderr, request["max_stderr"])
        write_frame(replies, {
            "exit": code, "stdout": stdout, "stderr": stderr,
            "stdout_truncated": stdout_cut, "stderr_truncated": stderr_cut,
            "timed_out": timed_out, "memory_exceeded": memory, "cpu_exceeded": cpu,
            # A reused interpreter that ran out of memory may be unusable, so it asks to be replaced.
            "recycle": (not request["fork"]) and (memory or code != 0),
        })


if __name__ == "__main__":
    main()
