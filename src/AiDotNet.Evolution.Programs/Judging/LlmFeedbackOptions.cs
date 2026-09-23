// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Configuration/LlmFeedbackOptions.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Programs;

/// <summary>Configures how a language model's opinion of a program is blended into that program's fitness.</summary>
public sealed class LlmFeedbackOptions
{
    /// <summary>Maximum accepted model response characters, checked before parsing.</summary>
    public int MaxResponseChars { get; set; } = 65_536;

    /// <summary>Task-defined cost units per dispatched judge request; not a monetary price or token count.</summary>
    public double JudgeCallCostUnits { get; set; } = 1;
    /// <summary>The prefix given to every judge-produced metric name.</summary>
    public const string DefaultMetricPrefix = "llm_";

    /// <summary>The metric name holding the mean of the individual judge scores.</summary>
    public const string AverageMetricSuffix = "average";

    /// <summary>The JSON field the judge's written criticism is read from unless another is configured.</summary>
    public const string DefaultCritiqueField = "reasoning";

    /// <summary>The artifact key the carried-forward critique is attached under.</summary>
    public const string CritiqueArtifactKey = "llm_judge_critique";

    private IList<string>? _criteria;

    /// <summary>Gets or sets whether the judge runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Criteria scored by the judge; names must map to distinct JSON fields.</summary>
    public IList<string> Criteria
    {
        get => _criteria ??= new List<string> { "correctness", "efficiency", "readability" };
        set => _criteria = value;
    }

    /// <summary>Multiplier for judge utility, between zero and one.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Gets or sets the share of the final quality that stays with the measured evaluator.</summary>
    public double CombinedBlend { get; set; } = 0.7;

    /// <summary>Prefix for judge descriptors.</summary>
    public string MetricPrefix { get; set; } = DefaultMetricPrefix;

    /// <summary>Allows judging non-completed results without changing their failure status.</summary>
    public bool RunOnFailedEvaluations { get; set; }

    /// <summary>Gets or sets whether judge scores are appended to the result's objectives as well as its descriptors.</summary>
    public bool RecordObjectives { get; set; } = true;

    /// <summary>Gets or sets the JSON shape the judge's answer must take, or <c>null</c> to derive one from the criteria.</summary>
    public string? ResponseSchema { get; set; }

    /// <summary>Gets or sets whether the request asks the provider to constrain the answer to JSON.</summary>
    public bool RequestJsonResponseFormat { get; set; } = true;

    /// <summary>Gets or sets how many times an unparseable judge answer is requested again.</summary>
    public int MaxJudgeRetries { get; set; } = 1;

    /// <summary>Gets or sets whether every ensemble member judges and their scores are averaged by weight.</summary>
    public bool JudgeWithEveryEnsembleMember { get; set; }

    /// <summary>Gets or sets whether the judge's written criticism is carried forward to the next proposal.</summary>
    public bool CarryCritiqueForward { get; set; } = true;

    /// <summary>Gets or sets the JSON field the judge's written criticism is read from.</summary>
    public string CritiqueField { get; set; } = DefaultCritiqueField;

    /// <summary>Gets or sets the largest critique carried forward, in characters.</summary>
    /// <remarks>Longer text is cut and marked as truncated rather than dropped.</remarks>
    public int MaxCritiqueChars { get; set; } = 1_200;

    /// <summary>Gets or sets the sampling temperature for judge requests, or <c>null</c> for the client's default.</summary>
    public double? Temperature { get; set; }

    /// <summary>Gets or sets the output token cap for judge requests, or <c>null</c> for the client's default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Creates an independent copy so a running evaluator is unaffected by later mutation.</summary>
    /// <returns>A new options instance carrying the same values and a copied criteria list.</returns>
    public LlmFeedbackOptions Clone() => new()
    {
        MaxResponseChars = MaxResponseChars,
        JudgeCallCostUnits = JudgeCallCostUnits,
        Enabled = Enabled,
        _criteria = _criteria is null ? null : new List<string>(_criteria),
        Weight = Weight,
        CombinedBlend = CombinedBlend,
        MetricPrefix = MetricPrefix,
        RunOnFailedEvaluations = RunOnFailedEvaluations,
        RecordObjectives = RecordObjectives,
        ResponseSchema = ResponseSchema,
        RequestJsonResponseFormat = RequestJsonResponseFormat,
        MaxJudgeRetries = MaxJudgeRetries,
        JudgeWithEveryEnsembleMember = JudgeWithEveryEnsembleMember,
        CarryCritiqueForward = CarryCritiqueForward,
        CritiqueField = CritiqueField,
        MaxCritiqueChars = MaxCritiqueChars,
        Temperature = Temperature,
        MaxOutputTokens = MaxOutputTokens
    };

    /// <summary>Validates the criteria, the blend, and the request bounds.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Criteria"/> is empty or holds a blank entry, or <see cref="MetricPrefix"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Weight"/> or <see cref="CombinedBlend"/> is outside its range, or a retry or token bound is invalid.
    /// </exception>
    public void Validate()
    {
        if (MaxResponseChars is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(MaxResponseChars));
        if (!double.IsFinite(JudgeCallCostUnits) || JudgeCallCostUnits <= 0 || JudgeCallCostUnits > 1e12)
            throw new ArgumentOutOfRangeException(nameof(JudgeCallCostUnits));
        if (Criteria.Count > 64) throw new ArgumentException("At most 64 criteria are supported.", nameof(Criteria));
        if (ResponseSchema?.Length > 65_536) throw new ArgumentException("Response schema is too large.", nameof(ResponseSchema));
        if (MetricPrefix is null) throw new ArgumentException("MetricPrefix cannot be null.", nameof(MetricPrefix));
        if (Criteria.Count == 0)
            throw new ArgumentException("At least one judging criterion is required.", nameof(Criteria));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string criterion in Criteria)
        {
            if (criterion is not { } text || text.Trim().Length == 0)
                throw new ArgumentException("A judging criterion cannot be empty or white space.", nameof(Criteria));
            if (!seen.Add(text.Trim()))
                throw new ArgumentException("Judging criteria must be distinct.", nameof(Criteria));
        }

        if (double.IsNaN(Weight) || double.IsInfinity(Weight) || Weight < 0 || Weight > 1)
            throw new ArgumentOutOfRangeException(nameof(Weight), Weight, "Value must be between 0 and 1.");
        if (double.IsNaN(CombinedBlend) || double.IsInfinity(CombinedBlend) || CombinedBlend < 0 || CombinedBlend > 1)
            throw new ArgumentOutOfRangeException(nameof(CombinedBlend), CombinedBlend, "Value must be between 0 and 1.");
        if (MaxJudgeRetries < 0 || MaxJudgeRetries > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxJudgeRetries), MaxJudgeRetries, "Value must be between 0 and 8.");
        if (MaxOutputTokens.HasValue && MaxOutputTokens.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens), MaxOutputTokens.Value, "Value must be positive.");
        if (CritiqueField is not { } critiqueField || critiqueField.Trim().Length == 0)
            throw new ArgumentException("CritiqueField cannot be empty or white space.", nameof(CritiqueField));

        // A critique field colliding with a criterion would make the same key mean both a score and prose, and the
        // judge would have to pick one. Rejecting it here beats an unparseable answer per candidate at run time.
        if (seen.Contains(CritiqueField.Trim()))
            throw new ArgumentException("CritiqueField cannot name one of the judging criteria.", nameof(CritiqueField));
        var fields = Criteria.Select(Prompts.ProgramPromptBuilder.ToCriterionFieldName).ToArray();
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || fields.Contains(AverageMetricSuffix)
            || fields.Contains(CritiqueField.Trim()))
            throw new ArgumentException("Criterion JSON names must be distinct and cannot collide with average or CritiqueField.", nameof(Criteria));
        foreach (string field in fields.Append(AverageMetricSuffix))
        {
            string name = MetricPrefix + field;
            if (name.Length is < 1 or > 64 || name != name.Trim() || name.Any(char.IsControl))
                throw new ArgumentException("Judge descriptor names must be bounded and canonical.", nameof(MetricPrefix));
        }
        if (MaxCritiqueChars < 1 || MaxCritiqueChars > EvolutionArtifact.MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCritiqueChars), MaxCritiqueChars,
                "Value must be between 1 and " + EvolutionArtifact.MaximumTextLength.ToString(CultureInfo.InvariantCulture) + ".");
        }
        if (Temperature.HasValue
            && (double.IsNaN(Temperature.Value) || double.IsInfinity(Temperature.Value)
                || Temperature.Value < 0 || Temperature.Value > 2))
        {
            throw new ArgumentOutOfRangeException(nameof(Temperature), Temperature.Value,
                "Value must be a finite number between 0 and 2.");
        }
    }
}
