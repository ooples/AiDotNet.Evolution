using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>One checksummed engine/accounting boundary, captured only with no in-flight resource work.</summary>
/// <remarks>The caller must pause engine mutation and persist this envelope instead of independent snapshots.
/// This is not a write-ahead journal: never resume an old boundary after unjournaled external spending.
/// Keep the expected checksum and newest-boundary identity in trusted custody to prevent rollback.</remarks>
public sealed class EvolutionResourceBoundary
{
    private const int MaximumCharacters = 16 * 1024 * 1024;
    internal EvolutionResourceBoundary(string runId, string engineState, string ledgerState)
    {
        Guard.NotNull(engineState); Guard.NotNull(ledgerState);
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 256 || string.IsNullOrWhiteSpace(engineState) ||
            engineState.Length > MaximumCharacters || ledgerState.Length > MaximumCharacters)
            throw new ArgumentException("Invalid or oversized coordinated boundary.");
        RunId = runId; EngineState = engineState; LedgerState = ledgerState;
        Checksum = EvolutionHash.Combine(new[] { "resource-boundary-v1", runId,
            EvolutionHash.Compute(engineState), EvolutionHash.Compute(ledgerState) });
    }
    /// <summary>Gets the accounting run identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the opaque serialized engine checkpoint, validated by its engine on restore.</summary>
    public string EngineState { get; }
    /// <summary>Gets the corresponding ledger state, including complete stage history.</summary>
    public string LedgerState { get; }
    /// <summary>Gets the digest binding engine state to accounting state.</summary>
    public string Checksum { get; }

    /// <summary>Captures both states while blocking ledger admission/settlement; the engine must already be paused.</summary>
    public static EvolutionResourceBoundary Capture(EvolutionResourceLedger ledger, Func<string> captureEngine)
    {
        Guard.NotNull(ledger); Guard.NotNull(captureEngine);
        return ledger.CaptureBoundary(captureEngine);
    }

    /// <summary>Creates a fresh restored ledger; current limits may change but recorded spending cannot disappear.</summary>
    /// <remarks>Restore EngineState into a fresh engine and publish the pair only after both validations succeed.</remarks>
    public EvolutionResourceLedger RestoreLedger(EvolutionResources limits, int retainedReceiptLimit = 1024, int maximumOperations = 100_000)
    {
        var ledger = new EvolutionResourceLedger(RunId, limits, retainedReceiptLimit, maximumOperations);
        ledger.RestoreState(LedgerState);
        if (ledger.Snapshot().Admitted != ledger.Snapshot().Settled)
            throw new InvalidDataException("A coordinated boundary cannot contain pending work.");
        return ledger;
    }

    /// <summary>Serializes a bounded envelope for one atomic persistence operation.</summary>
    public string Serialize()
    {
        string json = JsonSerializer.Serialize(new Envelope { RunId = RunId, EngineState = EngineState, LedgerState = LedgerState, Checksum = Checksum });
        if (json.Length > MaximumCharacters * 4) throw new InvalidOperationException("Boundary envelope exceeds its bound.");
        return json;
    }

    /// <summary>Validates against an externally retained digest before exposing either state.</summary>
    public static EvolutionResourceBoundary Deserialize(string json, string expectedChecksum)
    {
        Guard.NotNull(json);
        if (json.Length > MaximumCharacters * 4) throw new ArgumentException("Boundary envelope exceeds its bound.", nameof(json));
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != 4 || fields.Distinct().Count() != 4 ||
            fields.Any(field => field is not ("RunId" or "EngineState" or "LedgerState" or "Checksum")))
            throw new InvalidDataException("Invalid boundary fields.");
        var envelope = JsonSerializer.Deserialize<Envelope>(json) ?? throw new InvalidDataException("Missing boundary.");
        var boundary = new EvolutionResourceBoundary(envelope.RunId, envelope.EngineState, envelope.LedgerState);
        if (boundary.Checksum != envelope.Checksum || boundary.Checksum != expectedChecksum)
            throw new InvalidDataException("The engine/accounting boundary checksum differs.");
        return boundary;
    }

    /// <summary>Publishes a new envelope by same-directory rename after flushing; refuses an existing target.</summary>
    /// <remarks>File flush is not a portable directory-fsync guarantee. Trusted local filesystem semantics are required.</remarks>
    public void SaveNew(string path)
    {
        Guard.NotNullOrWhiteSpace(path);
        string target = Path.GetFullPath(path);
        string temporary = target + ".pending-" + Guid.NewGuid().ToString("N");
        byte[] bytes = Encoding.UTF8.GetBytes(Serialize());
        bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Envelope
    {
        public string RunId { get; set; } = "";
        public string EngineState { get; set; } = "";
        public string LedgerState { get; set; } = "";
        public string Checksum { get; set; } = "";
    }
}
