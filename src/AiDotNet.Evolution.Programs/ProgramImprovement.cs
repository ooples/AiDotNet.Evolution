using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

public sealed record VerificationRequest(string Nonce, ProgramArtifact Artifact);
/// <summary>Search feedback must contain only public test diagnostics. Held-out feedback is never sent to a proposer.</summary>
public sealed record VerificationReceipt(string Nonce, string ArtifactFingerprint, string OracleIdentity,
    bool Correct, double Duration, string Feedback);
public delegate ValueTask<EvolutionResourceResult<VerificationReceipt>> ProgramVerifier(
    VerificationRequest request, CancellationToken cancellationToken);
public delegate ValueTask<EvolutionResourceResult<PatchPlan>> ProgramPlanner(
    SearchRequest request, CancellationToken cancellationToken);

public sealed record ImprovementOptions(string RunId, string MeasuredBottleneck, string OracleIdentity,
    string AuditDirectory, int MaxRepairs = 2, decimal SetupCost = 1, decimal BuildCost = 1,
    decimal PatchCost = 1, decimal AuditCost = 1, decimal ModelMaximum = 10,
    decimal VerificationMaximum = 10, double MinimumSpeedup = 1.0);

public sealed record ImprovementResult(bool Promoted, ProgramArtifact Incumbent, ProgramArtifact? Candidate,
    int Attempts, string Reason, VerificationReceipt? BaselineConfirmation, VerificationReceipt? CandidateConfirmation);

/// <summary>One bounded proposal/repair session and one sealed final comparison. Not an OS execution sandbox.</summary>
public static class ProgramImprovement
{
    public const string CostResource = "program_work_units";

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
