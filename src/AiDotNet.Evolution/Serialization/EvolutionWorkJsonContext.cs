using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

[JsonSerializable(typeof(EvolutionResourceLedger.State), TypeInfoPropertyName = "ResourceLedgerState")]
[JsonSerializable(typeof(EvolutionWorkState))]
internal sealed partial class EvolutionWorkJsonContext : JsonSerializerContext;
