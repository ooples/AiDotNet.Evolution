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
/// file does. The shim also refuses to run a file whose contents no longer match.
/// </remarks>
internal static class OpenEvolveEvaluatorShim
{
    private const string ResourceName = "AiDotNet.Evolution.Cli.OpenEvolve.openevolve_evaluator_shim.py";

    public static string Build(string evaluatorPath, RunOpenEvolveEvaluator options)
    {
        string configuration = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["evaluator_path"] = evaluatorPath,
            // Raw bytes, as the shim hashes them, so line endings cannot make the two disagree.
            ["evaluator_sha256"] = Sha256(File.ReadAllBytes(evaluatorPath)),
            ["cascade_evaluation"] = options.Cascade,
            ["cascade_thresholds"] = options.CascadeThresholds,
            ["timeout"] = options.TimeoutSeconds,
            ["file_suffix"] = options.FileSuffix,
            ["feature_dimensions"] = options.FeatureDimensions
        });
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

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes);
        var hex = new StringBuilder(digest.Length * 2);
        foreach (byte value in digest) hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}
