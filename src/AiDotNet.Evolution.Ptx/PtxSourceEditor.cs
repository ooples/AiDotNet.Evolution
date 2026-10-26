using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>One editable line of the entry body, addressed by its one-based line number and its exact hash.</summary>
internal sealed record PtxLineTarget(int Line, int Start, int Length, string Kind, string Sha256, string Text);

/// <summary>Line-addressed patching of a kernel body. Addresses always refer to the unchanged parent.</summary>
internal static class PtxSourceEditor
{
    /// <summary>
    /// The prompt bytes one editable line costs when patches are allowed: its catalog entry (about 110 bytes of line number,
    /// kind and hash) plus the line itself in the source (up to about 80 bytes of typical PTX). A proposal's editable-line
    /// bound is derived from <see cref="PtxProgramEvolutionOptions.MaxInputTokens"/> by this, so any permitted bound fits
    /// the prompt; the old fixed 1,024 refused generated kernels (a 128x128 SGEMM is ~3,250 lines) whatever the budget.
    /// </summary>
    internal const int PromptBytesPerEditableLine = 192;

    /// <summary>The largest catalog built for paths that never send it in a prompt.</summary>
    internal const int MaximumTargetsCeiling = 65_536;

    private static readonly string[] ProtectedDirectives = { ".entry", ".version", ".target", ".address_size", ".visible", "aidotnet-launch" };

    internal static IReadOnlyList<PtxLineTarget> Catalog(string source, PtxSourceInspection inspection,
        int maximumTargets = MaximumTargetsCeiling)
    {
        if (maximumTargets is < 1 or > MaximumTargetsCeiling) throw new ArgumentOutOfRangeException(nameof(maximumTargets));
        var targets = new List<PtxLineTarget>();
        string[] lines = source.Split('\n');
        int offset = 0;
        for (int index = 0; index < lines.Length; offset += lines[index].Length + 1, index++)
        {
            if (index < inspection.BodyFirstLine || index > inspection.BodyLastLine) continue;
            string text = lines[index].TrimEnd('\r');
            string trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (targets.Count == maximumTargets)
                throw new ArgumentException("The kernel body exceeds " + maximumTargets + " editable lines; raise " +
                    nameof(PtxProgramEvolutionOptions) + "." + nameof(PtxProgramEvolutionOptions.MaxInputTokens) +
                    " (each line costs about " + PromptBytesPerEditableLine + " prompt bytes; " +
                    nameof(PtxProgramEvolutionOptions.MaxEditableLines) + " follows it unless set), or disable patches to evolve by rewrite.");
            string kind = trimmed.EndsWith(':') ? "label" : trimmed.StartsWith('.') ? "declaration" : trimmed is "{" or "}" ? "scope" : "instruction";
            targets.Add(new PtxLineTarget(index + 1, offset, text.Length, kind, ProgramSnapshot.Digest(text), text));
        }
        if (targets.Count == 0) throw new ArgumentException("The kernel body has no editable lines.");
        return targets.AsReadOnly();
    }

    /// <summary>Replaces whole lines; an empty replacement deletes the line, a multi-line one inserts.</summary>
    /// <exception cref="ArgumentException">An edit does not address the catalog, overlaps another, or touches a protected directive.</exception>
    internal static string Apply(string source, IReadOnlyList<PtxLineTarget> catalog, IEnumerable<(int Line, string Sha256, string Replacement)> edits, int maximumEdits)
    {
        var byLine = catalog.ToDictionary(t => t.Line);
        var chosen = new SortedDictionary<int, string>();
        foreach ((int line, string sha256, string replacement) in edits)
        {
            if (chosen.Count == maximumEdits) throw new ArgumentException("Too many edits.");
            if (!byLine.TryGetValue(line, out PtxLineTarget? target) || !string.Equals(target.Sha256, sha256, StringComparison.Ordinal))
                throw new ArgumentException("Edit at line " + line + " does not match the parent's catalog.");
            if (replacement is null || replacement.Length > PtxSourceInspector.MaximumSourceCharacters)
                throw new ArgumentException("A replacement is missing or too long.");
            string code = PtxSourceInspector.StripComments(replacement);
            if (ProtectedDirectives.Any(d => code.Contains(d, StringComparison.Ordinal) || replacement.Contains("aidotnet-launch", StringComparison.Ordinal)))
                throw new ArgumentException("Edits may not change the entry signature, target or launch header; use the launch field.");
            if (!chosen.TryAdd(line, replacement.Replace("\r\n", "\n", StringComparison.Ordinal)))
                throw new ArgumentException("Two edits address line " + line + ".");
        }
        if (chosen.Count == 0) throw new ArgumentException("No edits.");
        string[] lines = source.Split('\n');
        var result = new List<string>(lines.Length + 16);
        for (int index = 0; index < lines.Length; index++)
        {
            if (!chosen.TryGetValue(index + 1, out string? replacement)) result.Add(lines[index]);
            else if (replacement.Length > 0) result.AddRange(replacement.Split('\n'));
        }
        string patched = string.Join("\n", result);
        if (patched == source) throw new ArgumentException("The patch did not change the source.");
        return patched;
    }
}