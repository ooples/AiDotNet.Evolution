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
    while open_fds:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            timed_out = True
            break
        ready, _, _ = select.select(open_fds, [], [], min(remaining, 0.5))
        for fd in ready:
            data = os.read(fd, 65536)
            if not data:
                open_fds.remove(fd)
                continue
            if sizes[fd] < caps[fd]:
                chunks[fd].append(data)
                sizes[fd] += len(data)
    if timed_out:
        try:
            os.killpg(pid, signal.SIGKILL)
        except OSError:
            pass
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
