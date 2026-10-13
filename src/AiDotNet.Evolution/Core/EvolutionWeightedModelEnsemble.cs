using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>
/// One model in a weighted ensemble: the proposal source that speaks to it, and the weight with
/// which it is drawn.
/// </summary>
/// <typeparam name="TGenome">The genome type the source proposes.</typeparam>
public sealed class EvolutionModelWeight<TGenome>
{
    /// <summary>Creates a weighted entry.</summary>
    /// <param name="source">The proposal source for this model.</param>
    /// <param name="weight">The draw weight. Strictly positive; weights need not sum to one.</param>
    public EvolutionModelWeight(ICostedEvolutionProposalSource<TGenome> source, double weight)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight), weight, "A model's draw weight must be finite and strictly positive.");
        }

        Weight = weight;
    }

    /// <summary>The proposal source for this model.</summary>
    public ICostedEvolutionProposalSource<TGenome> Source { get; }

    /// <summary>The draw weight.</summary>
    public double Weight { get; }
}

/// <summary>
/// What an ensemble has charged to one model: proposals drawn, and failures it is accountable for.
/// </summary>
public sealed class EvolutionModelAccount
{
    internal EvolutionModelAccount(string modelId, long proposals, long failures)
    {
        ModelId = modelId;
        Proposals = proposals;
        Failures = failures;
    }

    /// <summary>The model's source id.</summary>
    public string ModelId { get; }

    /// <summary>How many proposals were drawn from this model.</summary>
    public long Proposals { get; }

    /// <summary>
    /// How many of those proposals ended in a failure charged to this model.
    /// </summary>
    /// <remarks>
    /// A failure is charged where it happened rather than to the ensemble, so a model that times
    /// out or throws is visible as the cause instead of being averaged into the run's error rate.
    /// </remarks>
    public long Failures { get; }
}

/// <summary>
/// Draws each proposal's model from a weighted set, using the run's own random stream so the choice
/// replays exactly (V1-50, #163).
/// </summary>
/// <typeparam name="TGenome">The genome type.</typeparam>
/// <remarks>
/// <para>
/// This is an <see cref="ICostedEvolutionProposalSource{TGenome}"/> itself, so it composes with the
/// portfolio, the escalation ladder and the resource meter rather than sitting beside them: whatever
/// accepts one source accepts an ensemble of them.
/// </para>
/// <para>
/// The draw uses <see cref="EvolutionVariationContext{TGenome}.Random"/>, the engine's seeded
/// stream, which is what makes replay determinism a property of the engine rather than of this
/// class's own bookkeeping. Nothing here holds a private RNG.
/// </para>
/// <para>
/// <see cref="Observe"/> is routed to the model that produced the proposal, not broadcast: a child
/// that learns from outcomes (a bandit arm, an escalation ladder) must not be taught another
/// model's result.
/// </para>
/// <para>
/// <b>Precedence against the escalation ladder and the bandit portfolio.</b> They decide a
/// different question and therefore nest rather than compete. This ensemble decides <i>which model
/// writes a proposal</i>; <see cref="EscalatingVariationOperator{TGenome}"/> and
/// <see cref="AdaptiveVariationPortfolio{TGenome}"/> decide <i>which operator is asked at all</i>,
/// and both take operators (<c>IVariationOperator</c>) rather than proposal sources. So the stack
/// is always ensemble inside <see cref="ResourceMeteredVariationOperator{TGenome}"/> inside a
/// ladder tier or a portfolio arm — never the reverse, and never two of them choosing the same
/// thing. A ladder that escalates moves to a different tier; the ensemble keeps drawing within
/// whichever tier is asked. A portfolio that shifts its arm weights changes how often this ensemble
/// is asked, not which model it then draws.
/// </para>
/// </remarks>
public sealed class EvolutionWeightedModelEnsemble<TGenome> : ICostedEvolutionProposalSource<TGenome>
{
    private const int MaximumModels = 64;
    private const int MaximumTrackedProposals = 65_536;

    private readonly EvolutionModelWeight<TGenome>[] _models;
    private readonly double[] _cumulative;
    private readonly double _total;
    private readonly long[] _proposals;
    private readonly long[] _failures;

    // Which model produced the proposal for a generation, so Observe reaches the right child. Keyed
    // by generation because that is what an evaluation carries back (EvolutionEvaluation has no
    // proposal identity), which is also how AdaptiveVariationPortfolio routes its arms.
    private readonly Dictionary<long, int> _pending = new();
    private readonly object _gate = new();
    private bool _notifying;

    /// <summary>Creates an ensemble over a weighted model set.</summary>
    /// <param name="id">The ensemble's own source id.</param>
    /// <param name="models">The weighted models. At least one, at most 64, ids distinct.</param>
    public EvolutionWeightedModelEnsemble(string id, IReadOnlyList<EvolutionModelWeight<TGenome>> models)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An ensemble needs an id.", nameof(id));
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (models.Count == 0)
        {
            throw new ArgumentException(
                "A weighted ensemble needs at least one model; an empty set would have nothing to draw.",
                nameof(models));
        }

        if (models.Count > MaximumModels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(models), models.Count, $"At most {MaximumModels} models may share one ensemble.");
        }

        _models = new EvolutionModelWeight<TGenome>[models.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i] ?? throw new ArgumentException("A model entry was null.", nameof(models));
            if (!seen.Add(model.Source.Id))
            {
                throw new ArgumentException(
                    $"Two models share the source id '{model.Source.Id}'; per-model accounting could not tell them apart.",
                    nameof(models));
            }

            _models[i] = model;
        }

        Id = id;
        _cumulative = new double[_models.Length];
        double running = 0.0;
        for (int i = 0; i < _models.Length; i++)
        {
            running += _models[i].Weight;
            _cumulative[i] = running;
        }

        if (double.IsInfinity(running) || double.IsNaN(running))
        {
            // Two weights of double.MaxValue sum to infinity, and Draw would then compare an
            // infinite point against finite cumulative bounds and pick the last model every time -
            // silently ignoring the mixture it was configured with. Refuse the configuration.
            throw new ArgumentOutOfRangeException(
                nameof(models), running,
                "The models' weights sum to a value that is not finite, so no draw could follow them.");
        }

        _total = running;
        _proposals = new long[_models.Length];
        _failures = new long[_models.Length];

        // The weights are part of the identity: changing one changes which proposals a replay draws,
        // so a stored run must not be resumed against a different mixture under the same hash.
        var parts = new List<string> { "weighted-model-ensemble-v1", id };
        foreach (var model in _models)
        {
            parts.Add(model.Source.Id);
            parts.Add(model.Source.VersionHash);
            parts.Add(model.Weight.ToString("R", CultureInfo.InvariantCulture));
        }

        VersionHash = EvolutionHash.Combine(parts);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string VersionHash { get; }

    /// <summary>What the ensemble has charged to each model, in the configured order.</summary>
    public IReadOnlyList<EvolutionModelAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                var accounts = new EvolutionModelAccount[_models.Length];
                for (int i = 0; i < _models.Length; i++)
                {
                    accounts[i] = new EvolutionModelAccount(_models[i].Source.Id, _proposals[i], _failures[i]);
                }

                return accounts;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<EvolutionResourceResult<TGenome>> ProposeAsync(
        EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        int index = Draw(context.Random);
        var model = _models[index];

        // Reserve the route BEFORE the child works. Reserving afterwards meant a child could finish
        // successfully and then have its receipt discarded by the routing cap, which also charged
        // the model a failure it did not cause.
        Reserve(context.Generation, index);
        lock (_gate)
        {
            _proposals[index]++;
        }

        try
        {
            var result = await model.Source.ProposeAsync(context, cancellationToken).ConfigureAwait(false);

            // A null result breaks the contract this interface declares, and it is also a failure of
            // the model that produced it, so it is charged and refused rather than passed upward to
            // fail somewhere that cannot name the model.
            if (result is null)
            {
                // Thrown, not charged here: the catch below is the single place a failure is counted
                // and the route released, so charging in both made one null result two failures.
                throw new InvalidOperationException(
                    "Model '" + model.Source.Id + "' returned no proposal result.");
            }

            // A child may report failure without throwing. The story charges a model that "fails or
            // times out", which is about the outcome, not the mechanism.
            if (result.Outcome == EvolutionResourceOutcome.Failed)
            {
                lock (_gate)
                {
                    _failures[index]++;
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            // A cancellation is the run stopping, not this model failing. Charging it would blame a
            // model for the operator's decision. The route is released so the generation can be
            // proposed again after the cancellation.
            Release(context.Generation);
            throw;
        }
        catch
        {
            lock (_gate)
            {
                _failures[index]++;
                _pending.Remove(context.Generation);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        if (evaluation is null) throw new ArgumentNullException(nameof(evaluation));

        // The child is told INSIDE the gate that drops the route, so a concurrent CaptureState
        // cannot catch a moment where the route is gone and the child has not learned - that
        // checkpoint would lose the outcome entirely. The reentrancy guard turns a child that calls
        // back into this ensemble into an error rather than a deadlock.
        lock (_gate)
        {
            if (_notifying)
            {
                throw new InvalidOperationException(
                    "A model observed an outcome while the ensemble was already delivering one.");
            }

            // An unknown or repeated outcome is refused rather than ignored: teaching the wrong
            // child another model's result corrupts its learning silently, and broadcasting to all
            // of them is worse. AdaptiveVariationPortfolio refuses the same way.
            if (!_pending.TryGetValue(evaluation.Lineage.Generation, out int index))
            {
                throw new InvalidOperationException(
                    "The weighted model ensemble received an unknown or repeated outcome.");
            }

            _notifying = true;
            try
            {
                _models[index].Source.Observe(evaluation, insertionResult);
                _pending.Remove(evaluation.Lineage.Generation);
            }
            finally
            {
                _notifying = false;
            }
        }
    }

    /// <inheritdoc/>
    public string CaptureState()
    {
        lock (_gate)
        {
            var children = new string[_models.Length];
            for (int i = 0; i < _models.Length; i++) children[i] = _models[i].Source.CaptureState();

            var document = new EnsembleState
            {
                VersionHash = VersionHash,
                Proposals = (long[])_proposals.Clone(),
                Failures = (long[])_failures.Clone(),
                Pending = _pending.OrderBy(pair => pair.Key).Select(pair => new PendingEntry
                {
                    Generation = pair.Key,
                    Model = pair.Value,
                }).ToArray(),
                Children = children,
            };

            return JsonSerializer.Serialize(document, EnsembleStateJson.Options);
        }
    }

    /// <inheritdoc/>
    public void RestoreState(string state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var document = JsonSerializer.Deserialize<EnsembleState>(state, EnsembleStateJson.Options)
            ?? throw new InvalidOperationException("The ensemble state did not deserialize.");

        if (!string.Equals(document.VersionHash, VersionHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ensemble state was captured from a different model mixture, so resuming it would "
                + "replay draws that this ensemble cannot reproduce.");
        }

        if (document.Children is null || document.Children.Length != _models.Length
            || document.Proposals is null || document.Proposals.Length != _models.Length
            || document.Failures is null || document.Failures.Length != _models.Length)
        {
            throw new InvalidOperationException("The ensemble state does not match this ensemble's shape.");
        }

        for (int i = 0; i < _models.Length; i++) _models[i].Source.RestoreState(document.Children[i]);

        lock (_gate)
        {
            Array.Copy(document.Proposals, _proposals, _models.Length);
            Array.Copy(document.Failures, _failures, _models.Length);
            if (document.Pending is null)
            {
                throw new InvalidOperationException(
                    "The ensemble state has no pending table; an omitted table is not the same claim as "
                    + "an empty one, and accepting it would silently discard every unrouted proposal.");
            }

            if (document.Pending.Length > MaximumTrackedProposals)
            {
                throw new InvalidOperationException(
                    "The ensemble state carries " + document.Pending.Length.ToString(CultureInfo.InvariantCulture)
                    + " pending proposals, beyond the " + MaximumTrackedProposals.ToString(CultureInfo.InvariantCulture)
                    + " this ensemble will route.");
            }

            _pending.Clear();
            foreach (var entry in document.Pending)
            {
                if (entry.Model < 0 || entry.Model >= _models.Length)
                {
                    throw new InvalidOperationException("The ensemble state references a model outside its set.");
                }

                _pending[entry.Generation] = entry.Model;
            }
        }
    }

    private int Draw(StableRandom random)
    {
        if (random is null) throw new ArgumentNullException(nameof(random));
        if (_models.Length == 1) return 0;

        // One draw per proposal from the engine's stream, so the sequence is a function of the run's
        // seed alone. NextDouble is in [0, 1); scaling by the weight total keeps the comparison in
        // the weights' own units rather than normalizing them into floating-point drift.
        double point = random.NextDouble() * _total;
        for (int i = 0; i < _cumulative.Length; i++)
        {
            if (point < _cumulative[i]) return i;
        }

        return _cumulative.Length - 1;
    }

    private void Reserve(long generation, int index)
    {
        lock (_gate)
        {
            // A generation already awaiting an outcome must not be re-proposed: overwriting the
            // route would send the first proposal's outcome to the second model and refuse the
            // second's outcome as unknown.
            if (_pending.ContainsKey(generation))
            {
                throw new InvalidOperationException(
                    "Generation " + generation.ToString(CultureInfo.InvariantCulture)
                    + " already has a proposal awaiting an outcome.");
            }

            if (_pending.Count >= MaximumTrackedProposals)
            {
                throw new InvalidOperationException(
                    "More than " + MaximumTrackedProposals.ToString(CultureInfo.InvariantCulture)
                    + " proposals are awaiting an outcome; the ensemble will not grow its routing "
                    + "table without bound.");
            }

            _pending[generation] = index;
        }
    }

    private void Release(long generation)
    {
        lock (_gate)
        {
            _pending.Remove(generation);
        }
    }

    private sealed class EnsembleState
    {
        public string? VersionHash { get; set; }
        public long[]? Proposals { get; set; }
        public long[]? Failures { get; set; }
        public PendingEntry[]? Pending { get; set; }
        public string[]? Children { get; set; }
    }

    private sealed class PendingEntry
    {
        public long Generation { get; set; }
        public int Model { get; set; }
    }

    private static class EnsembleStateJson
    {
        internal static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    }
}
