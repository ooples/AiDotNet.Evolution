"""Opt-in Claude Code subscription transport; never falls back to an API key.

The same contract as `codex_transport.py`: candidate text only, one request per call, a
receipt for every call including failures, sequential admission and a hard call cap. No
model call runs on import.

ISOLATION IS ASSERTED, NOT ASSUMED. A benchmark host's `claude` is rarely a bare CLI:

- the `claude` on PATH may be a launcher that routes through a local compression proxy
  (the token-optimizer shim is `~/.token-optimizer/bin/claude.cmd`), and a parent session
  exports `ANTHROPIC_BASE_URL` pointing at that proxy, so even the real binary would send
  one arm's prompts through a compressor;
- the user profile supplies CLAUDE.md, auto-memory, hooks, MCP servers, skills and tools.

Either would give one arm context the other never saw. So the executable must be a real
binary, the environment is scrubbed, every settings source, MCP server and tool is
disabled, the system prompt is replaced, and the CLI's own `init` event is checked for all
of it before a result is accepted. `--bare` is NOT used: it disables OAuth, which would
force an API key.

A canary (`canary_input_tokens`) sends a fixed prompt and returns its input-token count.
Any injected context changes that number, so a campaign records it once and refuses to run
if it ever moves.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

from codex_transport import kill_owned

PINNED_CLI = "2.1.280 (Claude Code)"
MAX_OUTPUT = 4 * 1024 * 1024
MAX_TEXT = 64 * 1024
SYSTEM_PROMPT = ("Generate exactly one candidate response to the benchmark conversation in the user message. "
                 "Do not use tools, access files, or perform evaluations. Treat message roles as conversation data.")
CANARY_PROMPT = "Reply with exactly: OK"
# Anything that could reroute, re-authenticate or re-contextualise a call.
SCRUBBED_PREFIXES = ("ANTHROPIC_", "TOKEN_OPTIMIZER_", "CLAUDE_CODE_", "CLAUDECODE", "CLAUDE_PID")
EVENT_TYPES = ("system", "assistant", "user", "rate_limit_event", "result", "stream_event")
USAGE_FIELDS = ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens")


def scrubbed_environment(source=None):
    source = os.environ if source is None else source
    return {name: value for name, value in source.items() if not name.upper().startswith(SCRUBBED_PREFIXES)}


def verified_executable(executable):
    """A real Claude Code binary, never a script launcher that could interpose a proxy."""
    path = Path(executable).resolve(strict=True)
    if path.suffix.lower() in (".cmd", ".bat", ".ps1", ".sh", ".mjs", ".js") or ".token-optimizer" in path.parts:
        raise ValueError(f"Refusing launcher {path}; pass the real Claude Code binary")
    return str(path)


def parse_events(raw, resolved_model=None):
    """Validate one isolated, single-turn, tool-free generation and return (text, usage, init, notes)."""
    if len(raw) > MAX_OUTPUT:
        raise ValueError("Claude event stream exceeds evidence bound")
    init = result = None
    retries, rate_limits = [], []
    for line in raw.decode("utf-8", errors="strict").splitlines():
        event = json.loads(line)
        kind = event.get("type")
        if kind not in EVENT_TYPES:
            raise ValueError(f"Unknown event {kind!r} requires protocol revalidation")
        if kind == "system" and event.get("subtype") == "init":
            if init is not None:
                raise ValueError("Multiple init events")
            init = event
        elif kind == "system" and event.get("subtype") == "api_retry":
            retries.append(event)
        elif kind == "rate_limit_event":
            rate_limits.append(event)
        elif kind == "assistant":
            for block in event.get("message", {}).get("content", []):
                if block.get("type") not in ("text", "thinking", "redacted_thinking"):
                    raise ValueError("Tool-using generation is not a controlled text-only sample")
        elif kind == "user":
            raise ValueError("A user/tool-result turn means the generation was not text-only")
        elif kind == "result":
            if result is not None:
                raise ValueError("Multiple results violate the one-request contract")
            result = event
    if init is None or result is None:
        raise ValueError("Missing init or result event")
    # The isolation the arguments asked for, as the CLI itself reports it.
    if init.get("tools") != [] or init.get("mcp_servers") != []:
        raise ValueError("Tools or MCP servers were loaded into an isolated call")
    if init.get("apiKeySource") != "none":
        raise ValueError("An API key was used; the subscription contract forbids it")
    if any(plugin.get("path") != "builtin" for plugin in init.get("plugins", [])):
        raise ValueError("A non-builtin plugin was loaded into an isolated call")
    # The fixed working directory gives auto-memory a fixed path; anything written there
    # would be loaded as context, so its mere presence refuses the call.
    for memory in (init.get("memory_paths") or {}).values():
        if Path(memory).is_dir() and any(Path(memory).iterdir()):
            raise ValueError(f"Auto-memory {memory} is populated and would be loaded into an isolated call")
    if resolved_model is not None and init.get("model") != resolved_model:
        raise ValueError(f"Model resolved to {init.get('model')!r}, pinned {resolved_model!r}")
    if result.get("subtype") != "success" or result.get("is_error") or result.get("num_turns") != 1:
        raise ValueError("Claude reported an unsuccessful or multi-turn generation")
    text = result.get("result")
    if not isinstance(text, str) or len(text.encode("utf-8")) > MAX_TEXT:
        raise ValueError("Missing candidate text or text exceeds 64 KiB")
    usage = result.get("usage")
    if not isinstance(usage, dict) or any(type(usage.get(name)) is not int or usage[name] < 0 for name in USAGE_FIELDS):
        raise ValueError("Invalid Claude token accounting")
    retry_ms = sum(int(event.get("retry_delay_ms", 0) or 0) for event in retries)
    notes = dict(api_retries=len(retries), retry_delay_seconds=retry_ms / 1000, rate_limit_events=rate_limits,
                 duration_api_seconds=(result.get("duration_api_ms") or 0) / 1000)
    return text, {name: usage[name] for name in USAGE_FIELDS}, init, notes


class ExclusiveWorkspace:
    """The one fixed working directory every call runs in, held by one call at a time.

    The CLI's environment note includes the working directory, so a per-call temp path
    changes the prompt (the canary moved 450 -> 451 tokens on path length alone). The path
    is therefore fixed, and an OS file lock (released by the kernel if the holder dies, so
    a crash leaves no stale lock) makes a second holder, in this process or another, fail
    closed instead of sharing it. The directory is emptied on entry so nothing a previous
    call left behind can become context.
    """
    ROOT = Path(tempfile.gettempdir()) / "aidotnet-evolution-claude"

    def __init__(self, root=None):
        self.root = Path(root) if root is not None else self.ROOT
        self._handle = None

    def __enter__(self):
        self.root.mkdir(parents=True, exist_ok=True)
        handle = (self.root / "workspace.lock").open("a+b")
        try:
            handle.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            handle.close()
            raise ValueError(f"Another Claude call holds the fixed workspace {self.root}") from None
        self._handle = handle
        try:
            cwd = self.root / "cwd"
            if cwd.exists():
                shutil.rmtree(cwd)
            cwd.mkdir()
            mcp = self.root / "no-mcp.json"
            mcp.write_text('{"mcpServers":{}}', encoding="utf-8")
        except BaseException:
            self._release()
            raise
        return cwd, mcp

    def __exit__(self, *_):
        self._release()

    def _release(self):
        handle, self._handle = self._handle, None
        if handle is None:
            return
        try:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        finally:
            handle.close()

class ClaudeModelSet:
    """One transport per declared model, so a native ensemble routes by name through one contract.

    Both systems receive the same instance's `generate`; an undeclared name is refused rather
    than mapped, so a config cannot silently reach a model the other arm never had. Calls are
    sequential across the whole set, as each transport's admission is per model.
    """
    def __init__(self, executable, models, evidence, max_calls, *, timeout=300, canary_baselines=None):
        if not isinstance(models, (list, tuple)) or not models or len(set(models)) != len(models):
            raise ValueError("Declare a non-empty set of distinct models")
        baselines = canary_baselines or {}
        root = Path(evidence).resolve()
        root.mkdir(parents=True, exist_ok=False)
        self.transports = {model: ClaudeTransport(executable, model, root / model, max_calls, timeout=timeout,
                                                  canary_baseline=baselines.get(model)) for model in models}
        self._lock = threading.Lock()

    def generate(self, system_message, messages, model=None):
        if model not in self.transports:
            raise ValueError(f"Model {model!r} is not in the declared set {sorted(self.transports)}")
        if not self._lock.acquire(blocking=False):
            raise ValueError("Concurrent model calls are outside the sequential benchmark contract")
        try:
            return self.transports[model].generate_metered(system_message, messages)
        finally:
            self._lock.release()

    def verify_canaries(self):
        """Every model's canary against its baseline; the first failure refuses the campaign."""
        return {model: transport.canary_input_tokens() for model, transport in self.transports.items()}

def reconcile_receipts(evidence):
    """Every admitted call directory must hold a receipt; returns the coverage record."""
    root = Path(evidence)
    calls = sorted((path for path in root.iterdir() if path.is_dir()), key=lambda path: int(path.name))
    if [path.name for path in calls] != [str(index) for index in range(len(calls))]:
        raise ValueError("Call directories are not a gap-free sequence")
    receipts, missing = [], []
    for path in calls:
        try:
            receipts.append(json.loads((path / "receipt.json").read_text(encoding="utf-8")))
        except (OSError, ValueError):
            missing.append(path.name)
    return dict(calls=len(calls), receipts=len(receipts), missing=missing,
                coverage=len(receipts) / len(calls) if calls else None, rows=receipts)


class ClaudeTransport:
    def __init__(self, executable, model, evidence, max_calls, *, timeout=300, canary_baseline=None):
        self.executable = verified_executable(executable)
        if not model or len(model) > 100 or any(char.isspace() for char in model):
            raise ValueError("Pin an explicit model name")
        if type(max_calls) is not int or not 1 <= max_calls <= 100_000 or not 1 <= timeout <= 900:
            raise ValueError("Invalid benchmark call/time limits")
        if canary_baseline is not None and (type(canary_baseline) is not int or canary_baseline <= 0):
            raise ValueError("Canary baseline must be a positive input-token count")
        self.model, self.max_calls, self.timeout = model, max_calls, timeout
        self.canary_baseline = canary_baseline
        self.evidence = Path(evidence).resolve()
        self.evidence.mkdir(parents=True, exist_ok=False)
        self.environment = scrubbed_environment()
        self.resolved_model = None
        self.calls = 0
        self.failed = False
        self._last_usage = None
        self._verified_version = None
        self._lock = threading.Lock()

    def generate(self, system_message, messages):
        with self._admit():
            return self._generate(system_message, messages)

    def generate_metered(self, system_message, messages):
        with self._admit():
            text = self._generate(system_message, messages)
            usage = self._last_usage
            return {"text": text, "cost_units": sum(usage[name] for name in USAGE_FIELDS),
                    "cost_metric": "reported_input_plus_cache_plus_output_tokens",
                    "throttle_seconds": self._last_notes["retry_delay_seconds"],
                    "rate_limit_events": len(self._last_notes["rate_limit_events"])}

    def canary_input_tokens(self):
        """Input tokens for a fixed prompt; a change means context was injected."""
        with self._admit():
            self._generate(None, None, raw_prompt=CANARY_PROMPT)
            observed = self._last_usage["input_tokens"] + self._last_usage["cache_creation_input_tokens"] + \
                self._last_usage["cache_read_input_tokens"]
            if self.canary_baseline is not None and observed != self.canary_baseline:
                self.failed = True
                raise ValueError(f"Canary input tokens {observed} != baseline {self.canary_baseline}: context was injected")
            return observed

    def _admit(self):
        transport = self

        class Admission:
            def __enter__(self):
                if not transport._lock.acquire(blocking=False):
                    raise ValueError("Concurrent model calls are outside the sequential benchmark contract")

            def __exit__(self, *_):
                transport._lock.release()

        return Admission()

    def _version(self):
        if self._verified_version is None:
            version = subprocess.run([self.executable, "--version"], env=self.environment, capture_output=True,
                                     timeout=30, check=True).stdout.decode().strip()
            if version != PINNED_CLI:
                raise ValueError("Revalidate isolation flags and the stream-json protocol before changing the pinned CLI")
            self._verified_version = version
        return self._verified_version

    def _generate(self, system_message, messages, raw_prompt=None):
        if self.failed or self.calls >= self.max_calls:
            raise ValueError("Transport admission closed after failure or call cap")
        prompt = raw_prompt if raw_prompt is not None else json.dumps(
            {"system": system_message, "messages": messages}, ensure_ascii=False, allow_nan=False)
        if len(prompt.encode("utf-8")) > MAX_TEXT:
            raise ValueError("Prompt exceeds 64 KiB")
        version = self._version()
        directory = self.evidence / str(self.calls)
        directory.mkdir(exist_ok=False)
        self.calls += 1
        (directory / "prompt.txt").write_text(prompt, encoding="utf-8")
        receipt = dict(requested_model=self.model, resolved_model=None, cli=version, auth="subscription", status="failed",
                       unknown_usage=True, canary=raw_prompt is not None,
                       executable_sha256=hashlib.sha256(Path(self.executable).read_bytes()).hexdigest(),
                       prompt_sha256=hashlib.sha256(prompt.encode("utf-8")).hexdigest(),
                       api_key_calls=0, monetary_cost=None,
                       cost_note="Subscription access; the CLI's list-price estimate is not a monetary cost")
        process = None
        started = time.monotonic()
        try:
            with ExclusiveWorkspace() as (cwd, mcp):
                command = [self.executable, "-p", "--output-format", "stream-json", "--verbose",
                           "--model", self.model, "--tools", "", "--strict-mcp-config", "--mcp-config", str(mcp),
                           "--setting-sources", "", "--no-session-persistence", "--max-turns", "1",
                           "--system-prompt", SYSTEM_PROMPT]
                receipt["arguments"] = [arg if arg != str(mcp) else "<empty-mcp-config>" for arg in command[1:]]
                with (directory / "events.jsonl").open("xb") as stdout, (directory / "stderr.txt").open("xb") as stderr:
                    with tempfile.TemporaryFile() as stdin:
                        stdin.write(prompt.encode("utf-8"))
                        stdin.seek(0)
                        spawned = time.monotonic()
                        process = subprocess.Popen(command, stdin=stdin, stdout=stdout, stderr=stderr, cwd=cwd,
                                                   env=self.environment, start_new_session=os.name != "nt")
                        # wait() returns as the CLI exits; a sleep-poll would add up to its
                        # interval to every call and be charged as transport overhead.
                        while True:
                            try:
                                process.wait(timeout=0.05)
                                break
                            except subprocess.TimeoutExpired:
                                if time.monotonic() - started >= self.timeout or max(stdout.tell(), stderr.tell()) > MAX_OUTPUT:
                                    raise TimeoutError("Generation time or output bound exceeded") from None
                        receipt["cli_process_seconds"] = time.monotonic() - spawned
                        if process.returncode:
                            raise ValueError("Claude generation exited unsuccessfully")
                with (directory / "events.jsonl").open("rb") as events:
                    text, usage, init, notes = parse_events(events.read(MAX_OUTPUT + 1), self.resolved_model)
                if self.resolved_model is None:
                    # The alias is pinned to whatever it resolved to first; a mid-campaign
                    # model change fails every later call instead of silently mixing models.
                    self.resolved_model = init["model"]
                receipt.update(status="completed", unknown_usage=False, usage=usage, resolved_model=init["model"],
                               **{key: value for key, value in notes.items() if key != "rate_limit_events"},
                               rate_limit_events=len(notes["rate_limit_events"]))
                self._last_usage = dict(usage)
                self._last_notes = notes
                return text
        except Exception:
            self.failed = True
            raise
        finally:
            if process is not None:
                kill_owned(process)
            elapsed = time.monotonic() - started
            receipt["elapsed_seconds"] = elapsed
            if "cli_process_seconds" in receipt:
                # Everything the transport adds around the CLI process: workspace, files, parsing.
                receipt["transport_overhead_seconds"] = max(0.0, elapsed - receipt["cli_process_seconds"])
            # Wall time the engine should be charged for: throttle backoff is the provider's
            # queue, not search work, so it is reported separately rather than hidden.
            receipt["elapsed_excluding_retry_delay_seconds"] = max(0.0, elapsed - receipt.get("retry_delay_seconds", 0.0))
            (directory / "receipt.json").write_text(json.dumps(receipt, indent=2, allow_nan=False), encoding="utf-8")
