using System.Globalization;

namespace AiDotNet.Evolution.Programs;

/// <summary>Configures the Docker container <see cref="ContainerExecutionEngine"/> runs candidates in.</summary>
/// <remarks>
/// <para>
/// The isolation itself is not configurable: every container runs with <c>--network none</c>, a read-only root
/// filesystem, <c>--cap-drop ALL</c>, <c>no-new-privileges</c>, the daemon's seccomp profile (or the one named by
/// <see cref="SeccompProfilePath"/>), no IPC namespace sharing, no host mounts other than the read-only directories named in
/// <see cref="ReadOnlyMounts"/>, and a non-root user. What is
/// configurable is the image, the reuse policy and the sizes. Time, memory, CPU and output limits come from
/// <see cref="ProgramSandboxOptions.Limits"/>, so one limits object describes a run whichever engine executes it.
/// </para>
/// <para><b>For Beginners:</b> point <see cref="Image"/> at an image that has the interpreter your candidates need
/// (the default Python path needs <c>python3</c> in it, for example <c>python:3.12-slim</c>) and pull it once with
/// <c>docker pull</c>. The defaults are a reasonable sandbox; the engine refuses to start if Docker does not apply
/// them.</para>
/// </remarks>
public sealed class ContainerExecutionOptions
{
    /// <summary>The largest <see cref="RecycleAfter"/> accepted.</summary>
    public const int MaxRecycleAfter = 1_000_000;

    // Paths a mount may not cover: the workdir is the engine's, and the kernel interfaces are Docker's.
    private static readonly string[] ReservedMountRoots = { "/work", "/proc", "/sys", "/dev" };

    // `--mount` is a comma-separated list of key=value fields, so these would end or re-quote the source field.
    private static readonly char[] MountSourceSeparators = { ',', '"', '\n', '\r' };

    private readonly Dictionary<ProgramLanguage, ProgramInterpreterSpecification> _interpreters;
    private readonly Dictionary<string, string> _readOnlyMounts = new(StringComparer.Ordinal);

    /// <summary>Initializes options with the default interpreter table and hardening sizes.</summary>
    public ContainerExecutionOptions()
    {
        _interpreters = CreateDefaultInterpreters();
    }

    /// <summary>Gets or sets the image candidates run in. Required.</summary>
    /// <remarks>
    /// The image must already be present on the Docker host; the engine does not pull. It is resolved to its content
    /// identity (<c>sha256:...</c>) when the engine is created, every container is started from that identity, and the
    /// identity is part of <see cref="ContainerExecutionEngine.VersionHash"/>, so a tag that later moves cannot change
    /// a running engine or be confused with an earlier result. Pinning a digest (<c>name@sha256:...</c>) makes the
    /// choice reproducible across machines as well. The image must be a Linux image with <c>python3</c> (the
    /// supervisor, see <see cref="SupervisorPython"/>) including its <c>ctypes</c> module.
    /// </remarks>
    public string Image { get; set; } = string.Empty;

    /// <summary>Gets or sets the Docker command-line client. Defaults to <c>docker</c> on <c>PATH</c>.</summary>
    /// <remarks>The client talks to whatever daemon its own configuration names; that daemon and its kernel are the
    /// security boundary, so point it only at a daemon you trust.</remarks>
    public string DockerExecutable { get; set; } = "docker";

    /// <summary>Gets or sets whether containers are reused across candidates. Defaults to <see cref="ContainerLifecycle.Warm"/>.</summary>
    public ContainerLifecycle Lifecycle { get; set; } = ContainerLifecycle.Warm;

    /// <summary>Gets or sets how a Python candidate is started. Defaults to <see cref="ContainerPythonLaunch.ForkSupervisor"/>.</summary>
    public ContainerPythonLaunch PythonLaunch { get; set; } = ContainerPythonLaunch.ForkSupervisor;

    /// <summary>Gets or sets how many candidates a warm container runs before it is replaced. Defaults to 1,000.</summary>
    /// <remarks>Every candidate already gets a clean workdir and no surviving process; replacing the container as
    /// well bounds anything subtler, such as kernel-side state the candidate's user could accumulate.</remarks>
    public int RecycleAfter { get; set; } = 1_000;

    /// <summary>Gets or sets the most processes and threads a container may hold, supervisor included. Defaults to 64.</summary>
    /// <remarks>Passed as <c>--pids-limit</c>; it is what turns a fork bomb into an ordinary failure.</remarks>
    public int PidsLimit { get; set; } = 64;

    /// <summary>Gets or sets the numeric user the container runs as. Defaults to 65534 (<c>nobody</c>); 0 (root) is refused.</summary>
    public int UserId { get; set; } = 65534;

    /// <summary>Gets or sets the numeric group the container runs as. Defaults to 65534 (<c>nogroup</c>); 0 is refused.</summary>
    public int GroupId { get; set; } = 65534;

    /// <summary>Gets or sets the size of the in-memory workdir mounted at <c>/work</c>, in megabytes. Defaults to 64.</summary>
    /// <remarks>
    /// The workdir is the only writable path in the container. It is a tmpfs mounted <c>nosuid,nodev,noexec</c> and
    /// owned by <see cref="UserId"/>; its pages count against the container's memory limit. Candidates see it as
    /// <c>HOME</c>, <c>TMPDIR</c> and the working directory.
    /// </remarks>
    public int WorkspaceSizeMb { get; set; } = 64;

    /// <summary>Gets or sets a seccomp profile file on the Docker host, or <c>null</c> for the daemon's default profile.</summary>
    /// <remarks>The engine refuses a daemon without seccomp support and verifies inside each container that a seccomp
    /// filter is active, so <c>null</c> never means unconfined.</remarks>
    public string? SeccompProfilePath { get; set; }

    /// <summary>Gets or sets the interpreter inside the image that runs the supervisor. Defaults to <c>python3</c>.</summary>
    public string SupervisorPython { get; set; } = "python3";

    /// <summary>Gets or sets how long a container may take to start and report ready, in seconds. Defaults to 60.</summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Gets the per-language commands run inside the container.</summary>
    /// <remarks>
    /// Defaults: Python runs <c>python3 {source}</c> and JavaScript runs <c>node {source}</c>, both resolved on the
    /// image's <c>PATH</c>. The argument template is split on white space (no shell, no quoting);
    /// <see cref="ProgramInterpreterSpecification.SourcePlaceholder"/> becomes the source file in <c>/work</c> and
    /// <see cref="ProgramInterpreterSpecification.WorkspacePlaceholder"/> becomes <c>/work</c>. The host-side
    /// <see cref="ProgramSandboxOptions.Interpreters"/> table is not used, because its commands name host paths.
    /// </remarks>
    public IDictionary<ProgramLanguage, ProgramInterpreterSpecification> Interpreters => _interpreters;

    /// <summary>Gets the host directories mounted read-only into every container, keyed by their absolute path inside it.</summary>
    /// <remarks>
    /// <para>
    /// Empty by default, so a container sees no host path. Use it for files an evaluator needs and cannot carry in its
    /// own source, such as the helpers an OpenEvolve evaluator imports from beside it. Each entry is passed as a
    /// <c>--mount type=bind,...,readonly</c> and the engine reads every mount back from the daemon, refusing a
    /// container in which any host path is writable or a mount it did not declare appears.
    /// </para>
    /// <para>
    /// Read-only is not private: the candidate runs as the same user as the evaluator, so it can read everything in a
    /// mounted directory. Keep credentials, held-out answers and anything else a candidate must not see out of it.
    /// The directory must be readable by <see cref="UserId"/>. Keys must be absolute paths of letters, digits,
    /// <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>, and must not be or lie under <c>/work</c>, <c>/proc</c>,
    /// <c>/sys</c> or <c>/dev</c>; values must be rooted host paths without a comma or a quote.
    /// </para>
    /// <para>
    /// A read-only mount stops writes but not <c>connect()</c>: a Unix socket inside it still accepts connections, and
    /// the Docker socket is root on the host. So a value is refused when it is the filesystem root; when it is, lies
    /// under, or contains <c>/run</c>, <c>/var/run</c>, <c>/var/lib/docker</c> or <c>/var/lib/containerd</c> (whatever
    /// is in them now); when it overlaps one of the current user's credential directories (<c>~/.ssh</c>,
    /// <c>~/.gnupg</c>, <c>~/.docker</c>, <c>~/.aws</c>, <c>~/.azure</c>, <c>~/.kube</c>, <c>~/.config/gcloud</c>);
    /// when it exists and is not a directory; and, on a Linux host, when a live Unix socket is bound at or anywhere
    /// below it, which includes the temporary directory itself while any .NET process runs. Symbolic links are
    /// expanded first. The engine also reads each mount's source back from <c>docker inspect</c> and refuses a
    /// container whose daemon mounted a different host path. The mounts are part of
    /// <see cref="ContainerExecutionEngine.VersionHash"/>; the files in them are not, so a caller that needs a
    /// changed file to change results should hash it into its own identity. All the mounts together are limited to
    /// <see cref="MaxReadOnlyMountChars"/> characters of <c>docker run</c> arguments: the supervisor script travels on
    /// the same command line, and Windows refuses one longer than 32,767 characters.
    /// </para>
    /// </remarks>
    public IDictionary<string, string> ReadOnlyMounts => _readOnlyMounts;

    /// <summary>The most characters <see cref="ReadOnlyMounts"/> may add to the <c>docker run</c> command line.</summary>
    public const int MaxReadOnlyMountChars = 4_096;

    /// <summary>Builds the default in-image interpreter table.</summary>
    /// <returns>Python as <c>python3</c> and JavaScript as <c>node</c>.</returns>
    public static Dictionary<ProgramLanguage, ProgramInterpreterSpecification> CreateDefaultInterpreters() => new()
    {
        [ProgramLanguage.Python] = new("python3", ProgramInterpreterSpecification.SourcePlaceholder),
        [ProgramLanguage.JavaScript] = new("node", ProgramInterpreterSpecification.SourcePlaceholder)
    };

    /// <summary>Creates an independent copy so a running engine is unaffected by later mutation.</summary>
    /// <returns>A new instance with the same values and a copied interpreter table.</returns>
    public ContainerExecutionOptions Clone()
    {
        var clone = new ContainerExecutionOptions
        {
            Image = Image,
            DockerExecutable = DockerExecutable,
            Lifecycle = Lifecycle,
            PythonLaunch = PythonLaunch,
            RecycleAfter = RecycleAfter,
            PidsLimit = PidsLimit,
            UserId = UserId,
            GroupId = GroupId,
            WorkspaceSizeMb = WorkspaceSizeMb,
            SeccompProfilePath = SeccompProfilePath,
            SupervisorPython = SupervisorPython,
            StartupTimeoutSeconds = StartupTimeoutSeconds
        };
        clone._interpreters.Clear();
        foreach (KeyValuePair<ProgramLanguage, ProgramInterpreterSpecification> entry in _interpreters)
            clone._interpreters[entry.Key] = entry.Value;
        foreach (KeyValuePair<string, string> mount in _readOnlyMounts)
            clone._readOnlyMounts[mount.Key] = mount.Value;
        return clone;
    }

    /// <summary>Rejects options that cannot be honoured or that would weaken the container.</summary>
    /// <exception cref="ArgumentException">A required text value is missing or unusable, or an interpreter entry is unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum is undefined or a size is out of range, or the user or group is root.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Image) || Image.Any(char.IsWhiteSpace) || Image.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("Image must name an image present on the Docker host, for example python:3.12-slim.", nameof(Image));
        if (string.IsNullOrWhiteSpace(DockerExecutable))
            throw new ArgumentException("DockerExecutable cannot be empty.", nameof(DockerExecutable));
        if (string.IsNullOrWhiteSpace(SupervisorPython) || SupervisorPython.Any(char.IsWhiteSpace))
            throw new ArgumentException("SupervisorPython must be one command name or absolute path inside the image.", nameof(SupervisorPython));
        if (SeccompProfilePath is not null && (string.IsNullOrWhiteSpace(SeccompProfilePath) ||
                                               string.Equals(SeccompProfilePath.Trim(), "unconfined", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "SeccompProfilePath must name a profile file, or be null for the daemon's default profile; 'unconfined' is refused.",
                nameof(SeccompProfilePath));
        }

        if (!Enum.IsDefined(typeof(ContainerLifecycle), Lifecycle))
            throw new ArgumentOutOfRangeException(nameof(Lifecycle), Lifecycle, "Value must be a defined lifecycle.");
        if (!Enum.IsDefined(typeof(ContainerPythonLaunch), PythonLaunch))
            throw new ArgumentOutOfRangeException(nameof(PythonLaunch), PythonLaunch, "Value must be a defined launch.");
        Require(RecycleAfter is >= 1 and <= MaxRecycleAfter, nameof(RecycleAfter), RecycleAfter,
            "Value must be between 1 and " + MaxRecycleAfter.ToString(CultureInfo.InvariantCulture) + ".");
        Require(PidsLimit is >= 8 and <= 65_536, nameof(PidsLimit), PidsLimit, "Value must be between 8 and 65536.");
        Require(UserId is > 0 and < int.MaxValue, nameof(UserId), UserId, "The container must not run as root (0).");
        Require(GroupId is > 0 and < int.MaxValue, nameof(GroupId), GroupId, "The container must not run in the root group (0).");
        Require(WorkspaceSizeMb is >= 1 and <= 65_536, nameof(WorkspaceSizeMb), WorkspaceSizeMb, "Value must be between 1 and 65536.");
        Require(StartupTimeoutSeconds is >= 1 and <= 3_600, nameof(StartupTimeoutSeconds), StartupTimeoutSeconds,
            "Value must be between 1 and 3600 seconds.");

        foreach (KeyValuePair<ProgramLanguage, ProgramInterpreterSpecification> entry in _interpreters)
        {
            if (entry.Value is null)
                throw new ArgumentException($"The interpreter for {entry.Key} cannot be null.", nameof(Interpreters));
            if (entry.Value.Executable.Any(char.IsWhiteSpace))
                throw new ArgumentException($"The executable for {entry.Key} must be one command name or path inside the image.", nameof(Interpreters));
            if (entry.Value.RunArgumentTemplate.IndexOf(ProgramInterpreterSpecification.SourcePlaceholder, StringComparison.Ordinal) < 0)
            {
                throw new ArgumentException(
                    $"The run command for {entry.Key} must contain the {ProgramInterpreterSpecification.SourcePlaceholder} placeholder.",
                    nameof(Interpreters));
            }
        }

        foreach (KeyValuePair<string, string> mount in _readOnlyMounts)
        {
            if (!IsMountTarget(mount.Key))
            {
                throw new ArgumentException(
                    "The read-only mount target '" + mount.Key + "' must be an absolute path of letters, digits, '.', '_', '-' and '/' " +
                    "that is not, and is not under, /work, /proc, /sys or /dev.",
                    nameof(ReadOnlyMounts));
            }

            if (string.IsNullOrWhiteSpace(mount.Value) || !Path.IsPathRooted(mount.Value) || mount.Value.IndexOfAny(MountSourceSeparators) >= 0)
            {
                throw new ArgumentException(
                    "The host directory for " + mount.Key + " must be a rooted path without a comma, a quote or a line break.",
                    nameof(ReadOnlyMounts));
            }

            string? refusal = ContainerMountSource.Refusal(mount.Value);
            if (refusal is not null)
            {
                throw new ArgumentException(
                    "The host directory for " + mount.Key + " cannot be mounted: it is " + refusal + ". Mount a directory that holds only " +
                    "what the evaluator needs.",
                    nameof(ReadOnlyMounts));
            }
        }

        // As the engine passes each one: "--mount" and "type=bind,source=...,target=...,readonly", space separated.
        long mountChars = _readOnlyMounts.Sum(mount => (long)"--mount type=bind,source=,target=,readonly  ".Length + mount.Key.Length + mount.Value.Length);
        if (mountChars > MaxReadOnlyMountChars)
        {
            throw new ArgumentException(
                "The read-only mounts add " + mountChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters to the docker command line; at most " +
                MaxReadOnlyMountChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " are allowed. Mount fewer or shorter paths.",
                nameof(ReadOnlyMounts));
        }
    }

    private static bool IsMountTarget(string path)
    {
        if (path.Length < 2 || path[0] != '/' || path[path.Length - 1] == '/') return false;
        if (path.Any(c => !(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/')))
            return false;
        string[] segments = path.Substring(1).Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == "..")) return false;
        return !ReservedMountRoots.Any(root =>
            string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private static void Require(bool condition, string name, object value, string message)
    {
        if (!condition) throw new ArgumentOutOfRangeException(name, value, name + ": " + message);
    }
}
