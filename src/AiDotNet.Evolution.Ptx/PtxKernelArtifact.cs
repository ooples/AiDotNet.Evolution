using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>A versioned, content-addressed winning kernel: PTX, SM target, launch, resources and the evidence that admitted it.</summary>
/// <remarks>
/// <para>An artifact is created only from a candidate that compiled within its limits, passed every correctness case
/// and completed a paired timing replay; <see cref="Load"/> re-derives every timing statistic from the raw samples and
/// refuses a file whose numbers, PTX hash or content address do not agree. Integrity is not authenticity: keep the
/// directory private.</para>
/// <para>AiDotNet.Tensors consumes it through <see cref="ToConfiguration"/> and <see cref="PtxKernelConfigurationCodec"/>,
/// the configuration and codec its <c>KernelTuningArtifactRegistry&lt;TConfiguration&gt;</c> and deployment snapshots
/// take; the paired samples map one to one onto its <c>KernelTuningPairedEvidence</c>, and registers, compile time and
/// the correctness errors onto its <c>KernelTuningMeasurement</c>. Registering does not promote: Tensors' own replay and
/// promotion policy still decide.</para>
/// </remarks>
[Experimental("AIDEVO005")]
public sealed class PtxKernelArtifact
{
    /// <summary>The artifact schema version.</summary>
    public const int SchemaVersion = 1;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const string FileSuffix = ".ptx-artifact.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 32,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ArtifactDocument _document;

    private PtxKernelArtifact(ArtifactDocument document, byte[] bytes)
    {
        _document = document;
        ArtifactId = ProgramSnapshot.Digest(bytes);
        Evidence = new PtxPairedTimingEvidence(document.Samples ?? throw new InvalidDataException("The artifact has no timing samples."),
            document.CalibratedNoiseRatio);
        Launch = document.Launch ?? throw new InvalidDataException("The artifact has no launch configuration.");
        Resources = document.Resources ?? throw new InvalidDataException("The artifact has no resources.");
    }

    /// <summary>Gets the SHA-256 of the canonical bytes, which is also the file name.</summary>
    public string ArtifactId { get; }
    /// <summary>Gets the contract name.</summary>
    public string ContractName => _document.ContractName;
    /// <summary>Gets the contract fingerprint the kernel was verified against.</summary>
    public string ContractFingerprint => _document.ContractFingerprint;
    /// <summary>Gets the PTX entry point.</summary>
    public string EntryPoint => _document.EntryPoint;
    /// <summary>Gets the SM target as major * 10 + minor.</summary>
    public int SmVersion => _document.SmVersion;
    /// <summary>Gets the PTX text, including its launch header.</summary>
    public string Ptx => _document.Ptx;
    /// <summary>Gets the launch configuration.</summary>
    public PtxLaunchConfiguration Launch { get; }
    /// <summary>Gets the JIT-assigned resources.</summary>
    public PtxKernelResources Resources { get; }
    /// <summary>Gets the paired timing evidence against the incumbent.</summary>
    public PtxPairedTimingEvidence Evidence { get; }
    /// <summary>Gets whether the evidence passed the promotion gate recorded in the artifact.</summary>
    public bool QualifiedForPromotion => _document.Qualified;
    /// <summary>Gets the reference the outputs were judged against.</summary>
    public string ReferenceIdentity => _document.ReferenceIdentity;
    /// <summary>Gets how many shapes passed correctness.</summary>
    public int CorrectnessCases => _document.CorrectnessCases;
    /// <summary>Gets the largest absolute output error over every case.</summary>
    public double MaxAbsoluteError => _document.MaxAbsoluteError;
    /// <summary>Gets the largest relative output error over every case.</summary>
    public double MaxRelativeError => _document.MaxRelativeError;
    /// <summary>Gets the device identity the evidence was measured on.</summary>
    public string DeviceIdentity => _document.DeviceIdentity;
    /// <summary>Gets the SHA-256 of the incumbent the candidate was measured against.</summary>
    public string IncumbentSha256 => _document.IncumbentSha256;
    /// <summary>Gets the timing protocol identity.</summary>
    public string TimingProtocol => _document.TimingProtocol;
    /// <summary>Gets the compiler fingerprint.</summary>
    public string CompilerFingerprint => _document.CompilerFingerprint;

    /// <summary>Builds an artifact from a verified winner.</summary>
    /// <param name="compiler">The contract's compiler.</param>
    /// <param name="correctness">The passed correctness report for the candidate.</param>
    /// <param name="timing">The completed timing report for the same candidate.</param>
    /// <param name="incumbentSource">The incumbent it was measured against.</param>
    /// <param name="timingProtocol">The timing protocol identity, such as <see cref="PtxTimingEvaluator.Identity"/>.</param>
    /// <returns>The artifact.</returns>
    /// <exception cref="ArgumentException">The evidence is incomplete or describes a different candidate.</exception>
    public static PtxKernelArtifact Create(PtxProgramCompiler compiler, PtxCorrectnessReport correctness, PtxTimingReport timing,
        string incumbentSource, string timingProtocol)
    {
        if (compiler is null) throw new ArgumentNullException(nameof(compiler));
        if (correctness is null) throw new ArgumentNullException(nameof(correctness));
        if (timing is null) throw new ArgumentNullException(nameof(timing));
        if (string.IsNullOrWhiteSpace(incumbentSource)) throw new ArgumentException("The incumbent is required.", nameof(incumbentSource));
        if (string.IsNullOrWhiteSpace(timingProtocol)) throw new ArgumentException("The timing protocol is required.", nameof(timingProtocol));
        if (!correctness.Passed || correctness.Compilation is not { Succeeded: true, Resources: { } resources } compilation)
            throw new ArgumentException("Only a candidate that compiled and passed correctness can become an artifact.", nameof(correctness));
        if (!timing.Completed || timing.Evidence is not { } evidence || timing.Device is not { } device)
            throw new ArgumentException("Only a completed timing replay can become evidence.", nameof(timing));
        if (timing.CandidateSha256 != ProgramSnapshot.Digest(compilation.Source) || timing.IncumbentSha256 != ProgramSnapshot.Digest(incumbentSource))
            throw new ArgumentException("The timing replay measured a different candidate or incumbent than the one verified.", nameof(timing));
        PtxKernelContract contract = compiler.Contract;
        var document = new ArtifactDocument
        {
            SchemaVersion = SchemaVersion,
            ContractName = contract.Name,
            ContractFingerprint = contract.Fingerprint,
            EntryPoint = contract.EntryPoint,
            SmVersion = contract.Target.SmVersion,
            Ptx = compilation.Source,
            PtxSha256 = ProgramSnapshot.Digest(compilation.Source),
            Launch = compilation.Launch,
            TimingShape = new SortedDictionary<string, long>(contract.TimingShape.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
            Resources = resources,
            ReferenceIdentity = correctness.ReferenceIdentity,
            CorrectnessCases = correctness.Cases.Count,
            MaxAbsoluteError = correctness.MaxAbsoluteError,
            MaxRelativeError = correctness.MaxRelativeError,
            ToleranceAbsolute = contract.Tolerance.Absolute,
            ToleranceRelative = contract.Tolerance.Relative,
            Samples = evidence.Samples.ToList(),
            CalibratedNoiseRatio = evidence.CalibratedNoiseRatio,
            Qualified = timing.QualifiesForPromotion,
            TimingProtocol = timingProtocol,
            DeviceIdentity = device.Identity,
            IncumbentSha256 = ProgramSnapshot.Digest(incumbentSource),
            CompilerFingerprint = compiler.Fingerprint,
            Producer = "AiDotNet.Evolution.Ptx/" + typeof(PtxKernelArtifact).Assembly.GetName().Version
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("The artifact exceeds 16 MiB.");
        return new PtxKernelArtifact(document, bytes);
    }

    /// <summary>Serializes the artifact as canonical JSON.</summary>
    /// <returns>The JSON bytes whose SHA-256 is <see cref="ArtifactId"/>.</returns>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(_document, Json);

    /// <summary>Reads and fully re-validates an artifact.</summary>
    /// <param name="bytes">The canonical JSON bytes.</param>
    /// <returns>The artifact.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a valid, self-consistent artifact.</exception>
    public static PtxKernelArtifact FromBytes(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Invalid artifact size.");
        try
        {
            ArtifactDocument document = JsonSerializer.Deserialize<ArtifactDocument>(bytes, Json) ?? throw new InvalidDataException("Empty artifact.");
            if (document.SchemaVersion != SchemaVersion) throw new InvalidDataException("Unsupported artifact schema.");
            if (string.IsNullOrEmpty(document.Ptx) || ProgramSnapshot.Digest(document.Ptx) != document.PtxSha256)
                throw new InvalidDataException("The PTX does not match its recorded hash.");
            if (document.Launch is null || document.Resources is null || document.Samples is null || document.TimingShape is null)
                throw new InvalidDataException("The artifact is incomplete.");
            if (!JsonSerializer.SerializeToUtf8Bytes(document, Json).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("The artifact is not in canonical form.");
            return new PtxKernelArtifact(document, bytes);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The artifact is malformed: " + exception.Message, exception);
        }
    }

    /// <summary>Writes the artifact to <c>{directory}/{ArtifactId}.ptx-artifact.json</c>, atomically and idempotently.</summary>
    /// <param name="directory">An absolute directory.</param>
    /// <returns>The file path.</returns>
    public string Save(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("An absolute directory is required.", nameof(directory));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ArtifactId + FileSuffix);
        byte[] bytes = ToBytes();
        if (File.Exists(path))
        {
            if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return path;
            throw new IOException("A different file already occupies this artifact's address.");
        }
        string pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        try
        {
            File.Move(pending, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(pending);
        }
        return path;
    }

    /// <summary>Loads and verifies an artifact file; its name must be its content address.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The artifact.</returns>
    /// <exception cref="InvalidDataException">The file is invalid or its content does not match its name.</exception>
    public static PtxKernelArtifact Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > MaximumBytes) throw new InvalidDataException("Invalid artifact file.");
        PtxKernelArtifact artifact = FromBytes(File.ReadAllBytes(path));
        if (!string.Equals(info.Name, artifact.ArtifactId + FileSuffix, StringComparison.Ordinal))
            throw new InvalidDataException("The artifact's content does not match its file name.");
        return artifact;
    }

    /// <summary>Gets the deployable configuration for the Tensors kernel-tuning registry.</summary>
    /// <returns>The configuration; encode it with <see cref="PtxKernelConfigurationCodec"/>.</returns>
    public PtxKernelConfiguration ToConfiguration() =>
        new(Ptx, EntryPoint, SmVersion, Launch.BlockX, Launch.BlockY, Launch.BlockZ, ContractFingerprint);

    private sealed class ArtifactDocument
    {
        public int SchemaVersion { get; set; }
        public string ContractName { get; set; } = string.Empty;
        public string ContractFingerprint { get; set; } = string.Empty;
        public string EntryPoint { get; set; } = string.Empty;
        public int SmVersion { get; set; }
        public string Ptx { get; set; } = string.Empty;
        public string PtxSha256 { get; set; } = string.Empty;
        public PtxLaunchConfiguration? Launch { get; set; }
        public SortedDictionary<string, long>? TimingShape { get; set; }
        public PtxKernelResources? Resources { get; set; }
        public string ReferenceIdentity { get; set; } = string.Empty;
        public int CorrectnessCases { get; set; }
        public double MaxAbsoluteError { get; set; }
        public double MaxRelativeError { get; set; }
        public double ToleranceAbsolute { get; set; }
        public double ToleranceRelative { get; set; }
        public List<PtxPairedSample>? Samples { get; set; }
        public double CalibratedNoiseRatio { get; set; }
        public bool Qualified { get; set; }
        public string TimingProtocol { get; set; } = string.Empty;
        public string DeviceIdentity { get; set; } = string.Empty;
        public string IncumbentSha256 { get; set; } = string.Empty;
        public string CompilerFingerprint { get; set; } = string.Empty;
        public string Producer { get; set; } = string.Empty;
    }
}

/// <summary>The deployable part of a PTX winner: the kernel text and how to launch it.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxKernelConfiguration : IEquatable<PtxKernelConfiguration>
{
    /// <summary>Creates a configuration.</summary>
    /// <param name="ptx">The PTX text.</param>
    /// <param name="entryPoint">The entry point.</param>
    /// <param name="smVersion">The SM target.</param>
    /// <param name="blockX">Threads in x.</param>
    /// <param name="blockY">Threads in y.</param>
    /// <param name="blockZ">Threads in z.</param>
    /// <param name="contractFingerprint">The contract the kernel was verified against.</param>
    [JsonConstructor]
    public PtxKernelConfiguration(string ptx, string entryPoint, int smVersion, int blockX, int blockY, int blockZ, string contractFingerprint)
    {
        if (string.IsNullOrEmpty(ptx) || ptx.Length > PtxSourceInspector.MaximumSourceCharacters) throw new ArgumentException("Bounded PTX is required.", nameof(ptx));
        if (!PtxNames.IsIdentifier(entryPoint)) throw new ArgumentException("An entry point is required.", nameof(entryPoint));
        if (smVersion is < 50 or > 200 || blockX < 1 || blockY < 1 || blockZ < 1 || (long)blockX * blockY * blockZ > 1024)
            throw new ArgumentException("Invalid target or block.");
        if (string.IsNullOrWhiteSpace(contractFingerprint)) throw new ArgumentException("A contract fingerprint is required.", nameof(contractFingerprint));
        Ptx = ptx;
        EntryPoint = entryPoint;
        SmVersion = smVersion;
        BlockX = blockX;
        BlockY = blockY;
        BlockZ = blockZ;
        ContractFingerprint = contractFingerprint;
    }

    /// <summary>Gets the PTX text.</summary>
    public string Ptx { get; }
    /// <summary>Gets the entry point.</summary>
    public string EntryPoint { get; }
    /// <summary>Gets the SM target.</summary>
    public int SmVersion { get; }
    /// <summary>Gets threads in x.</summary>
    public int BlockX { get; }
    /// <summary>Gets threads in y.</summary>
    public int BlockY { get; }
    /// <summary>Gets threads in z.</summary>
    public int BlockZ { get; }
    /// <summary>Gets the contract fingerprint.</summary>
    public string ContractFingerprint { get; }

    /// <inheritdoc/>
    public bool Equals(PtxKernelConfiguration? other) => other is not null && Ptx == other.Ptx && EntryPoint == other.EntryPoint &&
        SmVersion == other.SmVersion && BlockX == other.BlockX && BlockY == other.BlockY && BlockZ == other.BlockZ &&
        ContractFingerprint == other.ContractFingerprint;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PtxKernelConfiguration);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Ptx), EntryPoint, SmVersion, BlockX, BlockY, BlockZ, ContractFingerprint);
}

/// <summary>Canonical text encoding of <see cref="PtxKernelConfiguration"/>, as the Tensors kernel-tuning stores require.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxKernelConfigurationCodec : IEvolutionGenomeCodec<PtxKernelConfiguration>
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 4, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc/>
    public string Id => "aidotnet-evolution-ptx-kernel-configuration";

    /// <inheritdoc/>
    public string VersionHash => "ptx-kernel-configuration-json-v1";

    /// <inheritdoc/>
    public string Serialize(PtxKernelConfiguration genome) =>
        JsonSerializer.Serialize(genome ?? throw new ArgumentNullException(nameof(genome)), Json);

    /// <inheritdoc/>
    public PtxKernelConfiguration Deserialize(string payload)
    {
        try
        {
            PtxKernelConfiguration configuration = JsonSerializer.Deserialize<PtxKernelConfiguration>(payload ?? throw new ArgumentNullException(nameof(payload)), Json)
                ?? throw new InvalidDataException("The configuration payload is empty.");
            if (!string.Equals(Serialize(configuration), payload, StringComparison.Ordinal))
                throw new InvalidDataException("The configuration payload is not canonical.");
            return configuration;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The configuration payload is invalid.", exception);
        }
    }
}