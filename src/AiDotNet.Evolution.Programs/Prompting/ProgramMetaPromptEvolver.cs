using System.Globalization;
using System.Text;
using AiDotNet.Evolution;
using Newtonsoft.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Evolves the system instructions alongside the programs: candidate instructions are proposed from
/// the ones that worked, scored by the downstream quality of the programs written under them, and
/// kept in their own archive with lineage (V1-53, #166).
/// </summary>
/// <remarks>
/// <para>
/// <b>What scores an instruction.</b> Only the programs it produced. <see cref="Observe"/> credits
/// the instruction that wrote a proposal with that proposal's measured quality, so an instruction
/// rises because the search did better under it. Nothing the model says about its own instructions
/// enters the score.
/// </para>
/// <para>
/// <b>Replay.</b> Selection draws from the stream the caller passes in, which is the engine's seeded
/// stream, so a replay with the same seed selects the same instructions in the same order. The
/// evolver holds no private RNG.
/// </para>
/// <para>
/// <b>Cost.</b> Each instruction proposal is one model call, charged to the run's ledger when one is
/// supplied and reported separately through <see cref="ProposalCalls"/>,
/// <see cref="ProposalCostUnits"/> and <see cref="WriteCostDescriptors"/>. The meta level is a
/// second consumer of the same budget, so it has to be visible as its own line rather than folded
/// into the program proposals.
/// </para>
/// <para>
/// Nothing is contacted unless <see cref="ProgramMetaPromptOptions.Enabled"/> is set and
/// <see cref="ProposeAsync"/> is called.
/// </para>
/// </remarks>
public sealed class ProgramMetaPromptEvolver
{
    // Bumped when the checkpoint shape changes, so an older state is refused rather than misread.
    private const int StateSchemaVersion = 1;

    private const int MaximumPendingRoutes = 4_096;

    private readonly IProgramChatClient _chatClient;
    private readonly ProgramMetaPromptOptions _options;
    private readonly List<ProgramInstructionCandidate> _archive = new();
    private readonly Dictionary<long, string> _routes = new();
    private readonly object _gate = new();
    private readonly string _seedId;

    private long _proposalCalls;
    private decimal _proposalCostUnits;
    private long _refusedProposals;

    /// <summary>Creates an evolver seeded with the instruction the run starts from.</summary>
    /// <param name="chatClient">The caller-owned provider asked for new instructions.</param>
    /// <param name="seedInstruction">The run's starting instruction; it is never dropped from the archive.</param>
    /// <param name="options">Settings; defaults leave the feature off.</param>
    public ProgramMetaPromptEvolver(
        IProgramChatClient chatClient, string seedInstruction, ProgramMetaPromptOptions? options = null)
    {
        ProgramGuard.NotNull(chatClient);
        ProgramGuard.NotNullOrWhiteSpace(seedInstruction);

        ProgramMetaPromptOptions copy = (options ?? new ProgramMetaPromptOptions()).Clone();
        copy.Validate();
        string seed = seedInstruction.Trim();
        if (seed.Length > copy.MaxInstructionChars)
        {
            throw new ArgumentException(
                "The seed instruction is longer than MaxInstructionChars, so the evolver could never propose a "
                + "replacement it would accept.", nameof(seedInstruction));
        }

        _chatClient = chatClient;
        _options = copy;
        _seedId = Identify(seed);
        _archive.Add(new ProgramInstructionCandidate(_seedId, seed, parentId: null, generation: 0));
    }

    /// <summary>The archive, best mean quality first, with unscored instructions last.</summary>
    public IReadOnlyList<ProgramInstructionCandidate> Archive
    {
        get
        {
            lock (_gate)
            {
                return _archive
                    .OrderByDescending(candidate => candidate.Scored == 0 ? double.MinValue : candidate.MeanQuality)
                    .ThenBy(candidate => candidate.Generation)
                    .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>How many instruction proposals have been asked of the model.</summary>
    public long ProposalCalls => Interlocked.Read(ref _proposalCalls);

    /// <summary>The cost units the meta level has charged, separately from the program proposals.</summary>
    public decimal ProposalCostUnits
    {
        get
        {
            lock (_gate)
            {
                return _proposalCostUnits;
            }
        }
    }

    /// <summary>How many model answers were refused as unusable instructions.</summary>
    /// <remarks>
    /// A refusal still costs a call, and is reported, because a run whose meta level is paying for
    /// answers it cannot use should be able to see that rather than infer it from a flat archive.
    /// </remarks>
    public long RefusedProposals => Interlocked.Read(ref _refusedProposals);

    /// <summary>Picks the instruction for <paramref name="generation"/>'s proposal and records the route.</summary>
    /// <remarks>
    /// The route is what lets <see cref="Observe"/> credit the right instruction. A generation that
    /// already has a route is refused: overwriting it would credit the second instruction with the
    /// first one's result.
    /// </remarks>
    public string Select(StableRandom random, long generation)
    {
        ProgramGuard.NotNull(random);

        lock (_gate)
        {
            if (_routes.ContainsKey(generation))
            {
                throw new InvalidOperationException(
                    "Generation " + generation.ToString(CultureInfo.InvariantCulture)
                    + " already has an instruction awaiting its outcome.");
            }

            if (_routes.Count >= MaximumPendingRoutes)
            {
                throw new InvalidOperationException(
                    "More than " + MaximumPendingRoutes.ToString(CultureInfo.InvariantCulture)
                    + " proposals are awaiting an outcome; the evolver will not grow its routing table without bound.");
            }

            ProgramInstructionCandidate chosen = Choose(random);
            chosen.Uses++;
            _routes[generation] = chosen.Id;
            return chosen.Text;
        }
    }

    /// <summary>Credits the instruction that wrote <paramref name="generation"/>'s proposal with its quality.</summary>
    /// <remarks>
    /// An unknown or repeated generation is refused rather than ignored: crediting the wrong
    /// instruction silently inverts the signal the archive is built on.
    /// </remarks>
    public void Observe(long generation, double quality)
    {
        if (double.IsNaN(quality) || double.IsInfinity(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality,
                "A non-finite quality would make every mean non-finite and the archive unorderable.");
        }

        lock (_gate)
        {
            if (!_routes.TryGetValue(generation, out string? id))
            {
                throw new InvalidOperationException(
                    "The meta-prompt evolver received an unknown or repeated outcome for generation "
                    + generation.ToString(CultureInfo.InvariantCulture) + ".");
            }

            _routes.Remove(generation);
            ProgramInstructionCandidate? candidate = _archive.FirstOrDefault(entry => entry.Id == id);
            if (candidate is null)
            {
                // The archive dropped the instruction while its proposal was still outstanding. The
                // credit has nowhere to go, and inventing a home for it would be a fabricated score.
                return;
            }

            candidate.Scored++;
            candidate.TotalQuality += quality;
        }
    }

    /// <summary>Whether <paramref name="generation"/> is one where an instruction proposal is due.</summary>
    public bool ShouldPropose(long generation) =>
        _options.Enabled && generation > 0 && generation % _options.ProposeEveryGenerations == 0;

    /// <summary>
    /// Asks the model for a new instruction, derived from the archive's best, and adds it if it is
    /// usable. Returns the new instruction, or null if the answer was refused.
    /// </summary>
    /// <param name="generation">The generation the new instruction enters the archive at.</param>
    /// <param name="ledger">The run's ledger; the call is charged to it when one is supplied.</param>
    /// <param name="cancellationToken">Cancellation, honoured by the provider.</param>
    public async Task<string?> ProposeAsync(
        long generation, EvolutionResourceLedger? ledger = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new InvalidOperationException("Meta-prompt evolution is not enabled.");
        cancellationToken.ThrowIfCancellationRequested();

        string operation = "meta-prompt/" + generation.ToString(CultureInfo.InvariantCulture);
        EvolutionResources charge = EvolutionResources.Of("cost_units", _options.ProposalCostUnits);
        using EvolutionResourceReservation? reservation = ledger?.TryReserve(
            operation, EvolutionResourceStage.Proposal, charge, charge);
        if (ledger is not null && reservation is null) throw new EvolutionResourceBudgetException(operation);

        Interlocked.Increment(ref _proposalCalls);
        lock (_gate)
        {
            _proposalCostUnits += _options.ProposalCostUnits;
        }

        ProgramChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(
                BuildRequest(), new ProgramChatOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The run stopping is not a refused answer. The charge stands, because the call was made.
            reservation?.Complete(charge, EvolutionResourceOutcome.Canceled);
            throw;
        }
        catch
        {
            reservation?.Complete(charge, EvolutionResourceOutcome.Failed);
            Interlocked.Increment(ref _refusedProposals);
            throw;
        }

        reservation?.Complete(charge, EvolutionResourceOutcome.Completed);
        string? accepted = Accept(response?.Text, generation);
        if (accepted is null) Interlocked.Increment(ref _refusedProposals);
        return accepted;
    }

    /// <summary>Writes the meta level's own cost into an evaluation's descriptors, under its prefix.</summary>
    /// <remarks>
    /// The run's total cost already includes these units; this is what makes the meta level's share
    /// recoverable rather than inferred, which is what #166's third criterion asks for.
    /// </remarks>
    public void WriteCostDescriptors(IDictionary<string, double> descriptors)
    {
        ProgramGuard.NotNull(descriptors);

        lock (_gate)
        {
            descriptors[_options.MetricPrefix + "calls"] = _proposalCalls;
            descriptors[_options.MetricPrefix + "cost_units"] = (double)_proposalCostUnits;
            descriptors[_options.MetricPrefix + "refused"] = _refusedProposals;
            descriptors[_options.MetricPrefix + "archive_size"] = _archive.Count;
        }
    }

    /// <summary>A hash of the archive, the routes and the counters, for checkpoint equality.</summary>
    public string StateHash
    {
        get
        {
            lock (_gate)
            {
                var parts = new List<string>
                {
                    StateSchemaVersion.ToString(CultureInfo.InvariantCulture),
                    _options.Identity(),
                    _seedId,
                    _proposalCalls.ToString(CultureInfo.InvariantCulture),
                    // Normalized: a decimal round-trips through JSON with a different scale (3 comes
                    // back as 3.0), and two states that charged the same amount must hash the same.
                    _proposalCostUnits.ToString("F6", CultureInfo.InvariantCulture),
                    _refusedProposals.ToString(CultureInfo.InvariantCulture),
                };
                parts.AddRange(_archive.Select(candidate => candidate.Identity()));
                parts.AddRange(_routes
                    .OrderBy(route => route.Key)
                    .Select(route => route.Key.ToString(CultureInfo.InvariantCulture) + "=" + route.Value));
                return EvolutionHash.Combine(parts);
            }
        }
    }

    /// <summary>Captures the archive, the lineage, the outstanding routes and the counters.</summary>
    public string CaptureState()
    {
        lock (_gate)
        {
            return JsonConvert.SerializeObject(new State
            {
                SchemaVersion = StateSchemaVersion,
                OptionsIdentity = _options.Identity(),
                SeedId = _seedId,
                ProposalCalls = _proposalCalls,
                ProposalCostUnits = _proposalCostUnits,
                RefusedProposals = _refusedProposals,
                Archive = _archive.Select(candidate => new Entry
                {
                    Id = candidate.Id,
                    Text = candidate.Text,
                    ParentId = candidate.ParentId,
                    Generation = candidate.Generation,
                    Uses = candidate.Uses,
                    Scored = candidate.Scored,
                    TotalQuality = candidate.TotalQuality,
                }).ToList(),
                Routes = _routes.OrderBy(route => route.Key)
                    .Select(route => new Route { Generation = route.Key, InstructionId = route.Value }).ToList(),
            });
        }
    }

    /// <summary>Restores a captured state exactly, or refuses it.</summary>
    /// <remarks>
    /// A state from different settings, a different seed instruction or a newer schema is refused:
    /// restoring it would replay a selection sequence the archive never produced. An absent archive
    /// is refused too - an omitted list is not the same claim as an empty one, and the seed
    /// instruction is never absent from a state this evolver wrote.
    /// </remarks>
    public void RestoreState(string state)
    {
        ProgramGuard.NotNullOrWhiteSpace(state);

        State? document;
        try
        {
            document = JsonConvert.DeserializeObject<State>(state);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The meta-prompt state is not readable.", exception);
        }

        if (document is null) throw new InvalidOperationException("The meta-prompt state is empty.");
        if (document.SchemaVersion != StateSchemaVersion)
            throw new InvalidOperationException("The meta-prompt state has an unsupported schema version.");
        if (!string.Equals(document.OptionsIdentity, _options.Identity(), StringComparison.Ordinal))
            throw new InvalidOperationException("The meta-prompt state was captured under different settings.");
        if (!string.Equals(document.SeedId, _seedId, StringComparison.Ordinal))
            throw new InvalidOperationException("The meta-prompt state was captured from a different seed instruction.");
        if (document.Archive is null || document.Archive.Count == 0)
            throw new InvalidOperationException("The meta-prompt state carries no archive.");
        if (document.Archive.Count > _options.MaxArchiveSize)
            throw new InvalidOperationException("The meta-prompt state carries more instructions than the archive holds.");
        if (document.Routes is { Count: > MaximumPendingRoutes })
            throw new InvalidOperationException("The meta-prompt state carries more routes than the evolver will track.");

        if (document.ProposalCalls < 0 || document.RefusedProposals < 0 || document.ProposalCostUnits < 0m)
            throw new InvalidOperationException("The restored proposal counters are negative.");

        // Validate into local collections and only then replace the live ones. Clearing first meant
        // a state that failed validation half way left the evolver holding a partial archive with
        // the old counters, and a caller that caught the exception could then select or credit
        // against that.
        var archive = new List<ProgramInstructionCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry entry in document.Archive)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Text))
                throw new InvalidOperationException("A restored instruction has no id or no text.");

            // The id is derived from the text, so a mismatch is a rewritten checkpoint rather than
            // one this evolver wrote, and a duplicate id would make Observe credit whichever entry
            // came first regardless of which one Select used.
            if (!string.Equals(entry.Id, Identify(entry.Text), StringComparison.Ordinal))
                throw new InvalidOperationException("A restored instruction's id does not match its text.");
            if (!ids.Add(entry.Id))
                throw new InvalidOperationException("A restored instruction appears twice.");

            // The length bound is the one the constructor applies to the seed. The 16-character
            // minimum that Accept applies to a model's answer is deliberately NOT applied here: a
            // legitimately short seed instruction must still restore.
            if (entry.Text.Length > _options.MaxInstructionChars)
                throw new InvalidOperationException("A restored instruction is longer than MaxInstructionChars.");
            if (entry.Uses < 0 || entry.Scored < 0 || entry.Scored > entry.Uses)
                throw new InvalidOperationException("A restored instruction's use and score counts are inconsistent.");
            if (double.IsNaN(entry.TotalQuality) || double.IsInfinity(entry.TotalQuality))
                throw new InvalidOperationException("A restored instruction's total quality is not finite.");

            archive.Add(new ProgramInstructionCandidate(entry.Id, entry.Text, entry.ParentId, entry.Generation)
            {
                Uses = entry.Uses,
                Scored = entry.Scored,
                TotalQuality = entry.TotalQuality,
            });
        }

        if (!ids.Contains(_seedId))
            throw new InvalidOperationException("The restored archive does not contain the seed instruction.");

        var routes = new Dictionary<long, string>();
        foreach (Route route in document.Routes ?? new List<Route>())
        {
            // A route naming an instruction the archive does not hold would have its outcome
            // dropped on the floor, and two routes for one generation would lose the first.
            if (string.IsNullOrWhiteSpace(route.InstructionId))
                throw new InvalidOperationException("A restored route names no instruction.");
            if (!ids.Contains(route.InstructionId))
                throw new InvalidOperationException("A restored route names an instruction the archive does not hold.");
            if (routes.ContainsKey(route.Generation))
                throw new InvalidOperationException("A restored generation has two routes.");

            routes[route.Generation] = route.InstructionId;
        }

        lock (_gate)
        {
            _archive.Clear();
            _archive.AddRange(archive);
            _routes.Clear();
            foreach (KeyValuePair<long, string> route in routes) _routes.Add(route.Key, route.Value);
            _proposalCalls = document.ProposalCalls;
            _proposalCostUnits = document.ProposalCostUnits;
            _refusedProposals = document.RefusedProposals;
        }
    }

    private ProgramInstructionCandidate Choose(StableRandom random)
    {
        if (_archive.Count == 1) return _archive[0];

        if (_options.Selection == ProgramInstructionSelection.Uniform)
            return _archive[random.NextInt(_archive.Count)];

        // An unscored instruction is unexplored, not bad: a freshly proposed instruction is tried
        // before any scored one, so an incumbent that leads early cannot starve it.
        List<ProgramInstructionCandidate> unscored = _archive.Where(candidate => candidate.Scored == 0).ToList();
        if (unscored.Count > 0) return unscored[random.NextInt(unscored.Count)];

        if (_options.Selection == ProgramInstructionSelection.EpsilonGreedy &&
            random.NextDouble() < _options.ExplorationProbability)
        {
            return _archive[random.NextInt(_archive.Count)];
        }

        ProgramInstructionCandidate best = _archive[0];
        foreach (ProgramInstructionCandidate candidate in _archive)
        {
            if (candidate.MeanQuality > best.MeanQuality) best = candidate;
        }

        return best;
    }

    private IReadOnlyList<ProgramChatMessage> BuildRequest()
    {
        var text = new StringBuilder();
        text.Append("You are improving the standing instructions given to a program-improvement model. ");
        text.Append("Answer with the replacement instructions only: no preamble, no code, no explanation.")
            .Append('\n').Append('\n');
        text.Append("Each instruction below is followed by the mean quality of the programs written under it, ")
            .Append("on this run's own primary metric, and by how many were scored.").Append('\n');

        foreach (ProgramInstructionCandidate candidate in Archive.Take(4))
        {
            text.Append('\n').Append("--- mean quality ")
                .Append(candidate.Scored == 0
                    ? "not yet scored"
                    : candidate.MeanQuality.ToString("G6", CultureInfo.InvariantCulture))
                .Append(", scored ").Append(candidate.Scored.ToString(CultureInfo.InvariantCulture))
                .Append(" ---").Append('\n').Append(candidate.Text).Append('\n');
        }

        text.Append('\n').Append("Write instructions that would do better than the best of these. Keep them under ")
            .Append(_options.MaxInstructionChars.ToString(CultureInfo.InvariantCulture)).Append(" characters.");

        return new[]
        {
            ProgramChatMessage.System("You write concise standing instructions for a code-improvement model."),
            ProgramChatMessage.User(text.ToString()),
        };
    }

    private string? Accept(string? answer, long generation)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;

        string text = answer.Trim();
        // A model asked for instructions sometimes answers with a program. Refusing an over-long
        // answer keeps one bad reply from becoming the prompt for the rest of the run.
        if (text.Length > _options.MaxInstructionChars || text.Length < 16) return null;

        string id = Identify(text);
        lock (_gate)
        {
            // The same text is the same candidate: re-adding it would split one instruction's score
            // across two entries and make both look worse than the instruction actually is.
            if (_archive.Any(candidate => candidate.Id == id)) return null;

            string parentId = Archive[0].Id;
            if (_archive.Count >= _options.MaxArchiveSize && !Evict()) return null;
            _archive.Add(new ProgramInstructionCandidate(id, text, parentId, generation));
            return text;
        }
    }

    private bool Evict()
    {
        // The seed instruction is never dropped, so a run can always fall back to what it started
        // from. An unscored instruction is not dropped either: it has not had its chance yet.
        // Nor is one that still owes an outcome: Select has already routed a generation to it, and
        // dropping it would make Observe find no candidate and discard that generation's credit in
        // silence. With nothing else eligible, Accept refuses the proposal instead.
        ProgramInstructionCandidate? worst = _archive
            .Where(candidate => candidate.Id != _seedId && candidate.Scored > 0
                && !_routes.ContainsValue(candidate.Id))
            .OrderBy(candidate => candidate.MeanQuality)
            .ThenBy(candidate => candidate.Generation)
            .FirstOrDefault();
        if (worst is null) return false;
        _archive.Remove(worst);
        return true;
    }

    private static string Identify(string text) => EvolutionHash.Compute(text);

    private sealed class State
    {
        public int SchemaVersion { get; set; }
        public string? OptionsIdentity { get; set; }
        public string? SeedId { get; set; }
        public long ProposalCalls { get; set; }
        public decimal ProposalCostUnits { get; set; }
        public long RefusedProposals { get; set; }
        public List<Entry>? Archive { get; set; }
        public List<Route>? Routes { get; set; }
    }

    private sealed class Entry
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public string? ParentId { get; set; }
        public long Generation { get; set; }
        public long Uses { get; set; }
        public long Scored { get; set; }
        public double TotalQuality { get; set; }
    }

    private sealed class Route
    {
        public long Generation { get; set; }
        public string? InstructionId { get; set; }
    }
}
