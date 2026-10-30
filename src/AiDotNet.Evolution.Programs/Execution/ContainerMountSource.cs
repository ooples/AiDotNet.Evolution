namespace AiDotNet.Evolution.Programs;

/// <summary>Decides which host paths may be bind-mounted into a candidate's container, and recognises a declared path
/// in the source <c>docker inspect</c> reports back.</summary>
/// <remarks>
/// A read-only bind stops writes, not <c>connect()</c>: a Unix socket mounted <c>readonly</c> still accepts connections,
/// so mounting the Docker socket (or any directory holding it) read-only hands the candidate the daemon, and the
/// daemon is root on the host. The same holds for the containerd and SSH-agent sockets and the runtime's own
/// diagnostic sockets in the temporary directory. A path is therefore refused when it is the filesystem root, when it
/// is, lies under, or contains a container runtime's state or socket directory or one of the current user's credential
/// directories, when it exists but is not a directory, and (on Linux, where <c>/proc/net/unix</c> lists every bound
/// socket of the network namespace) when a live Unix socket sits at or anywhere below it. Every check runs on the path
/// as written and, when the path is native to this host, again with its symbolic links expanded, so a link cannot
/// smuggle a refused directory past them. On macOS and Windows hosts the live-socket scan is not available, so the
/// fixed lists are what applies there.
/// </remarks>
internal static class ContainerMountSource
{
    // Container runtimes' control sockets and state. /run and /var/run hold the Docker, containerd and systemd sockets;
    // /private/var/run is what /var/run resolves to on macOS.
    private static readonly string[] DaemonLocations =
        { "/run", "/var/run", "/private/var/run", "/var/lib/docker", "/var/lib/containerd" };

    // Credential stores under the current user's home (~/.docker also holds Docker Desktop's socket on macOS).
    private static readonly string[] CredentialDirectories =
        { ".ssh", ".gnupg", ".docker", ".aws", ".azure", ".kube", Path.Combine(".config", "gcloud") };

    // Prefixes Docker Desktop puts in front of a shared host path when it reports a mount's source from inside its VM.
    private static readonly string[] DesktopSharePrefixes = { "/host_mnt", "/run/desktop/mnt/host", "/mnt/host" };

    private const int MaxLinkExpansions = 40;

    /// <summary>Explains why <paramref name="source"/> may not be mounted, or returns <c>null</c> when it may.</summary>
    /// <param name="source">A rooted host path without mount-field separators.</param>
    internal static string? Refusal(string source)
    {
        if (IsDevicePath(source))
            return "a Windows device or named-pipe path, which can carry the Docker engine's pipe";

        List<(string Path, bool Windows)> forms;
        bool native;
        try
        {
            forms = Forms(source, out native);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "a path this host cannot resolve (" + exception.Message + ")";
        }

        foreach ((string path, bool windows) in forms)
        {
            if (IsRoot(path, windows)) return "the filesystem root, which holds the container runtime's sockets";
            if (!windows)
            {
                foreach (string location in DaemonLocations)
                {
                    if (Overlaps(path, location, windows))
                        return "'" + path + "', which overlaps " + location + ", where container runtimes keep their sockets and state";
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length == 0 || IsWindowsForm(home) != windows) continue;
            foreach (string credentials in CredentialDirectories)
            {
                string protectedPath = Normalize(Path.Combine(home, credentials), windows);
                if (Overlaps(path, protectedPath, windows))
                    return "'" + path + "', which overlaps the credential directory " + protectedPath;
            }
        }

        if (!native) return null;
        string resolved = forms[forms.Count - 1].Path;
        if (!Directory.Exists(resolved) && File.Exists(resolved))
            return "'" + resolved + "', which is not a directory (a file, socket or device)";
        if (Directory.Exists(resolved))
        {
            string? socket = LiveSocketAtOrBelow(forms);
            if (socket is not null)
                return "a directory holding the live Unix socket " + socket + ", which a read-only mount still lets the candidate connect to";
        }

        return null;
    }

    /// <summary>Whether <paramref name="reported"/>, the source <c>docker inspect</c> shows for a bind mount, is the host
    /// path <paramref name="declared"/>.</summary>
    /// <remarks>A Linux daemon reports the path itself, cleaned (and possibly with its links expanded); Docker Desktop
    /// reports it as seen from its VM, behind a share prefix and, for a Windows path, as <c>/c/...</c>.</remarks>
    internal static bool IsReportedAs(string reported, string declared)
    {
        if (reported.Length == 0 || IsDevicePath(declared)) return false;
        var accepted = new List<(string Path, StringComparison Comparison)>();
        List<(string Path, bool Windows)> forms;
        try
        {
            forms = Forms(declared, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach ((string path, bool windows) in forms)
        {
            if (!windows)
            {
                accepted.Add((path, StringComparison.Ordinal));
                continue;
            }

            accepted.Add((path, StringComparison.OrdinalIgnoreCase));
            if (path.Length >= 2 && path[1] == ':')
                accepted.Add(("/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        string cleaned = reported.Length > 1 ? reported.TrimEnd('/', '\\') : reported;
        foreach ((string path, StringComparison comparison) in accepted)
        {
            if (string.Equals(cleaned, path, comparison)) return true;
            if (path.StartsWith("/", StringComparison.Ordinal) &&
                DesktopSharePrefixes.Any(prefix => string.Equals(cleaned, prefix + path, comparison)))
            {
                return true;
            }
        }

        return false;
    }

    // The path as written (normalised) and, when it names something on this host, with its links expanded (last).
    private static List<(string Path, bool Windows)> Forms(string source, out bool native)
    {
        bool windows = IsWindowsForm(source);
        native = windows == OperatingSystem.IsWindows();
        string normalized = Normalize(source, windows);
        var forms = new List<(string, bool)> { (normalized, windows) };
        if (native)
        {
            string resolved = ExpandLinks(normalized, windows);
            if (!string.Equals(resolved, normalized, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                forms.Add((resolved, windows));
        }

        return forms;
    }

    // A '/'-rooted path is a POSIX path (on Windows it names a path in Docker Desktop's VM); anything else is Windows.
    private static bool IsWindowsForm(string path) =>
        !(path.StartsWith("/", StringComparison.Ordinal) && !(OperatingSystem.IsWindows() && path.StartsWith("//", StringComparison.Ordinal)));

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith("//./", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal);

    private static string Normalize(string path, bool windows)
    {
        if (windows)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }

        var segments = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join("/", segments);
    }

    private static bool IsRoot(string path, bool windows)
    {
        if (!windows) return path == "/";
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return string.Equals(path.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // True when either path is the other or lies under it: mounting a parent exposes the child, and a child is inside it.
    private static bool Overlaps(string path, string location, bool windows)
    {
        StringComparison comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        char separator = windows ? '\\' : '/';
        return string.Equals(path, location, comparison) ||
               path.StartsWith(location.TrimEnd(separator) + separator, comparison) ||
               location.StartsWith(path.TrimEnd(separator) + separator, comparison);
    }

    // Expands every symbolic link along the path. A component that cannot be read ends the expansion, leaving the rest
    // as written; the fixed lists still apply to that form.
    private static string ExpandLinks(string path, bool windows)
    {
        string current = path;
        for (int expansion = 0; expansion < MaxLinkExpansions; expansion++)
        {
            string root = windows ? Path.GetPathRoot(current) ?? string.Empty : "/";
            string[] components = current.Substring(root.Length).Split(new[] { windows ? '\\' : '/' }, StringSplitOptions.RemoveEmptyEntries);
            string prefix = root;
            string? replaced = null;
            for (int index = 0; index < components.Length; index++)
            {
                string next = Path.Combine(prefix, components[index]);
                string? target;
                try
                {
                    FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                    if (!info.Exists && info.LinkTarget is null) return current;
                    target = info.LinkTarget;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return current;
                }

                if (target is null)
                {
                    prefix = next;
                    continue;
                }

                string rest = string.Join(windows ? "\\" : "/", components.Skip(index + 1));
                string linked = Path.IsPathRooted(target) ? target : Path.Combine(prefix, target);
                replaced = Normalize(rest.Length == 0 ? linked : Path.Combine(linked, rest), windows);
                break;
            }

            if (replaced is null) return current;
            current = replaced;
        }

        return current;
    }

    private static string? LiveSocketAtOrBelow(List<(string Path, bool Windows)> forms)
    {
        const string SocketTable = "/proc/net/unix";
        if (!OperatingSystem.IsLinux() || !File.Exists(SocketTable)) return null;
        var expandedDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(SocketTable).Skip(1))
        {
            // Num RefCount Protocol Flags Type St Inode Path: unbound sockets have no path, abstract ones start with '@'.
            string[] fields = line.Split(new[] { ' ' }, 8, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8) continue;
            string socket = fields[7].Trim();
            if (!socket.StartsWith("/", StringComparison.Ordinal)) continue;
            string normalized = Normalize(socket, windows: false);
            string directory = normalized.Substring(0, Math.Max(1, normalized.LastIndexOf('/')));
            if (!expandedDirectories.TryGetValue(directory, out string? expandedDirectory))
            {
                expandedDirectory = ExpandLinks(directory, windows: false);
                expandedDirectories[directory] = expandedDirectory;
            }

            string expanded = Normalize(expandedDirectory + "/" + normalized.Substring(normalized.LastIndexOf('/') + 1), windows: false);
            foreach ((string path, _) in forms)
            {
                if (IsAtOrBelow(normalized, path) || IsAtOrBelow(expanded, path)) return socket;
            }
        }

        return null;
    }

    private static bool IsAtOrBelow(string candidate, string directory) =>
        string.Equals(candidate, directory, StringComparison.Ordinal) ||
        candidate.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal);
}
