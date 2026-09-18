// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/LlmJudgeProgramFitnessEvaluator.cs
// Original license retained in Programs/Legacy/AIDOTNET-LICENSE.txt.
using System.Globalization;
using AiDotNet.Evolution.Prompts;
using Newtonsoft.Json.Linq;


namespace AiDotNet.Evolution.Programs;

/// <summary>Blends a language model's judgement of a program into the score a measured evaluator produced.</summary>
public sealed class LlmJudgeProgramFitnessEvaluator : IProgramFitnessEvaluator
{
    private const int MaxDiagnosticLength = 240;

    private readonly IProgramChatClient _chatClient;
    private readonly IProgramFitnessEvaluator _inner;
    private readonly ProgramPromptBuilder _promptBuilder;
    private readonly LlmFeedbackOptions _options;
    private readonly string[] _criteria;
    private readonly string[] _fieldNames;
    private readonly string _identity;
    private long _judgeCalls;
    private long _judgeFailures;

    /// <summary>Initializes a judging evaluator around a measured one.</summary>
    /// <param name="chatClient">The chat client that answers judging requests; supplied by the caller.</param>
    /// <param name="inner">The measured evaluator whose score the judge adjusts.</param>
    /// <param name="promptBuilder">The builder that renders judging prompts; <c>null</c> uses the defaults.</param>
    /// <param name="options">Criteria, weighting, and blending settings; <c>null</c> uses the defaults.</param>
    /// <param name="id">A stable evaluator identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> or <paramref name="inner"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white space, or an option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is outside its permitted range.</exception>
    public LlmJudgeProgramFitnessEvaluator(
        IProgramChatClient chatClient,
        IProgramFitnessEvaluator inner,
        ProgramPromptBuilder? promptBuilder = null,
        LlmFeedbackOptions? options = null,
        string id = "llm-judge-program-evaluator")
    {
        ProgramGuard.NotNull(chatClient);
        ProgramGuard.NotNull(inner);
        ProgramGuard.NotNullOrWhiteSpace(id);

        LlmFeedbackOptions copy = (options ?? new LlmFeedbackOptions()).Clone();
        copy.Validate();

        _chatClient = chatClient;
        _inner = inner;
        _promptBuilder = promptBuilder ?? new ProgramPromptBuilder();
        _options = copy;
        _criteria = copy.Criteria.Select(criterion => criterion.Trim()).ToArray();
        _fieldNames = _criteria.Select(ProgramPromptBuilder.ToCriterionFieldName).ToArray();
        if (FindPanel(chatClient) is not null && !copy.JudgeWithEveryEnsembleMember)
            throw new ArgumentException("A judge panel requires JudgeWithEveryEnsembleMember.", nameof(options));
        Id = id.Trim();
        _identity = CaptureIdentity();
        VersionHash = "llm-judge-v3-" + EvolutionHash.Combine(new[] { _identity, _promptBuilder.VersionHash, Newtonsoft.Json.JsonConvert.SerializeObject(copy) });
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string VersionHash { get; }

    /// <summary>Gets the measured evaluator whose score this judge adjusts.</summary>
    public IProgramFitnessEvaluator Inner => _inner;

    /// <summary>Gets a copy of the judging settings this evaluator uses.</summary>
    /// <returns>An independent copy; mutating it does not affect the evaluator.</returns>
    public LlmFeedbackOptions GetOptions() => _options.Clone();

    /// <summary>Gets how many judging requests were sent since this evaluator was constructed.</summary>
    public long JudgeCalls => Interlocked.Read(ref _judgeCalls);

    /// <summary>Gets how many candidates ended with no usable judge answer and kept their measured score.</summary>
    public long JudgeFailures => Interlocked.Read(ref _judgeFailures);

    /// <inheritdoc/>
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(
        ProgramGenome candidate,
        EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(candidate);
        ProgramGuard.NotNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdentity();

        EvolutionTaskResult measured = await _inner
            .EvaluateAsync(candidate, context, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdentity();

        if (measured is null)
        {
            return EvolutionTaskResult.Failed(
                "llm_judge_inner_returned_null", "The measured evaluator returned no result.");
        }

        if (!_options.Enabled) return measured;
        if (measured.Status != EvolutionEvaluationStatus.Completed && !_options.RunOnFailedEvaluations) return measured;
        if (!measured.Quality.HasValue) return measured;

        if ((_options.CombinedBlend < 1 && (measured.Quality.Value is < 0 or > 1))
            || measured.Descriptors.Count + _fieldNames.Length + 1 > EvolutionTaskResult.MaximumNamedValues
            || (_options.RecordObjectives && measured.Objectives.Count + _fieldNames.Length > EvolutionTaskResult.MaximumVectorValues)
            || _fieldNames.Append(LlmFeedbackOptions.AverageMetricSuffix).Any(n => measured.Descriptors.ContainsKey(_options.MetricPrefix + n)))
            return Refuse(measured, "llm_judge_incompatible_measurement",
                "Blending requires normalized quality in [0,1], noncolliding names, and capacity for judge fields.");

        // The original scalar's uncertainty does not describe a model-blended score. Refuse before model spend.
        if (measured.MeasurementOrigin is not null)
        {
            var diagnostics = measured.Diagnostics.Take(EvolutionTaskResult.MaximumDiagnostics - 1).Concat(new[]
            {
                new EvolutionDiagnostic("program_judge_measurement_origin_unsupported",
                    "Origin-bearing measurements require a separately defined combined-score provenance model before LLM blending.")
            });
            return new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, measured.Quality, measured.Direction,
                measured.Descriptors, measured.Objectives, measured.ConstraintViolations, measured.CostUnits,
                diagnostics, measured.Metrics, measured.Artifacts).WithMeasurementOrigin(measured.MeasurementOrigin);
        }

        JudgeOutcome outcome = await JudgeAsync(candidate, context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdentity();
        if (!outcome.HasScores)
        {
            Interlocked.Increment(ref _judgeFailures);
            return Append(measured, measured.Quality.Value, outcome, outcome.Diagnostic);
        }

        double judgeQuality = measured.Direction == EvolutionOptimizationDirection.Minimize ? 1 - outcome.Average : outcome.Average;
        double blended = (_options.CombinedBlend * measured.Quality.Value)
            + ((1.0 - _options.CombinedBlend) * judgeQuality);
        return Append(measured, blended, outcome, outcome.Diagnostic);
    }

    private async Task<JudgeOutcome> JudgeAsync(
        ProgramGenome candidate,
        EvolutionEvaluationContext context,
        CancellationToken cancellationToken)
    {
        string schema = _options.ResponseSchema ?? Newtonsoft.Json.JsonConvert.SerializeObject(
            _fieldNames.ToDictionary(n => n, _ => (object)"number in [0,1]").Concat(
                new[] { new KeyValuePair<string, object>(_options.CritiqueField.Trim(), "string") }).ToDictionary(p => p.Key, p => p.Value));
        IReadOnlyList<ProgramChatMessage> messages = Array.AsReadOnly(_promptBuilder.BuildEvaluationMessages(
            candidate, _criteria, schema).ToArray());
        StableRandom random = context.CreateRandom();

        // The ensemble is usually wrapped: the standard way to build a production client adds retry and telemetry
        // middleware around it, and a plain type test on the outermost object would then quietly fall back to
        // single-member judging with nothing to say why.
        if (_options.JudgeWithEveryEnsembleMember && FindPanel(_chatClient) is { } panel)
        {
            return await JudgeWithPanelAsync(panel, messages, random, cancellationToken).ConfigureAwait(false);
        }

        string? lastProblem = null;
        int attempts = _options.MaxJudgeRetries + 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureIdentity();
            Interlocked.Increment(ref _judgeCalls);

            string answer;
            try
            {
                ProgramChatResponse response = await _chatClient
                    .GetResponseAsync(messages, BuildChatOptions(random), cancellationToken)
                    .ConfigureAwait(false);
                answer = response is null ? string.Empty : response.Text;
                cancellationToken.ThrowIfCancellationRequested();
                if (response?.ModelId is { } modelId && modelId != _chatClient.ModelId)
                    throw new InvalidOperationException("The response model does not match the pinned provider.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
#pragma warning restore CA1031
            {
                // Only the exception type reaches a diagnostic; a provider message can carry a key or an endpoint.
                lastProblem = "the request failed with " + exception.GetType().Name;
                continue;
            }

            if (TryReadScores(answer, out double[] scores, out string? critique, out string problem))
            {
                return JudgeOutcome.FromScores(_fieldNames, scores, _options.Weight, critique).WithCalls(attempt + 1);
            }

            lastProblem = problem;
        }

        return JudgeOutcome.Unusable(new EvolutionDiagnostic(
            "llm_judge_unusable",
            ProgramText.Bound(
                "The judge produced no usable scores after " +
                attempts.ToString(CultureInfo.InvariantCulture) + " attempts: " +
                (lastProblem ?? "no reason was recorded") + ".",
                MaxDiagnosticLength),
            isRedacted: true)).WithCalls(attempts);
    }

    /// <summary>Finds the weighted ensemble a client is, or wraps, so panel judging survives the usual middleware.</summary>
    /// <param name="client">The configured chat client.</param>
    /// <returns>The ensemble, or <c>null</c> when there is none to find.</returns>
    private static ProgramJudgePanel? FindPanel(IProgramChatClient client)
    {
        IProgramChatClient? current = client;
        for (int depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is ProgramJudgePanel panel) return panel;
            current = (current as IProgramChatClientDecorator)?.Inner;
        }
        return null;
    }

    /// <summary>Scores a candidate with every ensemble member and averages each criterion by member weight.</summary>
    private async Task<JudgeOutcome> JudgeWithPanelAsync(
        ProgramJudgePanel panel,
        IReadOnlyList<ProgramChatMessage> messages,
        StableRandom random,
        CancellationToken cancellationToken)
    {
        int dispatched = 0;
        IReadOnlyList<ProgramChatResponse?> responses;
        try
        {
            responses = await panel
                .GetResponsesAsync(messages, BuildChatOptions(random), () =>
                {
                    EnsureIdentity();
                    dispatched++;
                    Interlocked.Increment(ref _judgeCalls);
                }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Only the exception type reaches a diagnostic; a provider message can carry a key or an endpoint.
            return JudgeOutcome.Unusable(new EvolutionDiagnostic(
                "llm_judge_unusable",
                "The judge panel failed with " + exception.GetType().Name + ".",
                isRedacted: true)).WithCalls(dispatched);
        }

        var totals = new double[_fieldNames.Length];
        var weights = new double[_fieldNames.Length];
        int answered = 0;
        var usable = new List<(double[] Scores, double Weight)>();
        string? lastProblem = null;
        var critiques = new List<string>();

        for (int index = 0; index < responses.Count && index < panel.Members.Count; index++)
        {
            ProgramChatResponse? response = responses[index];
            if (response is null || (response.ModelId is { } modelId && modelId != panel.Members[index].Client.ModelId))
            {
                lastProblem = "a member returned no answer";
                continue;
            }

            if (!TryReadScores(response.Text, out double[] scores, out string? memberCritique, out string problem))
            {
                lastProblem = problem;
                continue;
            }

            double weight = panel.Members[index].Weight;
            if (weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight)) continue;

            // Numbered rather than named: a member's model identifier is configuration, and the next prompt has no
            // use for it beyond telling two opinions apart.
            if (memberCritique is not null)
            {
                critiques.Add("Judge " + (critiques.Count + 1).ToString(CultureInfo.InvariantCulture) + ": " + memberCritique);
            }

            answered++;
            usable.Add((scores, weight));
        }

        if (answered == 0)
        {
            return JudgeOutcome.Unusable(new EvolutionDiagnostic(
                "llm_judge_unusable",
                ProgramText.Bound(
                    "No member of the judge panel produced usable scores: " +
                    (lastProblem ?? "no reason was recorded") + ".",
                    MaxDiagnosticLength),
                isRedacted: true)).WithCalls(panel.Members.Count);
        }

        var averaged = new double[totals.Length];
        double maxWeight = usable.Max(m => m.Weight);
        foreach (var member in usable)
        {
            double weight = member.Weight / maxWeight;
            for (int field = 0; field < totals.Length; field++)
            {
                totals[field] += member.Scores[field] * weight;
                weights[field] += weight;
            }
        }
        for (int field = 0; field < totals.Length; field++)
            averaged[field] = weights[field] > 0 ? totals[field] / weights[field] : 0;

        // One combined critique rather than one artifact per member: the bound is on the whole text either way, and
        // a single ordered block is what a proposing model can actually read.
        string? panelCritique = critiques.Count == 0
            ? null
            : ProgramText.Bound(string.Join("\n\n", critiques), _options.MaxCritiqueChars);

        return JudgeOutcome.FromScores(_fieldNames, averaged, _options.Weight, panelCritique).WithCalls(panel.Members.Count);
    }

    private bool TryReadScores(string answer, out double[] scores, out string? critique, out string problem)
    {
        scores = Array.Empty<double>();
        critique = null;
        if (string.IsNullOrWhiteSpace(answer))
        {
            problem = "the answer was empty";
            return false;
        }

        if (!JudgeJson.TryExtract(answer, _options.MaxResponseChars, out JObject json))
        {
            problem = "the answer held no JSON object";
            return false;
        }

        var values = new double[_fieldNames.Length];
        for (int index = 0; index < _fieldNames.Length; index++)
        {
            if (!JudgeJson.TryReadNumber(json, _fieldNames[index], out double value))
            {
                problem = "the answer had no finite number for one criterion";
                return false;
            }

            // A judge that answers 1.4 is clamped rather than rejected: the intent is unambiguous and refusing it
            // would spend another call to learn nothing.
            values[index] = value < 0 ? 0 : value > 1 ? 1 : value;
        }

        scores = values;
        critique = ReadCritique(json);
        problem = string.Empty;
        return true;
    }

    /// <summary>Reads the judge's written criticism out of an answer that already parsed.</summary>
    /// <param name="json">The judge's answer.</param>
    /// <returns>The bounded, sanitized criticism, or <c>null</c> when there is none worth carrying.</returns>
    private string? ReadCritique(JObject json)
    {
        if (!_options.CarryCritiqueForward) return null;

        JToken? token = json[_options.CritiqueField.Trim()];
        if (token is null || token.Type == JTokenType.Null) return null;

        // Read as a scalar string: a judge that answers with an object or an array here has not followed the schema,
        // and serializing whatever it did send would put unbounded JSON into the next prompt.
        // Written as an explicit null test rather than IsNullOrWhiteSpace, which net471 does not annotate for flow
        // analysis, so the shorter form fails the nullable build on that target alone.
        if (token.Type != JTokenType.String || token.Value<string>() is not { } text || text.Trim().Length == 0)
            return null;

        string sanitized = ProgramText.Sanitize(text).Trim();
        return sanitized.Length == 0 ? null : ProgramText.Bound(sanitized, _options.MaxCritiqueChars);
    }

    private ProgramChatOptions BuildChatOptions(StableRandom random)
    {
        var options = new ProgramChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.MaxOutputTokens,
            Seed = unchecked((int)(random.NextUInt32() & 0x7FFFFFFF))
        };

        if (_options.RequestJsonResponseFormat) options.ResponseFormat = ProgramChatResponseFormat.Json;
        return options;
    }

    private EvolutionTaskResult Append(
        EvolutionTaskResult measured,
        double quality,
        JudgeOutcome? outcome,
        EvolutionDiagnostic? diagnostic)
    {
        var descriptors = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, double> pair in measured.Descriptors) descriptors[pair.Key] = pair.Value;

        var objectives = new List<double>(measured.Objectives);
        if (outcome is not null && outcome.HasScores)
        {
            for (int index = 0; index < outcome.Names.Count; index++)
            {
                descriptors[_options.MetricPrefix + outcome.Names[index]] = outcome.Scores[index];
                if (_options.RecordObjectives) objectives.Add(measured.Direction == EvolutionOptimizationDirection.Minimize
                    ? 1 - outcome.Scores[index] : outcome.Scores[index]);
            }

            descriptors[_options.MetricPrefix + LlmFeedbackOptions.AverageMetricSuffix] = outcome.Average;
        }

        var diagnostics = new List<EvolutionDiagnostic>(measured.Diagnostics);
        if (diagnostic is not null && diagnostics.Count < 64) diagnostics.Add(diagnostic);

        // The judge's written criticism travels as an artifact, which is the channel the engine already shows to
        // whoever proposes this candidate's successor. Attaching it here is what turns one judge's opinion into
        // something the search can answer rather than rediscover.
        var artifacts = new List<EvolutionArtifact>(measured.Artifacts);
        if (outcome?.Critique is { } critique && artifacts.Count < EvolutionTaskResult.MaximumArtifacts)
        {
            artifacts.Add(new EvolutionArtifact(
                LlmFeedbackOptions.CritiqueArtifactKey,
                critique,
                isTruncated: critique.Length >= _options.MaxCritiqueChars,
                isRedacted: false));
        }

        // Metrics and artifacts the measured evaluator reported are carried rather than replaced: the judge is a
        // wrapper, and a wrapper that silently drops what it wraps makes every metric query wrong for judged runs.
        return new EvolutionTaskResult(
            measured.Status,
            quality,
            measured.Direction,
            descriptors,
            objectives,
            measured.ConstraintViolations,
            measured.CostUnits + (outcome?.Calls ?? 0) * _options.JudgeCallCostUnits,
            diagnostics,
            measured.Metrics,
            artifacts);
    }

    private string CaptureIdentity()
    {
        var parts = new List<string> { _inner.Id, _inner.VersionHash };
        IProgramChatClient? current = _chatClient;
        var seen = new HashSet<IProgramChatClient>(ReferenceEqualityComparer.Instance);
        while (current is not null)
        {
            if (!seen.Add(current) || seen.Count > 8)
                throw new ArgumentException("Chat decorators must be acyclic and at most eight clients deep.");
            parts.Add(current.ModelId);
            current = (current as IProgramChatClientDecorator)?.Inner;
        }
        foreach (string part in parts) ProgramGuard.NotNullOrWhiteSpace(part);
        return EvolutionHash.Combine(parts);
    }

    private void EnsureIdentity()
    {
        if (CaptureIdentity() != _identity)
            throw new InvalidOperationException("The judge or measured evaluator identity changed during the run.");
    }

    private static EvolutionTaskResult Refuse(EvolutionTaskResult measured, string code, string message)
    {
        var result = new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, measured.Quality, measured.Direction, measured.Descriptors,
            measured.Objectives, measured.ConstraintViolations, measured.CostUnits,
            measured.Diagnostics.Take(EvolutionTaskResult.MaximumDiagnostics - 1).Append(new EvolutionDiagnostic(code, message)),
            measured.Metrics, measured.Artifacts);
        return measured.MeasurementOrigin is { } origin ? result.WithMeasurementOrigin(origin) : result;
    }

    private sealed class JudgeOutcome
    {
        public int Calls { get; private set; }
        public JudgeOutcome WithCalls(int calls) { Calls = calls; return this; }
        private JudgeOutcome(
            IReadOnlyList<string> names,
            IReadOnlyList<double> scores,
            double average,
            EvolutionDiagnostic? diagnostic,
            string? critique = null)
        {
            Names = names;
            Scores = scores;
            Average = average;
            Diagnostic = diagnostic;
            Critique = critique;
        }

        public IReadOnlyList<string> Names { get; }

        public IReadOnlyList<double> Scores { get; }

        public double Average { get; }

        public EvolutionDiagnostic? Diagnostic { get; }

        /// <summary>Gets the judge's written criticism, or <c>null</c> when it wrote none or it was not asked for.</summary>
        public string? Critique { get; }

        public bool HasScores => Scores.Count > 0;

        public static JudgeOutcome FromScores(
            IReadOnlyList<string> fieldNames,
            IReadOnlyList<double> scores,
            double weight,
            string? critique = null)
        {
            double total = 0;
            var weighted = new double[scores.Count];
            for (int index = 0; index < scores.Count; index++)
            {
                weighted[index] = scores[index] * weight;
                total += weighted[index];
            }

            return new JudgeOutcome(fieldNames, weighted, scores.Count == 0 ? 0 : total / scores.Count, null, critique);
        }

        public static JudgeOutcome Unusable(EvolutionDiagnostic diagnostic) =>
            new(Array.Empty<string>(), Array.Empty<double>(), 0, diagnostic);
    }
}
