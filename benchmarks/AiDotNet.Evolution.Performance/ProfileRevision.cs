using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace AiDotNet.Evolution.Performance;

/// <summary>The source revision the report claims, checked against the revision compiled into the measured assembly.</summary>
public sealed record ProfileRevisionInfo(string Supplied, string AssemblyRevision, string InformationalVersion, string WorkingTree);

/// <summary>Refuses to label a report with a revision the built binaries do not carry.</summary>
public static class ProfileRevision
{
    /// <summary>Extracts the commit embedded by SourceLink in an informational version such as "1.2.3+&lt;sha&gt;".</summary>
    public static string? RevisionFrom(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        int plus = informationalVersion.LastIndexOf('+');
        if (plus < 0) return null;
        string candidate = informationalVersion[(plus + 1)..].Trim();
        return candidate.Length == 40 && candidate.All(char.IsAsciiHexDigit) ? candidate : null;
    }

    /// <summary>Fails unless the supplied revision is a 40-character commit that matches the measured assembly's own commit.</summary>
    public static void EnsureMatches(string supplied, string? informationalVersion)
    {
        if (supplied is null || supplied.Length != 40 || !supplied.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Supply the exact 40-character Git revision of the built source.", nameof(supplied));
        string? embedded = RevisionFrom(informationalVersion);
        if (embedded is null)
            throw new ArgumentException(
                "The measured assembly carries no source revision (informational version '" + (informationalVersion ?? "none") +
                "'); build from a Git checkout so SourceLink can stamp the commit.", nameof(informationalVersion));
        if (!string.Equals(embedded, supplied, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The supplied revision " + supplied + " is not the revision compiled into the measured assembly (" + embedded + ").", nameof(supplied));
    }

    /// <summary>Verifies the supplied revision against the core assembly and records whether the tracked tree was clean.</summary>
    public static ProfileRevisionInfo Verify(string supplied)
    {
        string informational = typeof(EvolutionSearchSpace).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        EnsureMatches(supplied, informational);
        return new ProfileRevisionInfo(supplied, RevisionFrom(informational)!, informational, WorkingTree());
    }

    /// <summary>Reports the tracked working-tree state; informational only, because Git need not be present.</summary>
    public static string WorkingTree()
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "status", "--porcelain", "--untracked-files=no" }) start.ArgumentList.Add(argument);
            using var git = Process.Start(start);
            if (git is null) return "unavailable";
            string output = git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            if (!git.WaitForExit(15000)) { git.Kill(entireProcessTree: true); return "unavailable"; }
            if (git.ExitCode != 0) return "unavailable";
            int changed = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            return changed == 0 ? "clean" : "modified-tracked-files=" + changed.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "unavailable";
        }
    }
}
