using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

[JsonSerializable(typeof(EvolutionResourceLedger.State), TypeInfoPropertyName = "ResourceLedgerState")]
[JsonSerializable(typeof(EvolutionWorkState))]
[JsonSerializable(typeof(DurableSessionWorkDocument))]
internal sealed partial class EvolutionWorkJsonContext : JsonSerializerContext;
