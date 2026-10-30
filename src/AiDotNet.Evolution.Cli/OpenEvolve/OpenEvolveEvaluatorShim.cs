using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Cli;

/// <summary>
/// Builds the Python script that runs an unmodified OpenEvolve evaluator (<c>evaluate(program_path)</c>, or
/// <c>evaluate_stage1..3</c> with cascade thresholds) under this CLI's evaluator contract.
/// </summary>
/// <remarks>
/// The script is the embedded shim with its configuration prepended. The configuration carries the evaluator's
/// SHA-256, so the evaluator script's identity, and with it checkpoint compatibility, changes whenever the evaluator
/// file does. The shim also refuses to run a file whose contents no longer match. It also carries a digest of every
/// Python source the evaluator can import from its own directory and its <c>pythonPath</c> entries, so editing a
/// helper between <c>run</c> and <c>resume</c> changes the identity too, and the resume is refused rather than mixing
/// scores from two evaluators in one archive. Data files those directories hold are not part of the digest.
/// </remarks>
internal static class OpenEvolveEvaluatorShim
{
    private const string ResourceName = "AiDotNet.Evolution.Cli.OpenEvolve.openevolve_evaluator_shim.py";

    /// <param name="evaluatorPath">The evaluator file on this machine; its bytes are hashed here.</param>
    /// <param name="runtimePath">Where the shim finds the same file when it runs: <paramref name="evaluatorPath"/> for a
    /// host process, or its path through a read-only mount inside a container.</param>
    /// <param name="options">The run file's <c>openEvolveEvaluator</c> section.</param>
    /// <param name="helperDirectories">The evaluator's directory, then each <c>pythonPath</c> entry, on this machine.</param>
    /// <param name="excludedDirectory">The run's output directory, left out of the digest when it lies inside one of
    /// <paramref name="helperDirectories"/>: the run writes <c>best.py</c> there.</param>
    public static string Build(string evaluatorPath, string runtimePath, RunOpenEvolveEvaluator options,
        IReadOnlyList<string> helperDirectories, string? excludedDirectory)
    {
        var settings = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["evaluator_path"] = runtimePath,
            // Raw bytes, as the shim hashes them, so line endings cannot make the two disagree.
            ["evaluator_sha256"] = Sha256(File.ReadAllBytes(evaluatorPath)),
            ["helpers_sha256"] = HelperSourcesSha256(helperDirectories, excludedDirectory),
            ["cascade_evaluation"] = options.Cascade,
            ["cascade_thresholds"] = options.CascadeThresholds,
            ["timeout"] = options.TimeoutSeconds,
            ["file_suffix"] = options.FileSuffix,
            ["feature_dimensions"] = options.FeatureDimensions
        };

        // Added only when set, so the shim's config.get("fitness_metric") sees None and keeps OpenEvolve's
        // own rule. Omitting the key says "not configured" without putting a null into a dictionary whose
        // values are not nullable.
        if (options.FitnessMetric is { } fitnessMetric && fitnessMetric.Trim().Length > 0)
            settings["fitness_metric"] = fitnessMetric;

        string configuration = JsonSerializer.Serialize(settings);
        // Base64 keeps paths, quotes and non-ASCII text out of Python's string-literal rules.
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(configuration));
        return "import base64 as _b64, json as _json\nCONFIG = _json.loads(_b64.b64decode(\"" + encoded + "\").decode(\"utf-8\"))\n" +
               Shim();
    }

    private static string Shim()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The OpenEvolve evaluator shim is missing from the tool.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// SHA-256 over every <c>*.py</c> file under each directory, by position, relative path and bytes; hidden and
    /// linked entries and <c>__pycache__</c> are skipped. Positions rather than host paths, so moving a checkout keeps
    /// the identity while editing, adding or removing a helper changes it.
    /// </summary>
    internal static string HelperSourcesSha256(IReadOnlyList<string> directories, string? excludedDirectory)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        string? excluded = excludedDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedDirectory)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < directories.Count; index++)
        {
            string directory = Path.GetFullPath(directories[index]);
            Append(hash, "directory " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> files = Directory.EnumerateFiles(directory, "*.py", enumeration)
                .Where(file => excluded is null || !file.StartsWith(excluded, comparison))
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
                .Where(relative => !relative.Split('/').Contains("__pycache__"))
                .OrderBy(relative => relative, StringComparer.Ordinal);
            foreach (string relative in files)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, relative));
                Append(hash, relative + "\n" + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                hash.AppendData(bytes);
            }
        }

        return Hex(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    private static string Hex(byte[] digest)
    {
        var hex = new StringBuilder(digest.Length * 2);
        foreach (byte value in digest) hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}
