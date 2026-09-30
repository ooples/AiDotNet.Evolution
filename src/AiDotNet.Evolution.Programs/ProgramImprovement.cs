using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>Asks a verifier to test one exact artifact.</summary>
/// <param name="Nonce">A fresh value the receipt must echo, so a receipt cannot be replayed for another request.</param>
/// <param name="Artifact">The artifact to test.</param>
public sealed record VerificationRequest(string Nonce, ProgramArtifact Artifact);
/// <summary>Search feedback must contain only public test diagnostics. Held-out feedback is never sent to a proposer.</summary>
public sealed record VerificationReceipt(string Nonce, string ArtifactFingerprint, string OracleIdentity,
    bool Correct, double Duration, string Feedback);

/// <summary>Tests an artifact and reports what the test cost.</summary>
/// <param name="request">The artifact and the nonce to echo.</param>
/// <param name="cancellationToken">Cancels the test.</param>
/// <returns>The receipt, with a positive <see cref="ProgramImprovement.CostResource"/> charge.</returns>
public delegate ValueTask<EvolutionResourceResult<VerificationReceipt>> ProgramVerifier(
    VerificationRequest request, CancellationToken cancellationToken);

/// <summary>Proposes a patch plan, usually by asking a model, and reports what it cost.</summary>
/// <param name="request">The parent, targets and prior feedback.</param>
/// <param name="cancellationToken">Cancels the proposal.</param>
/// <returns>The plan, with a positive <see cref="ProgramImprovement.CostResource"/> charge.</returns>
public delegate ValueTask<EvolutionResourceResult<PatchPlan>> ProgramPlanner(
    SearchRequest request, CancellationToken cancellationToken);

/// <summary>Configures one <see cref="ProgramImprovement.RunAsync"/> session. Costs are in <see cref="ProgramImprovement.CostResource"/> units.</summary>
/// <param name="RunId">Names the run's audit folder: 1-64 ASCII letters, digits, <c>-</c> or <c>_</c>. An existing run is refused.</param>
/// <param name="MeasuredBottleneck">What measurement says limits the program, shown to the planner.</param>
/// <param name="OracleIdentity">The identity every verification receipt must carry.</param>
/// <param name="AuditDirectory">Where the run's audit folder is created.</param>
/// <param name="MaxRepairs">Repair attempts after the first proposal, 0 to 7.</param>
/// <param name="SetupCost">The charge for creating the compiler.</param>
/// <param name="BuildCost">The charge for each build.</param>
/// <param name="PatchCost">The charge for cataloguing targets and for applying each plan.</param>
/// <param name="AuditCost">The charge for writing each audit record.</param>
/// <param name="ModelMaximum">The most one planner call may cost.</param>
/// <param name="VerificationMaximum">The most one verification may cost.</param>
/// <param name="MinimumSpeedup">How many times faster than the incumbent the candidate must run on held-out tests, 1 to 1000.</param>
public sealed record ImprovementOptions(string RunId, string MeasuredBottleneck, string OracleIdentity,
    string AuditDirectory, int MaxRepairs = 2, decimal SetupCost = 1, decimal BuildCost = 1,
    decimal PatchCost = 1, decimal AuditCost = 1, decimal ModelMaximum = 10,
    decimal VerificationMaximum = 10, double MinimumSpeedup = 1.0);

/// <summary>The outcome of an improvement session.</summary>
/// <param name="Promoted">Whether the candidate passed held-out tests and beat the incumbent by the required speedup.</param>
/// <param name="Incumbent">The compiled starting program.</param>
/// <param name="Candidate">The last candidate that compiled and passed public tests, or <c>null</c>.</param>
/// <param name="Attempts">How many proposals were made.</param>
/// <param name="Reason">Why it was or was not promoted.</param>
/// <param name="BaselineConfirmation">The incumbent's held-out receipt, when a candidate reached that stage.</param>
/// <param name="CandidateConfirmation">The candidate's held-out receipt, when it reached that stage.</param>
public sealed record ImprovementResult(bool Promoted, ProgramArtifact Incumbent, ProgramArtifact? Candidate,
    int Attempts, string Reason, VerificationReceipt? BaselineConfirmation, VerificationReceipt? CandidateConfirmation);

/// <summary>One bounded proposal/repair session and one sealed final comparison. Not an OS execution sandbox.</summary>
public static class ProgramImprovement
{
    /// <summary>The resource name every charge in a session is made in.</summary>
    public const string CostResource = "program_work_units";

    /// <summary>Runs one proposal and repair session, then one sealed held-out comparison.</summary>
    /// <param name="parent">The program to improve. It must compile and pass public tests.</param>
    /// <param name="compilerFactory">Creates the compiler.</param>
    /// <param name="planner">Proposes patch plans.</param>
    /// <param name="searchVerifier">Runs public tests, whose feedback may reach the planner.</param>
    /// <param name="heldOutVerifier">Runs held-out tests; their feedback never reaches the planner.</param>
    /// <param name="ledger">The ledger every stage is charged to.</param>
    /// <param name="options">Costs, limits and the audit location.</param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <returns>Whether a candidate was promoted, with its evidence.</returns>
    /// <exception cref="IOException">The run's audit folder already exists.</exception>
    /// <exception cref="InvalidOperationException">The parent does not compile or fails public tests.</exception>
    /// <exception cref="InvalidDataException">A planner, compiler or verifier returned unbounded or mismatched evidence.</exception>
    public static async Task<ImprovementResult> RunAsync(ProgramSnapshot parent, Func<IProgramCompiler> compilerFactory,
        ProgramPlanner planner, ProgramVerifier searchVerifier, ProgramVerifier heldOutVerifier,
        EvolutionResourceLedger ledger, ImprovementOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(compilerFactory);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(searchVerifier);
        ArgumentNullException.ThrowIfNull(heldOutVerifier);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        string directory = Path.Combine(Path.GetFullPath(options.AuditDirectory), options.RunId);
        // Refuse an existing run: no accidental overwrite, stale receipt reuse or hidden-test retries on resume.
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (Directory.Exists(directory)) throw new IOException("The evidence run already exists; resumption is unsupported.");
        Directory.CreateDirectory(directory);
        using var ownership = new FileStream(Path.Combine(directory, "run.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        int operation = 0;
        string Next(string stage) => options.RunId + "/" + (++operation) + "/" + stage;
        async ValueTask<T> Work<T>(string stage, EvolutionResourceStage category, decimal maximum,
            Func<CancellationToken, ValueTask<EvolutionResourceResult<T>>> work)
        {
            var result = await EvolutionResourceWork.RunAsync(ledger, Next(stage), category, Units(maximum), Units(maximum),
                async token =>
                {
                    var receipt = await work(token).ConfigureAwait(false);
                    if (receipt is null || receipt.Actual[CostResource] <= 0)
                        throw new InvalidDataException("A positive resource receipt is required; unknown work retains its maximum.");
                    return new EvolutionResourceResult<EvolutionResourceResult<T>>(receipt, receipt.Actual, receipt.Outcome);
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Outcome != EvolutionResourceOutcome.Completed)
                throw new InvalidOperationException("A failed resource operation cannot supply a usable result.");
            return result.Value;
        }
        ValueTask<T> Fixed<T>(string stage, EvolutionResourceStage category, decimal price, Func<CancellationToken, T> work) =>
            Work<T>(stage, category, price, token => new(new EvolutionResourceResult<T>(work(token), Units(price))));
        async ValueTask Audit(string name, object record)
        {
            await Fixed("audit", EvolutionResourceStage.Persistence, options.AuditCost, _ =>
            {
                using var file = new FileStream(Path.Combine(directory, name + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, record);
                file.Flush(true);
                return true;
            });
        }
        async ValueTask<VerificationReceipt> Verify(ProgramArtifact artifact, ProgramVerifier verifier, bool final)
        {
            var request = new VerificationRequest(Guid.NewGuid().ToString("N"), artifact);
            var receipt = await Work(final ? "held-out" : "public-test",
                final ? EvolutionResourceStage.Confirmation : EvolutionResourceStage.Screening,
                options.VerificationMaximum, token => verifier(request, token));
            if (receipt is null || receipt.Nonce != request.Nonce || receipt.ArtifactFingerprint != artifact.Fingerprint ||
                receipt.OracleIdentity != options.OracleIdentity || !double.IsFinite(receipt.Duration) || receipt.Duration <= 0 ||
                receipt.Feedback is null || receipt.Feedback.Length > 4096)
                throw new InvalidDataException("Verification receipt does not bind valid measurements to this exact request/artifact/oracle.");
            return receipt;
        }

        await Audit("configuration", new { options, Source = parent });
        var compiler = await Fixed("compiler-setup", EvolutionResourceStage.Setup, options.SetupCost, _ => compilerFactory());
        var baseline = await Fixed("baseline-build", EvolutionResourceStage.Setup, options.BuildCost, t => compiler.Build(parent, t));
        if (baseline.Artifact is not { } incumbent) throw new InvalidOperationException("The initial program must compile.");
        RequireSource(incumbent, parent);
        await Audit("baseline", new { Artifact = incumbent, Image = incumbent.GetImage() });
        var initialTest = await Verify(incumbent, searchVerifier, false);
        await Audit("baseline-test", initialTest);
        if (!initialTest.Correct) throw new InvalidOperationException("The initial program must pass public tests.");
        var targets = await Fixed("catalog", EvolutionResourceStage.Proposal, options.PatchCost, t => compiler.Catalog(parent, t));
        ProgramArtifact? candidate = null;
        string feedback = "";
        int attempts = 0;
        for (int attempt = 1; attempt <= options.MaxRepairs + 1; attempt++)
        {
            attempts = attempt;
            var plan = await Work("model", attempt == 1 ? EvolutionResourceStage.Proposal : EvolutionResourceStage.Refinement,
                options.ModelMaximum, t => planner(new(parent, options.MeasuredBottleneck, targets, feedback, attempt), t));
            // Snapshot producer-owned collections before recording or applying; never retain mutable patch aliases.
            if (plan is null || plan.Edits is null || plan.Edits.Count is < 1 or > 16 ||
                string.IsNullOrWhiteSpace(plan.Hypothesis) || plan.Hypothesis.Length > 1024 ||
                plan.Edits.Any(e => e is null || e.Target is null || e.Replacement is null || e.Replacement.Length > ProgramSnapshot.MaximumCharacters))
                throw new InvalidDataException("The producer returned an unbounded patch plan.");
            plan = plan with { Edits = Array.AsReadOnly(plan.Edits.ToArray()) };
            await Audit($"attempt-{attempt}-plan", plan);
            ProgramSnapshot source;
            try
            {
                source = await Fixed("patch", EvolutionResourceStage.Refinement, options.PatchCost, t => compiler.Apply(parent, plan, t));
            }
            catch (ArgumentException)
            {
                feedback = "Invalid syntax-addressed patch. Use disjoint nodes from the original catalog.";
                await Audit($"attempt-{attempt}-rejected", new { feedback });
                continue;
            }
            var build = await Fixed("build", EvolutionResourceStage.Refinement, options.BuildCost, t => compiler.Build(source, t));
            if (build.Artifact is not { } artifact)
            {
                feedback = Bounded(build.Feedback);
                await Audit($"attempt-{attempt}-build", new { source, feedback });
                continue;
            }
            RequireSource(artifact, source);
            await Audit($"attempt-{attempt}-build", new { Artifact = artifact, Image = artifact.GetImage() });
            if (artifact.CompilerFingerprint != incumbent.CompilerFingerprint || artifact.ApiFingerprint != incumbent.ApiFingerprint)
            {
                feedback = "The dependency/compiler target or public API contract changed.";
                await Audit($"attempt-{attempt}-rejected", new { feedback });
                continue;
            }
            var search = await Verify(artifact, searchVerifier, false);
            await Audit($"attempt-{attempt}-test", search);
            if (!search.Correct) { feedback = Bounded(search.Feedback); continue; }
            candidate = artifact;
            break;
        }
        // No return to the proposal loop after either held-out call. Results are final evidence only.
        VerificationReceipt? baselineFinal = null, candidateFinal = null;
        if (candidate is not null)
        {
            baselineFinal = await Verify(incumbent, heldOutVerifier, true);
            await Audit("held-out-baseline", baselineFinal);
            candidateFinal = await Verify(candidate, heldOutVerifier, true);
            await Audit("held-out-candidate", candidateFinal);
        }
        bool promoted = baselineFinal is { Correct: true } && candidateFinal is { Correct: true } &&
            candidateFinal.Duration < baselineFinal.Duration / options.MinimumSpeedup;
        var result = new ImprovementResult(promoted, incumbent, candidate, attempts,
            promoted ? "Exact-artifact confirmation passed." : "No candidate passed all promotion gates.", baselineFinal, candidateFinal);
        await Audit("result", result);
        // The caller's ledger remains authoritative; this snapshot precedes its own persistence charge.
        await Audit("ledger", new { State = ledger.CaptureState(), ExcludesThisPersistenceCharge = options.AuditCost });
        return result;
    }

    /// <summary>Creates a resource amount in <see cref="CostResource"/> units.</summary>
    /// <param name="amount">The amount.</param>
    /// <returns>The resources.</returns>
    public static EvolutionResources Units(decimal amount) => EvolutionResources.Of(CostResource, amount);
    private static string Bounded(string text) => text is null || text.Length > 4096
        ? throw new InvalidDataException("Diagnostics exceed their bound.") : text;
    private static void RequireSource(ProgramArtifact artifact, ProgramSnapshot source)
    {
        if (artifact.Source.Fingerprint != source.Fingerprint) throw new InvalidDataException("The compiler returned a stale artifact.");
    }
    private static void Validate(ImprovementOptions options)
    {
        if (string.IsNullOrEmpty(options.RunId) || options.RunId.Length > 64 ||
            options.RunId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            string.IsNullOrWhiteSpace(options.MeasuredBottleneck) || options.MeasuredBottleneck.Length > 4096 ||
            string.IsNullOrWhiteSpace(options.OracleIdentity) || options.OracleIdentity.Length > 256 ||
            string.IsNullOrWhiteSpace(options.AuditDirectory) || options.MaxRepairs is < 0 or > 7 ||
            !double.IsFinite(options.MinimumSpeedup) || options.MinimumSpeedup < 1 || options.MinimumSpeedup > 1000)
            throw new ArgumentException("Invalid program improvement options.");
        foreach (decimal price in new[] { options.SetupCost, options.BuildCost, options.PatchCost, options.AuditCost,
                     options.ModelMaximum, options.VerificationMaximum })
            if (price <= 0 || price > 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(options));
    }
}
