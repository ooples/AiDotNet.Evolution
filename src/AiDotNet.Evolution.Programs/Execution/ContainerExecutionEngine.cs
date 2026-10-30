using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDotNet.Evolution.Programs;

/// <summary>Runs each candidate inside a locked-down Docker container, isolated from the host's filesystem, network
/// and system calls.</summary>
/// <remarks>
/// <para>
/// Every container is started with <c>--network none</c> (loopback only), <c>--read-only</c> (no writable path but an
/// in-memory <c>/work</c>), <c>--cap-drop ALL</c>, <c>--security-opt no-new-privileges=true</c>, the daemon's default
/// seccomp profile, <c>--ipc none</c>, <c>--pids-limit</c>, <c>--memory</c> with no swap, <c>--cpus</c>, a non-root
/// numeric user and no host mounts beyond the read-only directories named in
/// <see cref="ContainerExecutionOptions.ReadOnlyMounts"/> (none by default). The candidate's source and standard input
/// travel over the container's attached standard input, so no host file needs to be shared with it. Before a container is used, the engine reads
/// the configuration back from <c>docker inspect</c> and has the supervisor inside report its own seccomp mode,
/// <c>no_new_privs</c> flag, effective capabilities and user, and refuses the container if any of them differs from what
/// was asked for.
/// </para>
/// <para>
/// Inside, a small Python supervisor is the container's PID 1 and runs one candidate at a time in a fresh process:
/// forked from itself for Python (see <see cref="ContainerPythonLaunch"/>) or exec'd from
/// <see cref="ContainerExecutionOptions.Interpreters"/> for other languages. It enforces the wall-clock and CPU-time
/// limits, caps captured output, and after every candidate kills every other process in the container and empties
/// <c>/work</c>. In <see cref="ContainerLifecycle.Warm"/> mode the container then runs the next candidate; in
/// <see cref="ContainerLifecycle.Cold"/> mode it is removed. The memory limit is the container's cgroup limit and covers
/// the supervisor (about 10 MB) and the workdir's contents as well as the candidate; the kernel's out-of-memory killer
/// ends the candidate and the response reports <see cref="ProgramExecuteErrorCode.MemoryLimitExceeded"/>.
/// </para>
/// <para>
/// <b>Security boundary:</b> the Docker daemon and the host kernel. A container shares the kernel, so a kernel
/// vulnerability reachable through the seccomp filter is outside what this engine can stop; run it on a host whose
/// kernel you keep patched, and use a VM boundary in addition for adversaries who would spend a kernel exploit. Nothing
/// is mounted from the host and the network namespace has only loopback, but the candidate's output, exit code and
/// timing reach the caller, as they must.
/// </para>
/// <para><b>For Beginners:</b> this is the engine to use when a language model writes the code. Install Docker, pull
/// an image with Python in it, and pass its name in <see cref="ContainerExecutionOptions.Image"/>. Each candidate then
/// runs in a throwaway box that cannot see your files, cannot use the internet, and cannot use more memory or
/// processes than you allow.</para>
/// </remarks>
public sealed class ContainerExecutionEngine : IProgramExecutionEngine, IProgramExecutionTelemetrySource, IDisposable
{
    private const string ScriptResource = "AiDotNet.Evolution.Programs.Execution.container_supervisor.py";
    private const string OwnerLabel = "org.aidotnet.evolution.container";
    private const string Workdir = "/work";
    private const int MaxDiagnosticChars = 4_000;
    private const int RemoveAttempts = 3;
    private static readonly TimeSpan ReplyMargin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(60);

    private readonly ProgramSandboxOptions _options;
    private readonly ContainerExecutionOptions _container;
    private readonly ProgramSandboxLimitOptions _limits;
    private readonly string _script;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Container> _idle = new();
    private readonly ConcurrentDictionary<Container, byte> _busy = new();
    private int _queued;
    private int _active;
    private int _unremoved;
    private volatile bool _disposed;

    /// <summary>Creates the engine, checks the Docker daemon and resolves the image. Containers start on first use.</summary>
    /// <param name="options">The sandbox options. <see cref="ProgramSandboxOptions.Mode"/> must be
    /// <see cref="ProgramSandboxMode.Container"/>; its limits, declared environment and runtime version apply.</param>
    /// <param name="container">The image and container settings.</param>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The mode is not <see cref="ProgramSandboxMode.Container"/>, or an option is unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is impossible, or the memory limit is under 16 MB.</exception>
    /// <exception cref="InvalidOperationException">Docker is not installed or not running, is not a Linux-container
    /// daemon with seccomp, or the image is not present.</exception>
    public ContainerExecutionEngine(ProgramSandboxOptions options, ContainerExecutionOptions container)
    {
        ProgramGuard.NotNull(options);
        ProgramGuard.NotNull(container);
        _options = options.Clone();
        _options.Validate();
        if (_options.Mode != ProgramSandboxMode.Container)
        {
            throw new ArgumentException(
                $"ContainerExecutionEngine implements {ProgramSandboxMode.Container} only, but the options select {_options.Mode}.",
                nameof(options));
        }

        _container = container.Clone();
        _container.Validate();
        _limits = _options.Limits;
        if (_limits.MemoryLimitMb < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _limits.MemoryLimitMb,
                "The container memory limit must be at least 16 MB: it also holds the supervisor and the workdir.");
        }

        _script = ReadScript();
        // Blocking I/O in a constructor is deliberate: a host without a usable daemon should fail where it is configured,
        // not on the first candidate, where the failure would be scored as the candidate's.
        Task.Run(VerifyDaemonAsync).GetAwaiter().GetResult();
        ImageId = Task.Run(ResolveImageAsync).GetAwaiter().GetResult();
        _slots = new SemaphoreSlim(_limits.MaxConcurrentExecutions, _limits.MaxConcurrentExecutions);
        VersionHash = EvolutionHash.Combine(new[]
        {
            "container-execution-v1", ImageId, _options.RuntimeVersion, _container.Lifecycle.ToString(),
            _container.PythonLaunch.ToString(), _container.SupervisorPython,
            _container.RecycleAfter.ToString(CultureInfo.InvariantCulture), _container.PidsLimit.ToString(CultureInfo.InvariantCulture),
            _container.UserId.ToString(CultureInfo.InvariantCulture) + ":" + _container.GroupId.ToString(CultureInfo.InvariantCulture),
            _container.WorkspaceSizeMb.ToString(CultureInfo.InvariantCulture), _container.SeccompProfilePath ?? "default-seccomp",
            JsonSerializer.Serialize(_limits),
            JsonSerializer.Serialize(_container.Interpreters.OrderBy(pair => pair.Key)
                .Select(pair => new { pair.Key, pair.Value.Executable, pair.Value.RunArgumentTemplate })),
            EvolutionHash.Compute(_script),
            // What a candidate can read beyond the image; the files' contents are the caller's to identify.
            string.Join("\u001f", _container.ReadOnlyMounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value)),
            // A declared module search path changes what a candidate can import, so it changes what a result means.
            string.Join("\u001f", _options.DeclaredEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value))
        });
    }

    /// <inheritdoc />
    public string Id => "container-execution";

    /// <inheritdoc />
    public string VersionHash { get; }

    /// <summary>Gets the content identity (<c>sha256:...</c>) of the image every container starts from.</summary>
    public string ImageId { get; }

    /// <summary>Gets whether containers are reused across candidates.</summary>
    public ContainerLifecycle Lifecycle => _container.Lifecycle;

    /// <inheritdoc />
    public int QueuedExecutionCount => Volatile.Read(ref _queued);

    /// <inheritdoc />
    public int ActiveExecutionCount => Volatile.Read(ref _active);

    // Containers started and not yet removed; for tests that a timeout or a recycle does not leave one behind.
    internal int LiveContainerCount => _idle.Count + _busy.Count;

    // Containers `docker rm --force` could not remove after RemoveAttempts tries; the owner label still finds them.
    internal int UnremovedContainerCount => Volatile.Read(ref _unremoved);

    /// <inheritdoc />
    public bool TryExecute(ProgramLanguage language, string sourceCode, string input, out string output, out string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        ProgramExecuteResponse response;
        try
        {
            response = Task.Run(() => ExecuteAsync(new ProgramExecuteRequest
            {
                Language = language,
                SourceCode = sourceCode ?? string.Empty,
                StdIn = input ?? string.Empty
            }, cancellationToken), cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // A token canceled before Task.Run starts skips the delegate, so ExecuteAsync never reports it.
            output = string.Empty;
            errorMessage = "Execution was canceled.";
            return false;
        }

        if (response.Success && !response.StdOutTruncated)
        {
            output = response.StdOut;
            errorMessage = null;
            return true;
        }

        output = string.Empty;
        errorMessage = response.Error ?? "Captured output was incomplete or truncated.";
        return false;
    }

    /// <inheritdoc />
    public async Task<ProgramExecuteResponse> ExecuteAsync(ProgramExecuteRequest request, CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Own the mutable request before queuing, so a later edit cannot bypass the checks below.
        var snapshot = new ProgramExecuteRequest
        {
            Language = request.Language,
            SourceCode = request.SourceCode ?? string.Empty,
            StdIn = request.StdIn,
            CompileOnly = request.CompileOnly,
            AllowedLanguages = request.AllowedLanguages?.ToList() ?? new(),
            PreferredLanguage = request.PreferredLanguage,
            AllowUndetectedLanguageFallback = request.AllowUndetectedLanguageFallback
        };
        string source = snapshot.SourceCode;
        if (string.IsNullOrWhiteSpace(source))
            return Rejected(snapshot.Language, ProgramExecuteErrorCode.SourceCodeRequired, "SourceCode is required.");
        if (source.Length > _limits.MaxSourceCodeChars)
            return Rejected(snapshot.Language, ProgramExecuteErrorCode.SourceCodeTooLarge, "The source exceeds the sandbox's size limit.");
        if ((snapshot.StdIn?.Length ?? 0) > _limits.MaxStdInChars)
            return Rejected(snapshot.Language, ProgramExecuteErrorCode.StdInTooLarge, "Standard input exceeds the sandbox's size limit.");
        ProgramExecuteResponse? rejection = ProcessProgramExecutionEngine.ResolveLanguage(snapshot, source, out ProgramLanguage language);
        if (rejection is not null) return rejection;
        if (snapshot.CompileOnly)
            return Rejected(language, ProgramExecuteErrorCode.InvalidRequest, "The container engine does not support compile-only requests.");
        bool forkPython = language == ProgramLanguage.Python && _container.PythonLaunch == ContainerPythonLaunch.ForkSupervisor;
        string fileName = "candidate" + ProgramLanguageDetector.GetFileExtension(language);
        JsonArray? argv = null;
        if (!forkPython)
        {
            if (!_container.Interpreters.TryGetValue(language, out ProgramInterpreterSpecification? interpreter) || interpreter is null)
            {
                return Rejected(language, ProgramExecuteErrorCode.InvalidRequest,
                    $"No in-image interpreter is configured for {language}. Add one to ContainerExecutionOptions.Interpreters.");
            }

            argv = BuildArgv(interpreter, Workdir + "/" + fileName);
        }

        Interlocked.Increment(ref _queued);
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Rejected(language, ProgramExecuteErrorCode.TimeoutOrCanceled, "Execution was canceled while queued.");
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }

        Interlocked.Increment(ref _active);
        Container? container = null;
        bool reusable = false;
        try
        {
            if (_container.Lifecycle == ContainerLifecycle.Warm)
            {
                while (_idle.TryTake(out Container? idle))
                {
                    if (idle.IsUsable)
                    {
                        container = idle;
                        break;
                    }

                    await RemoveAsync(idle).ConfigureAwait(false);
                }
            }

            if (container is null)
            {
                try
                {
                    container = await StartContainerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Rejected(language, ProgramExecuteErrorCode.TimeoutOrCanceled, "Execution was canceled while the container started.");
                }
                catch (InvalidOperationException exception)
                {
                    return Rejected(language, ProgramExecuteErrorCode.ExecutionFailed, exception.Message);
                }
            }

            _busy[container] = 0;
            if (_disposed) return Rejected(language, ProgramExecuteErrorCode.ExecutionFailed, "The engine was disposed before the candidate ran.");
            var frame = new JsonObject
            {
                ["source"] = source,
                ["stdin"] = snapshot.StdIn ?? string.Empty,
                ["file"] = fileName,
                ["argv"] = argv,
                ["time_limit"] = _limits.TimeLimitSeconds,
                ["cpu_seconds"] = (int)_limits.GetCpuTimeLimit().TotalSeconds,
                ["max_stdout"] = _limits.MaxStdOutChars,
                ["max_stderr"] = _limits.MaxStdErrChars
            };

            // The supervisor enforces the wall clock and replies; the margin covers a supervisor that cannot.
            JsonNode? reply = await container.ExchangeAsync(frame, _limits.GetTimeLimit() + ReplyMargin, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Rejected(language, ProgramExecuteErrorCode.TimeoutOrCanceled, "Execution was canceled.");
                if (_disposed)
                    return Rejected(language, ProgramExecuteErrorCode.ExecutionFailed, "The engine was disposed during execution.");
                return Rejected(language, ProgramExecuteErrorCode.ExecutionFailed,
                    container.HasExited
                        ? "The container exited during the candidate and was replaced."
                        : "The container's supervisor stopped responding and the container was replaced.");
            }

            container.Completed++;
            bool clean = reply["clean"]?.GetValue<bool>() == true;
            reusable = clean && _container.Lifecycle == ContainerLifecycle.Warm && container.Completed < _container.RecycleAfter;
            return FromReply(reply, language, _limits);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException
                                              or InvalidDataException or Win32Exception or ObjectDisposedException)
        {
            reusable = false;
            return Rejected(language, ProgramExecuteErrorCode.ExecutionFailed, "The container failed: " + exception.GetType().Name + ".");
        }
        finally
        {
            if (container is not null)
            {
                _busy.TryRemove(container, out _);
                if (reusable && !_disposed)
                {
                    _idle.Add(container);
                    // Dispose may have drained the pool between the check and the add; do not leave this one behind.
                    if (_disposed) DrainIdle();
                }
                else
                {
                    await RemoveAsync(container).ConfigureAwait(false);
                }
            }

            Interlocked.Decrement(ref _active);
            _slots.Release();
        }
    }

    private static JsonArray BuildArgv(ProgramInterpreterSpecification interpreter, string sourcePath)
    {
        var argv = new JsonArray { interpreter.Executable };
        foreach (string token in interpreter.RunArgumentTemplate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            argv.Add(token
                .Replace(ProgramInterpreterSpecification.SourcePlaceholder, sourcePath, StringComparison.Ordinal)
                .Replace(ProgramInterpreterSpecification.WorkspacePlaceholder, Workdir, StringComparison.Ordinal));
        }

        return argv;
    }

    // The supervisor decides memory_exceeded: from the cgroup's OOM counter, or from exit code 125 only for a Python
    // candidate it forked (where 125 is the MemoryError sentinel); any other candidate may exit 125 for its own reasons.
    // A memory event fails the execution even when the candidate exits 0, so its output is never scored.
    internal static ProgramExecuteResponse FromReply(JsonNode reply, ProgramLanguage language, ProgramSandboxLimitOptions limits)
    {
        int exit = reply["exit"]?.GetValue<int>() ?? -1;
        bool timedOut = reply["timed_out"]?.GetValue<bool>() == true;
        bool memory = reply["memory_exceeded"]?.GetValue<bool>() == true;
        bool cpu = reply["cpu_exceeded"]?.GetValue<bool>() == true;
        bool success = exit == 0 && !timedOut && !memory;
        string? error = success ? null
            : timedOut ? $"Execution exceeded the {limits.TimeLimitSeconds.ToString(CultureInfo.InvariantCulture)} second limit and was terminated."
            : memory ? $"Execution exceeded the {limits.MemoryLimitMb.ToString(CultureInfo.InvariantCulture)} MB container memory limit."
            : cpu ? "Execution exceeded its CPU-time limit."
            : $"Execution failed with exit code {exit.ToString(CultureInfo.InvariantCulture)}.";
        return new ProgramExecuteResponse
        {
            Success = success,
            Language = language,
            ExitCode = timedOut ? -1 : exit,
            StdOut = reply["stdout"]?.GetValue<string>() ?? string.Empty,
            StdErr = reply["stderr"]?.GetValue<string>() ?? string.Empty,
            StdOutTruncated = reply["stdout_truncated"]?.GetValue<bool>() == true,
            StdErrTruncated = reply["stderr_truncated"]?.GetValue<bool>() == true,
            Error = error,
            ErrorCode = success ? null
                : timedOut ? ProgramExecuteErrorCode.TimeoutOrCanceled
                : memory ? ProgramExecuteErrorCode.MemoryLimitExceeded
                : cpu ? ProgramExecuteErrorCode.CpuTimeLimitExceeded
                : ProgramExecuteErrorCode.ExecutionFailed
        };
    }

    // The arguments of `docker run`. Internal so a test can prove every hardening flag is present.
    internal List<string> BuildRunArguments(string name)
    {
        string memory = _limits.MemoryLimitMb.ToString(CultureInfo.InvariantCulture) + "m";
        string user = _container.UserId.ToString(CultureInfo.InvariantCulture);
        string group = _container.GroupId.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "run", "--interactive", "--rm", "--name", name, "--label", OwnerLabel + "=" + name,
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges=true",
            "--ipc", "none",
            "--pids-limit", _container.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", memory, "--memory-swap", memory,
            "--cpus", _limits.CpuLimit.ToString("0.###", CultureInfo.InvariantCulture),
            "--user", user + ":" + group,
            "--tmpfs", Workdir + ":rw,nosuid,nodev,noexec,size=" + _container.WorkspaceSizeMb.ToString(CultureInfo.InvariantCulture)
                       + "m,uid=" + user + ",gid=" + group + ",mode=0700",
            "--log-driver", "none",
            "--env", "HOME=" + Workdir, "--env", "TMPDIR=" + Workdir, "--env", "LANG=C.UTF-8",
            "--env", "PYTHONIOENCODING=utf-8", "--env", "PYTHONDONTWRITEBYTECODE=1"
        };
        // Declared host directories only, each read-only; VerifyConfigurationAsync refuses anything else the daemon mounted.
        foreach (KeyValuePair<string, string> mount in _container.ReadOnlyMounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("--mount");
            arguments.Add("type=bind,source=" + mount.Value + ",target=" + mount.Key + ",readonly");
        }

        if (_container.SeccompProfilePath is { } profile)
        {
            arguments.Add("--security-opt");
            arguments.Add("seccomp=" + profile);
        }

        // Only the caller's own written-down values; the reserved names cannot be declared (ProgramSandboxOptions.Validate).
        foreach (KeyValuePair<string, string> declared in _options.DeclaredEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("--env");
            arguments.Add(declared.Key + "=" + declared.Value);
        }

        arguments.Add("--entrypoint");
        arguments.Add(_container.SupervisorPython);
        arguments.Add(ImageId);
        arguments.Add("-u");
        arguments.Add("-c");
        arguments.Add(_script);
        return arguments;
    }

    private async Task<Container> StartContainerAsync(CancellationToken cancellationToken)
    {
        string name = "aidotnet-evolution-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(_container.DockerExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in BuildRunArguments(name)) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The Docker client did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The Docker client '" + _container.DockerExecutable + "' could not be started: " + exception.Message, exception);
        }

        var container = new Container(name, process);
        bool ready = false;
        try
        {
            JsonNode? hello = await container.ReadAsync(TimeSpan.FromSeconds(_container.StartupTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (hello?["ready"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The container did not become ready: " + container.Diagnostics());
            string? attestation = CheckAttestation(hello);
            if (attestation is not null)
                throw new InvalidOperationException("The container is not isolated as configured: " + attestation + ".");
            string? configuration = await VerifyConfigurationAsync(name, cancellationToken).ConfigureAwait(false);
            if (configuration is not null)
                throw new InvalidOperationException("Docker did not apply the declared isolation: " + configuration + ".");
            ready = true;
            return container;
        }
        finally
        {
            if (!ready) await RemoveAsync(container).ConfigureAwait(false);
        }
    }

    // What the supervisor reports about itself from /proc/self/status, so a daemon that silently ignored a flag is caught.
    private string? CheckAttestation(JsonNode hello)
    {
        if (hello["pid"]?.GetValue<int>() != 1) return "the supervisor is not the container's init process";
        if (hello["uid"]?.GetValue<int>() != _container.UserId) return "the container does not run as user " + _container.UserId.ToString(CultureInfo.InvariantCulture);
        if (hello["hardened"]?.GetValue<bool>() != true) return "the supervisor could not make itself undumpable (the image needs Python's ctypes)";
        if (hello["seccomp"]?.GetValue<string>() != "2") return "no seccomp filter is active";
        if (hello["no_new_privs"]?.GetValue<string>() != "1") return "no_new_privs is not set";
        string? capabilities = hello["cap_eff"]?.GetValue<string>();
        if (capabilities is null || capabilities.Length == 0 || capabilities.Any(digit => digit != '0')) return "the container holds capabilities";
        if (hello["clean"]?.GetValue<bool>() != true) return "the workdir is not empty";
        return null;
    }

    // Reads the container's configuration back from the daemon; null when every isolation setting is as requested.
    private async Task<string?> VerifyConfigurationAsync(string name, CancellationToken cancellationToken)
    {
        (int exit, string output, string error) = await RunDockerAsync(new[] { "inspect", "--type", "container", name }, cancellationToken).ConfigureAwait(false);
        if (exit != 0) return "docker inspect failed: " + Truncate(error);
        JsonNode? root = JsonNode.Parse(output)?.AsArray().FirstOrDefault();
        JsonNode? host = root?["HostConfig"];
        if (root is null || host is null) return "docker inspect returned no configuration";
        long memory = _limits.GetMemoryLimitBytes();
        long cpus = (long)Math.Round(double.Parse(_limits.CpuLimit.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 1e9);
        string user = _container.UserId.ToString(CultureInfo.InvariantCulture) + ":" + _container.GroupId.ToString(CultureInfo.InvariantCulture);
        List<string> security = Strings(host["SecurityOpt"]);
        if (root["Image"]?.GetValue<string>() != ImageId) return "the image is not " + ImageId;
        if (root["Config"]?["User"]?.GetValue<string>() != user) return "the user is not " + user;
        if (host["NetworkMode"]?.GetValue<string>() != "none") return "the network is not none";
        if (host["ReadonlyRootfs"]?.GetValue<bool>() != true) return "the root filesystem is writable";
        if (host["Privileged"]?.GetValue<bool>() != false) return "the container is privileged";
        if (Strings(host["CapAdd"]).Count != 0) return "capabilities were added";
        if (!Strings(host["CapDrop"]).Any(cap => string.Equals(cap, "ALL", StringComparison.OrdinalIgnoreCase))) return "capabilities were not dropped";
        if (!security.Any(option => option is "no-new-privileges=true" or "no-new-privileges" or "no-new-privileges:true"))
            return "no-new-privileges is not set";
        if (security.Any(option => option.Contains("unconfined", StringComparison.OrdinalIgnoreCase))) return "a security profile is unconfined";
        if (host["IpcMode"]?.GetValue<string>() != "none") return "the IPC namespace is shared";
        if (host["PidsLimit"]?.GetValue<long>() != _container.PidsLimit) return "the process limit is not applied";
        if (host["Memory"]?.GetValue<long>() != memory || host["MemorySwap"]?.GetValue<long>() != memory) return "the memory limit is not applied";
        if (host["NanoCpus"]?.GetValue<long>() != cpus) return "the CPU limit is not applied";
        if (Strings(host["Binds"]).Count != 0) return "host paths are bound";
        string? mounts = CheckMounts(root["Mounts"] as JsonArray, _container.ReadOnlyMounts);
        if (mounts is not null) return mounts;
        if (host["Tmpfs"]?[Workdir] is null) return "the workdir is not a tmpfs";
        return null;
    }

    /// <summary>Checks <c>docker inspect</c>'s mount list against the declared read-only mounts; null when they match.</summary>
    /// <remarks>Every bind must be declared, read-only, and backed by the declared host path, so a daemon or wrapper that
    /// substituted another source (the root, a socket directory) is caught as surely as a writable one.</remarks>
    internal static string? CheckMounts(JsonArray? mounts, IDictionary<string, string> declaredMounts)
    {
        var declared = new HashSet<string>(declaredMounts.Keys, StringComparer.Ordinal);
        foreach (JsonNode? mount in mounts ?? new JsonArray())
        {
            string? type = mount?["Type"]?.GetValue<string>();
            if (type == "tmpfs") continue;
            string destination = mount?["Destination"]?.GetValue<string>() ?? string.Empty;
            if (type != "bind" || !declared.Remove(destination)) return "an undeclared host path or volume is mounted at " + destination;
            if (mount?["RW"]?.GetValue<bool>() != false) return "the host path mounted at " + destination + " is writable";
            string source = mount?["Source"]?.GetValue<string>() ?? string.Empty;
            if (!ContainerMountSource.IsReportedAs(source, declaredMounts[destination]))
                return "the host path mounted at " + destination + " is '" + source + "', not the declared " + declaredMounts[destination];
        }

        if (declared.Count != 0) return "a declared read-only mount is missing: " + string.Join(", ", declared);
        return null;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList() : new List<string>();

    private async Task VerifyDaemonAsync()
    {
        (int exit, string output, string error) = await RunDockerAsync(
            new[] { "info", "--format", "{{.OSType}}\n{{json .SecurityOptions}}" }, CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("Docker is not available: 'docker info' failed. Start the Docker daemon. " + Truncate(error));
        string[] lines = output.Split('\n', 2);
        if (lines[0].Trim() != "linux")
            throw new InvalidOperationException("The Docker daemon runs '" + lines[0].Trim() + "' containers; ContainerExecutionEngine needs Linux containers.");
        List<string> security = lines.Length > 1 ? Strings(JsonNode.Parse(lines[1].Trim())) : new List<string>();
        if (!security.Any(option => option.Contains("name=seccomp", StringComparison.Ordinal)))
            throw new InvalidOperationException("The Docker daemon does not support seccomp, so the default system-call filter cannot be applied.");
    }

    private async Task<string> ResolveImageAsync()
    {
        (int exit, string output, _) = await RunDockerAsync(
            new[] { "image", "inspect", "--format", "{{.Id}} {{.Os}}", _container.Image }, CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                "The image '" + _container.Image + "' is not present on the Docker host. Pull it first: docker pull " + _container.Image);
        }

        string[] parts = output.Trim().Split(' ');
        if (parts.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^sha256:[0-9a-f]{64}$"))
            throw new InvalidOperationException("Docker reported an unusable identity for image '" + _container.Image + "'.");
        if (parts[1] != "linux")
            throw new InvalidOperationException("The image '" + _container.Image + "' is a " + parts[1] + " image; a Linux image is required.");
        return parts[0];
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunDockerAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_container.DockerExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The Docker client did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "The Docker client '" + _container.DockerExecutable + "' could not be started; install Docker or set DockerExecutable. " + exception.Message,
                exception);
        }

        using (process)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ControlTimeout);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception kill) when (kill is InvalidOperationException or Win32Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The Docker client did not answer within " + ControlTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
            }
        }
    }

    // Closes the attached input (the supervisor exits and --rm removes the container), then forces removal if the
    // container is still there; a container is never left running behind the engine.
    private async Task RemoveAsync(Container container)
    {
        bool exited = await container.CloseAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!exited && !await ForceRemoveAsync(container.Name).ConfigureAwait(false))
            Interlocked.Increment(ref _unremoved);

        container.Dispose();
    }

    // Never throws: it runs in ExecuteAsync's finally, where an exception would replace the candidate's response.
    // "No such container" means --rm already removed it; any other refusal is retried.
    private async Task<bool> ForceRemoveAsync(string name)
    {
        for (int attempt = 1; attempt <= RemoveAttempts; attempt++)
        {
            try
            {
                (int exit, _, string error) = await RunDockerAsync(new[] { "rm", "--force", name }, CancellationToken.None).ConfigureAwait(false);
                if (exit == 0 || error.Contains("No such container", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (InvalidOperationException)
            {
                // The daemon is gone; nothing more can be done from here, and --rm removes it if the daemon returns.
                return false;
            }

            if (attempt < RemoveAttempts) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
        }

        return false;
    }

    private void DrainIdle()
    {
        while (_idle.TryTake(out Container? idle))
            Task.Run(() => RemoveAsync(idle)).GetAwaiter().GetResult();
    }

    private static string Truncate(string text) => text.Length <= MaxDiagnosticChars ? text.Trim() : text.Substring(0, MaxDiagnosticChars).Trim();

    private static string ReadScript()
    {
        using Stream stream = typeof(ContainerExecutionEngine).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("The container supervisor script is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // A carriage return inside `python -c` would be a syntax error on a Windows checkout.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static ProgramExecuteResponse Rejected(ProgramLanguage language, ProgramExecuteErrorCode code, string error) => new()
    {
        Success = false,
        Language = language,
        ExitCode = -1,
        Error = error,
        ErrorCode = code
    };

    /// <summary>Removes every container this engine started.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DrainIdle();
        // Closing a busy container's input ends its exchange at once; its execution then removes it.
        foreach (Container busy in _busy.Keys) busy.Abort();
    }

    private sealed class Container : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stderrPump;
        private int _disposed;

        internal Container(string name, Process process)
        {
            Name = name;
            _process = process;
            _input = process.StandardInput.BaseStream;
            _output = process.StandardOutput.BaseStream;
            // Drain the client's stderr so a chatty daemon error can never block it, keeping the start for diagnostics.
            _stderrPump = Task.Run(async () =>
            {
                char[] buffer = new char[1024];
                try
                {
                    int read;
                    while ((read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        lock (_stderr)
                        {
                            if (_stderr.Length < MaxDiagnosticChars) _stderr.Append(buffer, 0, Math.Min(read, MaxDiagnosticChars - _stderr.Length));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }

        internal string Name { get; }

        internal int Completed { get; set; }

        internal bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        internal bool IsUsable => Volatile.Read(ref _disposed) == 0 && !HasExited;

        internal string Diagnostics()
        {
            _stderrPump.Wait(TimeSpan.FromSeconds(2));
            lock (_stderr)
            {
                string text = _stderr.ToString().Trim();
                return text.Length == 0 ? "the Docker client exited without a message." : text;
            }
        }

        internal async Task<JsonNode?> ExchangeAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            byte[] body = Encoding.UTF8.GetBytes(request.ToJsonString());
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<JsonNode?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                byte[] header = new byte[4];
                await _output.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
                if (length is < 2 or > 64 * 1024 * 1024) throw new InvalidDataException("The container supervisor sent an invalid frame.");
                byte[] body = new byte[length];
                await _output.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                return JsonNode.Parse(body);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        // True when the client exited by itself after its input closed, which means the container is gone.
        internal async Task<bool> CloseAsync(TimeSpan grace)
        {
            try { _input.Close(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            using var wait = new CancellationTokenSource(grace);
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
            {
                return false;
            }
        }

        internal void Abort()
        {
            try { _input.Close(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            try { _output.Close(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already exited.
            }

            _process.Dispose();
        }
    }
}
