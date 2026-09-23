using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>Which predeclared panel a policy trial belongs to.</summary>
/// <remarks>Only development observations may influence candidate selection; held-out observations are
/// measured after the champion is frozen and never fed back into it.</remarks>
[JsonConverter(typeof(EvolutionPolicyTrialPhaseConverter))]
[Experimental("AIDEVO002")]
public enum EvolutionPolicyTrialPhase
{
    /// <summary>The selection panel. Its observations choose the champion.</summary>
    Development,
    /// <summary>The held-out panel, run only after the champion is frozen.</summary>
    Holdout,
}

/// <summary>The terminal disposition of a policy optimization campaign.</summary>
/// <remarks>Closed on purpose. These values are the campaign's public verdict -- callers branch on them and
/// they are serialized into retained reports -- so they are an enumerated contract rather than free text a
/// future edit could silently reword.</remarks>
[JsonConverter(typeof(EvolutionPolicyCampaignOutcomeConverter))]
[Experimental("AIDEVO002")]
public enum EvolutionPolicyCampaignOutcome
{
    /// <summary>An unrecoverable or unclassified failure. This is the initial value, so an interrupted
    /// campaign reports failure rather than inheriting a more favourable verdict.</summary>
    Failed,
    /// <summary>No proposal beat the strongest baseline by the declared minimum gain, so nothing was held out.</summary>
    NoDevelopmentImprovement,
    /// <summary>The frozen champion passed every held-out baseline comparison.</summary>
    GeneralizationPassed,
    /// <summary>The frozen champion failed at least one held-out baseline comparison.</summary>
    GeneralizationRejected,
    /// <summary>The resource ledger denied admission, so the campaign stopped without a verdict.</summary>
    BudgetDenied,
    /// <summary>The caller canceled the campaign.</summary>
    Canceled,
    /// <summary>The campaign deadline elapsed, as distinct from caller cancellation.</summary>
    TimedOut,
    /// <summary>The inner-trial limit was reached before the campaign could conclude.</summary>
    TrialLimit,
    /// <summary>A single trial exceeded its own deadline, as distinct from the campaign deadline.</summary>
    InnerTimedOut,
    /// <summary>Retained inline evidence reached its declared byte limit.</summary>
    EvidenceLimit,
    /// <summary>A trial returned an invalid receipt or consumed more than its declared maximum.</summary>
    InvalidOrOverBudgetTrial,
}

/// <summary>The stable wire tokens for the closed campaign contracts.</summary>
/// <remarks>ONE SOURCE FOR THE TOKENS. They are not only a serialized form: the trial phase token is also
/// hashed into each trial's seed, so member names and wire tokens must never be allowed to diverge. A
/// reflection-based converter or a bare ToString would have capitalized the phase and silently reseeded
/// every campaign.</remarks>
internal static class EvolutionPolicyWire
{
    internal static string Token(EvolutionPolicyTrialPhase phase) => phase switch
    {
        EvolutionPolicyTrialPhase.Development => "development",
        EvolutionPolicyTrialPhase.Holdout => "holdout",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    internal static string Token(EvolutionPolicyCampaignOutcome outcome) => outcome switch
    {
        EvolutionPolicyCampaignOutcome.Failed => "Failed",
        EvolutionPolicyCampaignOutcome.NoDevelopmentImprovement => "NoDevelopmentImprovement",
        EvolutionPolicyCampaignOutcome.GeneralizationPassed => "GeneralizationPassed",
        EvolutionPolicyCampaignOutcome.GeneralizationRejected => "GeneralizationRejected",
        EvolutionPolicyCampaignOutcome.BudgetDenied => "BudgetDenied",
        EvolutionPolicyCampaignOutcome.Canceled => "Canceled",
        EvolutionPolicyCampaignOutcome.TimedOut => "TimedOut",
        EvolutionPolicyCampaignOutcome.TrialLimit => "TrialLimit",
        EvolutionPolicyCampaignOutcome.InnerTimedOut => "InnerTimedOut",
        EvolutionPolicyCampaignOutcome.EvidenceLimit => "EvidenceLimit",
        EvolutionPolicyCampaignOutcome.InvalidOrOverBudgetTrial => "InvalidOrOverBudgetTrial",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

/// <summary>Maps <see cref="EvolutionPolicyTrialPhase"/> to and from its stable wire token.</summary>
/// <remarks>EXPLICIT TOKENS, NOT MEMBER NAMES. A reflection-based string enum converter would tie the wire
/// format to C# identifiers, so renaming a member would silently rewrite retained reports, and
/// JsonStringEnumMemberName is unavailable on net8.0 and net471. Mapping here keeps one serialized form
/// across all three target frameworks and preserves the tokens these reports already carry.</remarks>
internal sealed class EvolutionPolicyTrialPhaseConverter : JsonConverter<EvolutionPolicyTrialPhase>
{
    public override EvolutionPolicyTrialPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "development" => EvolutionPolicyTrialPhase.Development,
            "holdout" => EvolutionPolicyTrialPhase.Holdout,
            _ => throw new JsonException("Unknown policy trial phase."),
        };

    public override void Write(Utf8JsonWriter writer, EvolutionPolicyTrialPhase value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);
        writer.WriteStringValue(EvolutionPolicyWire.Token(value));
    }
}

/// <summary>Maps <see cref="EvolutionPolicyCampaignOutcome"/> to and from its stable wire token.</summary>
/// <remarks>Explicit for the same reason as <see cref="EvolutionPolicyTrialPhaseConverter"/>: the tokens are
/// the retained contract, independent of future member renames.</remarks>
internal sealed class EvolutionPolicyCampaignOutcomeConverter : JsonConverter<EvolutionPolicyCampaignOutcome>
{
    public override EvolutionPolicyCampaignOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "Failed" => EvolutionPolicyCampaignOutcome.Failed,
            "NoDevelopmentImprovement" => EvolutionPolicyCampaignOutcome.NoDevelopmentImprovement,
            "GeneralizationPassed" => EvolutionPolicyCampaignOutcome.GeneralizationPassed,
            "GeneralizationRejected" => EvolutionPolicyCampaignOutcome.GeneralizationRejected,
            "BudgetDenied" => EvolutionPolicyCampaignOutcome.BudgetDenied,
            "Canceled" => EvolutionPolicyCampaignOutcome.Canceled,
            "TimedOut" => EvolutionPolicyCampaignOutcome.TimedOut,
            "TrialLimit" => EvolutionPolicyCampaignOutcome.TrialLimit,
            "InnerTimedOut" => EvolutionPolicyCampaignOutcome.InnerTimedOut,
            "EvidenceLimit" => EvolutionPolicyCampaignOutcome.EvidenceLimit,
            "InvalidOrOverBudgetTrial" => EvolutionPolicyCampaignOutcome.InvalidOrOverBudgetTrial,
            _ => throw new JsonException("Unknown policy campaign outcome."),
        };

    public override void Write(Utf8JsonWriter writer, EvolutionPolicyCampaignOutcome value, JsonSerializerOptions options)
    {
        Guard.NotNull(writer);
        writer.WriteStringValue(EvolutionPolicyWire.Token(value));
    }
}
