using System.Globalization;
using System.Text;

namespace AiDotNet.Evolution.Programs;

/// <summary>Summarizes the edits a reply made, in the form OpenEvolve records as a program's changes.</summary>
/// <remarks>
/// A single-line edit reads <c>Change 1: 'old' to 'new'</c>; a longer one shows both blocks, each line indented, cut
/// at <c>maxLineLength</c> characters and <c>maxLines</c> lines (OpenEvolve's <c>format_diff_summary</c>, with its
/// defaults of 100 and 30). Unlike OpenEvolve, which strips only the block as a whole, every line loses the
/// indentation the block shares, so continuation lines line up with the first.
/// </remarks>
internal static class ProgramDiffSummary
{
    /// <summary>The summary of a reply that replaced the whole program.</summary>
    internal const string FullRewrite = "Full rewrite";

    internal static string Format(IEnumerable<ProgramDiffBlock> blocks, int maxLineLength = 100, int maxLines = 30)
    {
        var summary = new StringBuilder();
        int number = 0;
        foreach (ProgramDiffBlock block in blocks)
        {
            number++;
            string[] search = Lines(block.SearchText);
            string[] replace = Lines(block.ReplaceText);
            if (summary.Length > 0) summary.Append('\n');
            summary.Append("Change ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(": ");
            if (search.Length == 1 && replace.Length == 1)
            {
                summary.Append('\'').Append(search[0]).Append("' to '").Append(replace[0]).Append('\'');
                continue;
            }

            summary.Append("Replace:\n").Append(Block(search, maxLineLength, maxLines))
                .Append("\nwith:\n").Append(Block(replace, maxLineLength, maxLines));
        }

        return summary.ToString();
    }

    // Drops the blank lines around the block and the indentation every line shares, so a nested edit keeps its
    // relative indentation and a one-line edit reads as its bare text.
    private static string[] Lines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
        int common = int.MaxValue;
        foreach (string line in lines)
        {
            if (line.Trim().Length == 0) continue;
            int indent = line.Length - line.TrimStart().Length;
            common = Math.Min(common, indent);
        }

        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].Trim().Length == 0 ? string.Empty : lines[i].Substring(common).TrimEnd();
        return lines;
    }

    private static string Block(string[] lines, int maxLineLength, int maxLines)
    {
        var block = new StringBuilder();
        int shown = Math.Min(lines.Length, maxLines);
        for (int i = 0; i < shown; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length > maxLineLength) line = line.Substring(0, maxLineLength - 3) + "...";
            if (block.Length > 0) block.Append('\n');
            block.Append("  ").Append(line);
        }

        if (lines.Length > maxLines)
            block.Append("\n  ... (").Append((lines.Length - maxLines).ToString(CultureInfo.InvariantCulture)).Append(" more lines)");
        return block.Length == 0 ? "  (empty)" : block.ToString();
    }
}
