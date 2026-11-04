using System.Globalization;

namespace AiDotNet.Evolution.Programs;

internal static class ProgramSourceCompression
{
    internal static string Compress(string parentSource, string source, string reference)
    {
        if (string.IsNullOrEmpty(parentSource) || string.IsNullOrEmpty(source)) return source;
        string[] parent = parentSource.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < parent.Length; i++)
        {
            if (!positions.TryGetValue(parent[i], out List<int>? starts))
                positions[parent[i]] = starts = new List<int>();
            starts.Add(i);
        }

        var output = new List<string>();
        int budget = 100_000;
        int parentCursor = 0;
        int index = 0;
        while (index < lines.Length)
        {
            int best = 0;
            int bestStart = 0;
            if (positions.TryGetValue(lines[index], out List<int>? starts))
            {
                foreach (int start in starts)
                {
                    // Bound repeated-line searches, including rejected candidate starts.
                    if (--budget < 0) return source;
                    if (start < parentCursor) continue;
                    int length = 0;
                    while (index + length < lines.Length && start + length < parent.Length)
                    {
                        if (--budget < 0) return source;
                        if (!string.Equals(lines[index + length], parent[start + length], StringComparison.Ordinal)) break;
                        length++;
                    }
                    if (length > best) { best = length; bestStart = start; }
                }
            }

            if (best >= 4)
            {
                output.Add(lines[index]);
                output.Add("# ... (" + (best - 2).ToString(CultureInfo.InvariantCulture)
                    + " lines unchanged from " + reference + ") ...");
                output.Add(lines[index + best - 1]);
                index += best;
                parentCursor = bestStart + best;
            }
            else
            {
                output.Add(lines[index++]);
            }
        }

        string result = string.Join("\n", output);
        return result.Length < source.Length ? result : source;
    }
}
