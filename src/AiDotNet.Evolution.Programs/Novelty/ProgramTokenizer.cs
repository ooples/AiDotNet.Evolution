// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramTokenizer.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs.Novelty;

internal static class ProgramTokenizer
{
    internal static HashSet<string> Tokenize(string source)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        while (index < source.Length)
        {
            char character = source[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (IsWordCharacter(character))
            {
                int start = index;
                while (index < source.Length && IsWordCharacter(source[index])) index++;
                tokens.Add(source.Substring(start, index - start));
                continue;
            }

            tokens.Add(character.ToString());
            index++;
        }

        return tokens;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';
}
