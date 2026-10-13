using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Everything that defines a kernel's job, as data: signature, launch, shapes, inputs and tolerance.</summary>
/// <remarks>
/// <para>One contract type describes every operator: there is no per-operator code anywhere in this package. A
/// convolution, a LayerNorm, a GEMM or a fused optimizer step differ only in the contract they supply - its
/// parameters, shape symbols, launch extents and tolerance - and in the reference that defines the right answer.</para>
/// <para>The same contract drives every stage: the compiler checks a candidate's <c>.entry</c> signature against
/// <see cref="Parameters"/>, the correctness evaluator builds its seeded cases from <see cref="ValidationShapes"/>
/// plus shape fuzzing over <see cref="Symbols"/>, the timing evaluator measures at <see cref="TimingShape"/>, and the
/// proposal prompt carries the contract verbatim. Contracts serialize to canonical JSON (<see cref="ToJson"/>), so they
/// can live beside the kernels they describe; <see cref="Fingerprint"/> identifies one exactly.</para>
/// </remarks>
[Experimental("AIDEVO005")]
public sealed class PtxKernelContract
{
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Creates and validates a contract.</summary>
    /// <param name="name">The kernel's logical name, such as <c>layernorm-forward-f32</c>.</param>
    /// <param name="entryPoint">The PTX <c>.entry</c> name every candidate must define.</param>
    /// <param name="description">What the kernel computes, in words; shown to the model. At most 4096 characters.</param>
    /// <param name="target">The SM target and resource limits.</param>
    /// <param name="parameters">The kernel arguments in <c>.param</c> order.</param>
    /// <param name="symbols">The shape symbols and their fuzzing ranges.</param>
    /// <param name="launch">The incumbent's launch configuration; candidates may change only its block dimensions.</param>
    /// <param name="tolerance">The default output tolerance.</param>
    /// <param name="timingShape">The shape timing measures, usually the production shape.</param>
    /// <param name="validationShapes">Fixed shapes every candidate must pass, in addition to the fuzzed ones.</param>
    /// <param name="fuzzCases">How many fuzzed shapes to add, 0 to 64.</param>
    /// <param name="seed">The seed for inputs and shape fuzzing.</param>
    /// <exception cref="ArgumentException">The contract is inconsistent.</exception>
    [JsonConstructor]
    public PtxKernelContract(string name, string entryPoint, string? description, PtxTargetLimits target,
        IReadOnlyList<PtxKernelParameter> parameters, IReadOnlyList<PtxShapeSymbol> symbols, PtxLaunchConfiguration launch,
        PtxTolerance tolerance, IReadOnlyDictionary<string, long> timingShape,
        IReadOnlyList<IReadOnlyDictionary<string, long>>? validationShapes, int fuzzCases, ulong seed)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("A bounded printable contract name is required.", nameof(name));
        if (!PtxNames.IsIdentifier(entryPoint)) throw new ArgumentException("The entry point must be a PTX identifier.", nameof(entryPoint));
        if (description?.Length > 4096) throw new ArgumentException("The description exceeds 4096 characters.", nameof(description));
        Name = name;
        EntryPoint = entryPoint;
        Description = description ?? string.Empty;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Parameters = Array.AsReadOnly((parameters ?? throw new ArgumentNullException(nameof(parameters))).ToArray());
        Symbols = Array.AsReadOnly((symbols ?? throw new ArgumentNullException(nameof(symbols))).ToArray());
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        Tolerance = tolerance ?? throw new ArgumentNullException(nameof(tolerance));
        if (Parameters.Count is < 1 or > 64 || Parameters.Any(p => p is null) || Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != Parameters.Count)
            throw new ArgumentException("A contract needs 1 to 64 uniquely named parameters.", nameof(parameters));
        if (!Parameters.Any(p => p.IsOutput)) throw new ArgumentException("A contract needs at least one output buffer.", nameof(parameters));
        if (Symbols.Count > 16 || Symbols.Any(s => s is null) || Symbols.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != Symbols.Count)
            throw new ArgumentException("A contract has at most 16 uniquely named symbols.", nameof(symbols));
        var known = new HashSet<string>(Symbols.Select(s => s.Name), StringComparer.Ordinal) { PtxExtent.BlockX, PtxExtent.BlockY, PtxExtent.BlockZ };
        IEnumerable<PtxExtent> extents = Parameters.SelectMany(p => new[] { p.Length, p.Value }).OfType<PtxExtent>()
            .Concat(new[] { Launch.GridX, Launch.GridY, Launch.GridZ, Launch.DynamicSharedMemoryBytes });
        string? unknown = extents.SelectMany(e => e.Symbols).FirstOrDefault(s => !known.Contains(s));
        if (unknown is not null) throw new ArgumentException("Extent symbol '" + unknown + "' is not declared.", nameof(symbols));
        if (Parameters.SelectMany(p => new[] { p.Length, p.Value }).OfType<PtxExtent>().SelectMany(e => e.Symbols)
            .Any(s => s is PtxExtent.BlockX or PtxExtent.BlockY or PtxExtent.BlockZ))
            throw new ArgumentException("Buffer lengths and scalar values cannot depend on the block size; only the launch may.", nameof(parameters));
        if (Launch.ThreadsPerBlock > Target.MaxThreadsPerBlock) throw new ArgumentException("The launch exceeds the target's threads per block.", nameof(launch));
        if (fuzzCases is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(fuzzCases));
        FuzzCases = fuzzCases;
        Seed = seed;
        TimingShape = CompleteShape(timingShape, nameof(timingShape));
        ValidationShapes = Array.AsReadOnly((validationShapes ?? Array.Empty<IReadOnlyDictionary<string, long>>())
            .Select(shape => CompleteShape(shape, nameof(validationShapes))).ToArray());
        if (ValidationShapes.Count > 64) throw new ArgumentException("At most 64 fixed validation shapes.", nameof(validationShapes));
        foreach (IReadOnlyDictionary<string, long> shape in ValidationShapes.Append(TimingShape)) _ = Resolve(shape, Launch);
        Fingerprint = ProgramSnapshot.Digest(ToJson());
    }

    /// <summary>Gets the logical kernel name.</summary>
    public string Name { get; }
    /// <summary>Gets the PTX entry point.</summary>
    public string EntryPoint { get; }
    /// <summary>Gets the description shown to the model.</summary>
    public string Description { get; }
    /// <summary>Gets the SM target and limits.</summary>
    public PtxTargetLimits Target { get; }
    /// <summary>Gets the arguments in <c>.param</c> order.</summary>
    public IReadOnlyList<PtxKernelParameter> Parameters { get; }
    /// <summary>Gets the shape symbols.</summary>
    public IReadOnlyList<PtxShapeSymbol> Symbols { get; }
    /// <summary>Gets the incumbent launch configuration.</summary>
    public PtxLaunchConfiguration Launch { get; }
    /// <summary>Gets the default output tolerance.</summary>
    public PtxTolerance Tolerance { get; }
    /// <summary>Gets the timed shape.</summary>
    public IReadOnlyDictionary<string, long> TimingShape { get; }
    /// <summary>Gets the fixed validation shapes.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, long>> ValidationShapes { get; }
    /// <summary>Gets the number of fuzzed shapes.</summary>
    public int FuzzCases { get; }
    /// <summary>Gets the seed for inputs and fuzzing.</summary>
    public ulong Seed { get; }

    /// <summary>Gets the SHA-256 of the canonical JSON, which identifies the contract exactly.</summary>
    [JsonIgnore]
    public string Fingerprint { get; }

    /// <summary>Serializes the contract as canonical JSON.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads and validates a contract written by <see cref="ToJson"/> or by hand.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="JsonException">The text is not a contract.</exception>
    /// <exception cref="ArgumentException">The contract is inconsistent.</exception>
    public static PtxKernelContract FromJson(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        if (json.Length > 1024 * 1024) throw new ArgumentException("A contract is at most 1 MiB of JSON.", nameof(json));
        return JsonSerializer.Deserialize<PtxKernelContract>(json, Json) ?? throw new JsonException("The contract is empty.");
    }

    /// <summary>Returns every shape correctness checks: the fixed shapes, the timing shape, then deterministic fuzzing.</summary>
    /// <returns>The cases, without duplicates.</returns>
    /// <remarks>Fuzzing first covers the degenerate minimum, then sweeps every tile boundary (t - 1, t, t + 1, 2t + 1) and the
    /// maximum, then adds seeded mixes and ragged values in between, so partial tiles are always exercised, not just aligned
    /// shapes. A sweep larger than <see cref="FuzzCases"/> is cut at that count.</remarks>
    public IReadOnlyList<PtxShapeCase> GetValidationCases()
    {
        var cases = new List<PtxShapeCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string label, IReadOnlyDictionary<string, long> shape)
        {
            string key = string.Join(",", shape.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
            if (seen.Add(key)) cases.Add(new PtxShapeCase(label, shape));
        }
        foreach (IReadOnlyDictionary<string, long> shape in ValidationShapes) Add("fixed", shape);
        Add("timing", TimingShape);
        if (Symbols.Count == 0 || FuzzCases == 0) return cases.AsReadOnly();
        var pools = Symbols.Select(symbol =>
        {
            var values = new SortedSet<long> { symbol.Minimum, symbol.Maximum };
            foreach (long tile in symbol.TileSizes)
                foreach (long value in new[] { tile - 1, tile, tile + 1, 2 * tile + 1 })
                    if (value >= symbol.Minimum && value <= symbol.Maximum) values.Add(value);
            return values.ToArray();
        }).ToArray();
        ulong state = Seed ^ 0xD6E8FEB86659FD93UL;
        ulong Next()
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        int target = cases.Count + FuzzCases, sweep = pools.Max(pool => pool.Length);
        for (int attempt = 0; cases.Count < target && attempt < FuzzCases * 16; attempt++)
        {
            var shape = new Dictionary<string, long>(StringComparer.Ordinal);
            string label;
            if (attempt == 0)
            {
                label = "degenerate";
                foreach (PtxShapeSymbol symbol in Symbols) shape[symbol.Name] = symbol.Minimum;
            }
            else if (attempt < sweep)
            {
                // Every boundary value is guaranteed a case, not left to the draw: symbol i takes the attempt-th value of its pool.
                label = "tile-edge";
                for (int i = 0; i < Symbols.Count; i++) shape[Symbols[i].Name] = pools[i][attempt % pools[i].Length];
            }
            else if (attempt % 3 == 0)
            {
                label = "ragged";
                foreach (PtxShapeSymbol symbol in Symbols)
                    shape[symbol.Name] = symbol.Minimum + (long)(Next() % (ulong)(symbol.Maximum - symbol.Minimum + 1));
            }
            else
            {
                label = "tile-edge";
                for (int i = 0; i < Symbols.Count; i++) shape[Symbols[i].Name] = pools[i][(int)(Next() % (ulong)pools[i].Length)];
            }
            try
            {
                _ = Resolve(shape, Launch);
                Add(label, shape);
            }
            catch (ArgumentException)
            {
                // A drawn shape the launch cannot express (for example a grid past its dimension limit) is skipped.
            }
        }
        return cases.AsReadOnly();
    }

    internal static PtxWorkerLaunch Resolve(IReadOnlyDictionary<string, long> shape, PtxLaunchConfiguration launch) => launch.Resolve(shape);

    internal static long BufferElements(PtxKernelParameter parameter, IReadOnlyDictionary<string, long> shape)
    {
        long elements = (parameter.Length ?? throw new InvalidOperationException("Not a buffer.")).Evaluate(shape);
        if (elements < 1) throw new ArgumentException("Buffer '" + parameter.Name + "' has no elements at " + new PtxShapeCase("shape", shape) + ".");
        return elements;
    }

    private IReadOnlyDictionary<string, long> CompleteShape(IReadOnlyDictionary<string, long>? shape, string parameter)
    {
        if (shape is null) throw new ArgumentNullException(parameter);
        var copy = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (PtxShapeSymbol symbol in Symbols)
        {
            if (!shape.TryGetValue(symbol.Name, out long value) || value < 1)
                throw new ArgumentException("Shape needs a positive value for '" + symbol.Name + "'.", parameter);
            copy[symbol.Name] = value;
        }
        if (shape.Keys.Any(key => !copy.ContainsKey(key))) throw new ArgumentException("A shape names an undeclared symbol.", parameter);
        return copy;
    }

    /// <summary>The contract as the proposal prompt shows it: everything a rewrite must preserve.</summary>
    internal object ToPromptData() => new
    {
        name = Name,
        entryPoint = EntryPoint,
        description = Description,
        target = new { sm = Target.TargetName, Target.MaxRegistersPerThread, Target.MaxSharedMemoryPerBlockBytes, Target.MaxThreadsPerBlock, Target.MaxLocalBytesPerThread },
        signature = Parameters.Select((p, index) => new
        {
            index,
            name = p.Name,
            kind = p.Kind.ToString(),
            ptxType = p.Kind == PtxParameterKind.Buffer ? ".u64 (global pointer)" : PtxSourceInspector.ScalarParamType(p.ElementType),
            elementType = p.ElementType.ToString(),
            role = p.Kind == PtxParameterKind.Buffer ? p.Role.ToString() : null,
            elements = p.Length?.ToString(),
            value = p.Kind == PtxParameterKind.Scalar ? (p.Value?.ToString() ?? p.Constant?.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            tolerance = p.IsOutput ? (p.Tolerance ?? Tolerance) : null
        }),
        layout = "Buffers are dense, row-major, contiguous arrays of elementType; element counts are the extents shown.",
        launch = new
        {
            grid = new[] { Launch.GridX.ToString(), Launch.GridY.ToString(), Launch.GridZ.ToString() },
            block = new[] { Launch.BlockX, Launch.BlockY, Launch.BlockZ },
            dynamicSharedMemoryBytes = Launch.DynamicSharedMemoryBytes.ToString(),
            note = "Only the block dimensions may change, through the launch header; grid extents naming blockX/Y/Z follow."
        },
        symbols = Symbols.Select(s => new { s.Name, s.Minimum, s.Maximum, s.TileSizes }),
        timingShape = TimingShape,
        validation = "Every candidate must match the reference on the fixed shapes, the timing shape and " + FuzzCases +
            " fuzzed shapes (degenerate, tile-edge and ragged sizes) before it is timed.",
        defaultTolerance = Tolerance
    };
}