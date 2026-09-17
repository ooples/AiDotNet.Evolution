using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>An opt-in, single-use offline policy optimizer with sequestered family-level validation and complete cost accounting.</summary>
/// <remarks>
/// The catalogue contains data, not generated executable code. Task families, normalization, seeds, baselines and gates
/// are frozen before dispatch. A provider timeout stops the entire campaign; its late output cannot select a policy.
/// An uncooperative provider may still run and owns its own process/device containment. Never launch replacement
/// campaigns over such work without a separately coordinated global resource/admission policy.
/// </remarks>
public sealed class EvolutionPolicyOptimizer
{
    private readonly EvolutionPolicySpace _space;
    private readonly EvolutionPolicyOptimizationOptions _options;
    private readonly EvolutionPolicyTrial[] _development, _holdout;
    private readonly EvolutionPolicyBaseline[] _baselines;
    private readonly EvolutionSearchPolicy _stable;
    private readonly EvolutionResourceLedger _ledger;
    private readonly List<EvolutionPolicyTrialRecord> _records = new();
    private readonly int _developmentLimit;
    private readonly string _planHash;
    private int _started, _proposals, _inlineEvidenceBytes;
    private Task? _abandonedWork;
    private string _outcome = "Failed";
    private string? _failureCode;
    private TimeSpan _innerElapsed;

    /// <summary>Freezes a campaign with at least two development families, two held-out families and two distinct manual baselines.</summary>
    public EvolutionPolicyOptimizer(EvolutionPolicySpace space, EvolutionPolicyOptimizationOptions options,
        IEnumerable<EvolutionPolicyTrial> developmentTasks, IEnumerable<EvolutionPolicyTrial> heldOutTasks,
        IEnumerable<EvolutionPolicyBaseline> baselines, EvolutionSearchPolicy stablePreset)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _development = CopyTasks(developmentTasks); _holdout = CopyTasks(heldOutTasks);
        Guard.NotNull(baselines);
        _baselines = baselines.Take(9).ToArray();
        if (_baselines.Length is < 2 or > 8 || _baselines.Any(value => value is null || !space.Contains(value.Policy)) ||
            _baselines.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != _baselines.Length ||
            _baselines.Select(value => value.Policy.Id).Distinct(StringComparer.Ordinal).Count() != _baselines.Length)
            throw new ArgumentException("Declare two to eight distinct in-catalogue manual baselines.", nameof(baselines));
        _baselines = _baselines.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        if (stablePreset is null || !_baselines.Any(value => value.Policy.Id == stablePreset.Id))
            throw new ArgumentException("The stable preset must be a predeclared baseline.", nameof(stablePreset));
        _stable = stablePreset;
        if (_development.Concat(_holdout).Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _development.Length + _holdout.Length ||
            _development.Select(value => value.Family).Intersect(_holdout.Select(value => value.Family), StringComparer.OrdinalIgnoreCase).Any())
            throw new ArgumentException("Development and holdout must have disjoint task identities and semantic families.");
        if (_development.Concat(_holdout).Select(value => value.CostUnitVersionHash).Concat(_baselines.Select(value => value.CostUnitVersionHash))
            .Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("All task and baseline charges must share one declared cost-unit protocol.");
        int developmentPanel = _development.Length * options.Replicates;
        int heldOutReservation = (_baselines.Length + 1) * _holdout.Length * options.Replicates;
        _developmentLimit = Math.Min(options.MaximumDevelopmentPolicies, (options.MaximumInnerTrials - heldOutReservation) / developmentPanel);
        if (_developmentLimit <= _baselines.Length || space.Policies.Count <= _baselines.Length)
            throw new ArgumentException("Reserve enough inner trials for baseline development, a novel policy and the complete held-out panel.");
        _planHash = EvolutionHash.Compute(JsonSerializer.Serialize(new
        {
            Schema = "offline-policy-optimizer-v1",
            Space = space.VersionHash,
            Options = options,
            Development = _development,
            Holdout = _holdout,
            Baselines = _baselines,
            StablePreset = stablePreset.Id
        }, EvolutionJson.Compact));
        var limits = PolicyResources.WithCounters(options.CampaignResources,
            (long)options.MaximumInnerTrials * options.TrialBudget.MaximumEvaluations,
            (long)options.MaximumInnerTrials * options.TrialBudget.MaximumProposals,
            (long)options.MaximumInnerTrials * options.TrialBudget.MaximumRestarts);
        limits = new EvolutionResources(limits.Amounts.Concat(new[] { new KeyValuePair<string, decimal>(PolicyResources.OuterProposals, options.MaximumOuterProposals) }));
        int operations = options.MaximumInnerTrials + options.MaximumOuterProposals;
        _ledger = new EvolutionResourceLedger(_planHash, limits, retainedReceiptLimit: operations, maximumOperations: operations);
    }

    /// <summary>Gets whether an abandoned inner provider still occupies its original work; this is not permission to replace it.</summary>
    public bool HasOutstandingWork => _abandonedWork is { IsCompleted: false };
    /// <summary>Gets the detached report after return, cancellation, or a propagated fatal provider exception.</summary>
    public EvolutionPolicyOptimizationReport? LastReport { get; private set; }

    /// <summary>Runs the campaign once, returning an auditable stable-preset result on recoverable failures or cancellation after admission.</summary>
    /// <remarks>Fatal runtime exceptions propagate with LastReport retained when possible. No result activates a production configuration.</remarks>
    public async Task<EvolutionPolicyOptimizationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw new InvalidOperationException("A policy campaign is single-use; its budget and holdout cannot be reset.");
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        EvolutionSearchPolicy? champion = null;
        EvolutionPolicyBaselineComparison[] comparisons = Array.Empty<EvolutionPolicyBaselineComparison>();
        try
        {
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var baseline in _baselines)
            {
                ChargeProposal();
                scores.Add(baseline.Policy.Id, await DevelopmentScore(baseline.Policy, deadline.Token).ConfigureAwait(false));
            }
            champion = _baselines.OrderByDescending(value => scores[value.Policy.Id]).ThenBy(value => value.Policy.Id, StringComparer.Ordinal).First().Policy;
            double strongestBaseline = scores.Values.Max();
            var random = new StableRandom(_options.Seed);
            while (scores.Count < _developmentLimit && scores.Count < _space.Policies.Count && _proposals < _options.MaximumOuterProposals)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ChargeProposal(); // Duplicate proposals still consume the declared outer-work budget.
                var proposed = random.NextDouble() < 0.5 ? _space.Sample(random) : _space.Mutate(champion, random);
                if (scores.ContainsKey(proposed.Id)) continue;
                double score = await DevelopmentScore(proposed, deadline.Token).ConfigureAwait(false);
                scores.Add(proposed.Id, score);
                if (score > scores[champion.Id] || score == scores[champion.Id] && string.CompareOrdinal(proposed.Id, champion.Id) < 0) champion = proposed;
            }
            if (scores[champion.Id] <= strongestBaseline || scores[champion.Id] - strongestBaseline < _options.MinimumMeanGain)
            {
                _outcome = "NoDevelopmentImprovement";
            }
            else
            {
                // Freeze the champion before any holdout provider is called. Never feed these observations back into selection.
                var panel = new Dictionary<string, List<EvolutionPolicyTrialRecord>>(StringComparer.Ordinal)
                { [champion.Id] = new List<EvolutionPolicyTrialRecord>() };
                foreach (var baseline in _baselines) panel.Add(baseline.Policy.Id, new List<EvolutionPolicyTrialRecord>());
                var policies = new[] { champion }.Concat(_baselines.Select(value => value.Policy)).ToArray();
                foreach (var task in _holdout)
                    for (int replicate = 0; replicate < _options.Replicates; replicate++)
                    {
                        // Rotate order by the matched task/seed block to reduce systematic warm-up/order bias.
                        int offset = replicate % policies.Length;
                        for (int index = 0; index < policies.Length; index++)
                        {
                            var policy = policies[(index + offset) % policies.Length];
                            panel[policy.Id].Add(await RunOne(task, policy, "holdout", replicate, deadline.Token).ConfigureAwait(false));
                        }
                    }
                comparisons = _baselines.Select(baseline => Compare(panel[champion.Id], panel[baseline.Policy.Id], baseline)).ToArray();
                _outcome = comparisons.All(value => value.Passed) ? "GeneralizationPassed" : "GeneralizationRejected";
            }
        }
        catch (Exception error) when (EvolutionExceptionPolicy.IsRecoverable(error))
        {
            _failureCode = error.GetType().Name;
            _outcome = error is PolicyTrialStopped stopped ? stopped.Outcome : error is EvolutionResourceBudgetException ? "BudgetDenied" :
                error is OperationCanceledException ? cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut" : "Failed";
        }
        finally
        {
            clock.Stop();
            LastReport = new EvolutionPolicyOptimizationReport(_planHash, _outcome, _options, _space, _development, _holdout,
                _stable, champion, _baselines, _records.ToArray(), comparisons, _ledger.Snapshot(), clock.Elapsed, _innerElapsed, _failureCode);
        }
        return LastReport!;
    }

    private async Task<double> DevelopmentScore(EvolutionSearchPolicy policy, CancellationToken token)
    {
        var records = new List<EvolutionPolicyTrialRecord>();
        foreach (var task in _development)
            for (int replicate = 0; replicate < _options.Replicates; replicate++)
                records.Add(await RunOne(task, policy, "development", replicate, token).ConfigureAwait(false));
        return records.GroupBy(value => value.Family, StringComparer.OrdinalIgnoreCase).Average(group => group.Average(Utility));
    }

    private void ChargeProposal()
    {
        var amount = EvolutionResources.Of(PolicyResources.OuterProposals, 1);
        using var reservation = _ledger.TryReserve("outer/" + _proposals.ToString(CultureInfo.InvariantCulture), EvolutionResourceStage.Proposal, amount, amount)
            ?? throw new EvolutionResourceBudgetException("outer");
        _proposals++; reservation.Complete(amount);
    }

    private async Task<EvolutionPolicyTrialRecord> RunOne(EvolutionPolicyTrial task, EvolutionSearchPolicy policy,
        string phase, int replicate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_records.Count >= _options.MaximumInnerTrials) throw new PolicyTrialStopped("TrialLimit");
        int sequence = _records.Count;
        ulong seed = ulong.Parse(EvolutionHash.Combine(new[] { _options.Seed.ToString(CultureInfo.InvariantCulture), phase,
            task.Id, task.VersionHash, replicate.ToString(CultureInfo.InvariantCulture) }).Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var maximum = _options.TrialBudget.ReservedResources;
        var stage = phase == "holdout" ? EvolutionResourceStage.Confirmation : EvolutionResourceStage.Evaluation;
        // Deliberately disposed inside the finally below rather than with a using statement, and CodeQL's
        // "missed using opportunity" here is a false positive. Dispose() is not a passive release: it calls
        // ledger.Abandon, which charges an unsettled reservation's maximum as unknown consumption. A using
        // declaration would run that after the finally block, so the trial record would be appended while its
        // ledger operation was still pending and the ledger would settle afterwards -- breaking the
        // Admitted == Settled boundary that coordinated checkpoints rely on. TryReserve may also return null,
        // and that path records a rejection and throws before any using scope would begin.
        var reservation = _ledger.TryReserve("inner/" + sequence.ToString(CultureInfo.InvariantCulture), stage, maximum, maximum);
        if (reservation is null)
        {
            _records.Add(new EvolutionPolicyTrialRecord(sequence, phase, task, policy, replicate, seed, TimeSpan.Zero,
                EvolutionResourceOutcome.Rejected, EvolutionResources.Empty, null, "BudgetDeniedBeforeDispatch"));
            throw new EvolutionResourceBudgetException("inner");
        }
        var clock = Stopwatch.StartNew();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.TrialBudget.Timeout);
        Task<EvolutionPolicyObservation>? work = null;
        EvolutionPolicyObservation? observation = null;
        EvolutionResources charged = maximum;
        EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Unknown;
        string? failure = null;
        try
        {
            work = Task.Run(() => task.RunAsync(policy, _options.TrialBudget, seed, timeout.Token), timeout.Token);
            if (await Task.WhenAny(work, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token)).ConfigureAwait(false) != work)
            {
                _abandonedWork = work;
                throw new PolicyTrialStopped(token.IsCancellationRequested ? "CanceledOrTimedOut" : "InnerTimedOut");
            }
            observation = await work.ConfigureAwait(false) ?? throw new InvalidOperationException("Policy trial returned no receipt.");
            var amounts = observation.ActualResources.Amounts;
            if (!amounts.ContainsKey("cost_units") || new[] { PolicyResources.Evaluations, PolicyResources.Proposals, PolicyResources.Restarts }
                .Any(key => !amounts.TryGetValue(key, out decimal amount) || decimal.Truncate(amount) != amount))
                throw new InvalidDataException("Policy receipt must explicitly report integral engine counters and cost_units.");
            if (amounts.Keys.Any(key => !maximum.Amounts.ContainsKey(key)))
                throw new InvalidDataException("Policy receipt introduced undeclared resources.");
            bool over = amounts.Any(value => value.Value > maximum[value.Key]);
            var returnedOutcome = observation.HasUnknownConsumption ? EvolutionResourceOutcome.Unknown :
                timeout.IsCancellationRequested ? EvolutionResourceOutcome.Canceled :
                observation.IsValid && observation.IsFresh && !over ? EvolutionResourceOutcome.Completed : EvolutionResourceOutcome.Rejected;
            reservation.Complete(observation.ActualResources, returnedOutcome);
            outcome = returnedOutcome; // Do not label an invalid/unsettled receipt completed if reconciliation throws.
            charged = outcome == EvolutionResourceOutcome.Unknown ? maximum : observation.ActualResources;
            _inlineEvidenceBytes += observation.EvidenceJson is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(observation.EvidenceJson);
            if (_inlineEvidenceBytes > _options.MaximumInlineEvidenceBytes)
                throw new PolicyTrialStopped("EvidenceLimit");
            timeout.Token.ThrowIfCancellationRequested(); // Late quality cannot qualify; its known cost still belongs in the ledger.
            // An over-budget receipt is already Rejected above, so the outcome alone decides here.
            if (outcome != EvolutionResourceOutcome.Completed)
                throw new PolicyTrialStopped("InvalidOrOverBudgetTrial");
        }
        catch (Exception error) { failure = error.GetType().Name; throw; }
        finally
        {
            reservation.Dispose();
            clock.Stop(); _innerElapsed += clock.Elapsed;
            _records.Add(new EvolutionPolicyTrialRecord(sequence, phase, task, policy, replicate, seed, clock.Elapsed,
                outcome, charged, observation, failure));
            if (work is { IsCompleted: false } pending)
                _ = pending.ContinueWith(finished => { _ = finished.Exception; timeout.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else { _ = work?.Exception; timeout.Dispose(); }
        }
        return _records[_records.Count - 1];
    }

    private EvolutionPolicyBaselineComparison Compare(List<EvolutionPolicyTrialRecord> candidate,
        List<EvolutionPolicyTrialRecord> baseline, EvolutionPolicyBaseline definition)
    {
        var candidateFamilies = candidate.GroupBy(value => value.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Average(Utility), StringComparer.OrdinalIgnoreCase);
        var gains = baseline.GroupBy(value => value.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EvolutionPolicyFamilyGain(group.Key, candidateFamilies[group.Key], group.Average(Utility)))
            .OrderBy(value => value.Family, StringComparer.Ordinal).ToArray();
        bool stable = candidate.Concat(baseline).GroupBy(value => value.PolicyId + "/" + value.TaskId, StringComparer.Ordinal)
            .All(group => group.Max(Utility) - group.Min(Utility) <= _options.MaximumWithinTaskRange);
        return new EvolutionPolicyBaselineComparison(definition, gains, _options.MaximumPValue / _baselines.Length, _options.MinimumMeanGain, stable);
    }
    private static double Utility(EvolutionPolicyTrialRecord record) => record.Observation?.Utility
        ?? throw new InvalidDataException("A completed policy panel lacks measured utility.");
    private static EvolutionPolicyTrial[] CopyTasks(IEnumerable<EvolutionPolicyTrial> tasks)
    {
        Guard.NotNull(tasks);
        var copy = tasks.Take(65).ToArray();
        if (copy.Length is < 2 or > 64 || copy.Any(value => value is null) ||
            copy.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length ||
            copy.Select(value => value.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            throw new ArgumentException("Declare two to sixty-four unique tasks spanning at least two semantic families.", nameof(tasks));
        return copy.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }
    private sealed class PolicyTrialStopped(string outcome) : InvalidOperationException("The policy campaign stopped without adopting a candidate.")
    { internal string Outcome { get; } = outcome; }
}
