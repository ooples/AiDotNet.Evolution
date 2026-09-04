using System.Text.Json;

namespace AiDotNet.Evolution;

internal static class EvolutionJson
{
    internal static readonly JsonSerializerOptions Compact = Create(writeIndented: false);
    internal static readonly JsonSerializerOptions Indented = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented) => new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = writeIndented
    };
}
