using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Ptx;

/// <summary>One kernel argument, in PTX <c>.param</c> order: a guarded device buffer or a by-value scalar.</summary>
public sealed class PtxKernelParameter
{
    /// <summary>Creates a parameter; prefer the factory methods.</summary>
    /// <param name="name">An identifier, unique within the contract.</param>
    /// <param name="kind">Buffer or scalar.</param>
    /// <param name="elementType">The element type of the buffer or the scalar's type.</param>
    /// <param name="role">How a buffer is used; ignored for scalars.</param>
    /// <param name="length">A buffer's element count.</param>
    /// <param name="distribution">How an input buffer is filled; defaults to uniform [-1, 1).</param>
    /// <param name="tolerance">An output buffer's tolerance, or <c>null</c> for the contract default.</param>
    /// <param name="value">A scalar's value: an extent for integer scalars, or a constant via <paramref name="constant"/>.</param>
    /// <param name="constant">A scalar constant, used when <paramref name="value"/> is <c>null</c>.</param>
    /// <exception cref="ArgumentException">The combination is invalid.</exception>
    [JsonConstructor]
    public PtxKernelParameter(string name, PtxParameterKind kind, PtxElementType elementType, PtxBufferRole role,
        PtxExtent? length, PtxValueDistribution? distribution, PtxTolerance? tolerance, PtxExtent? value, double? constant)
    {
        if (!PtxNames.IsIdentifier(name)) throw new ArgumentException("A parameter needs an identifier name.", nameof(name));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(elementType) || !Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Name = name;
        Kind = kind;
        ElementType = elementType;
        if (kind == PtxParameterKind.Buffer)
        {
            Length = length ?? throw new ArgumentException("A buffer needs a length.", nameof(length));
            Role = role;
            Distribution = role == PtxBufferRole.Output ? null : distribution ?? PtxValueDistribution.Uniform(-1, 1);
            Tolerance = role == PtxBufferRole.Input ? null : tolerance;
            if (value is not null || constant is not null) throw new ArgumentException("A buffer has no scalar value.");
        }
        else
        {
            if (!elementType.IsScalarCapable()) throw new ArgumentException("Scalars are 32- or 64-bit integers or floats.", nameof(elementType));
            if ((value is null) == (constant is null)) throw new ArgumentException("A scalar needs exactly one of an extent or a constant.");
            if (value is not null && elementType.IsFloating()) throw new ArgumentException("Floating scalars take a constant.", nameof(value));
            if (constant is { } c && (!double.IsFinite(c) || (!elementType.IsFloating() && c != Math.Floor(c))))
                throw new ArgumentException("A scalar constant must be finite, and integral for integer types.", nameof(constant));
            if (length is not null || distribution is not null || tolerance is not null) throw new ArgumentException("A scalar has no buffer settings.");
            Value = value;
            Constant = constant;
        }
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; }
    /// <summary>Gets whether this is a buffer or a scalar.</summary>
    public PtxParameterKind Kind { get; }
    /// <summary>Gets the element or scalar type.</summary>
    public PtxElementType ElementType { get; }
    /// <summary>Gets a buffer's role.</summary>
    public PtxBufferRole Role { get; }
    /// <summary>Gets a buffer's element count, or <c>null</c> for a scalar.</summary>
    public PtxExtent? Length { get; }
    /// <summary>Gets how an input buffer is filled, or <c>null</c>.</summary>
    public PtxValueDistribution? Distribution { get; }
    /// <summary>Gets an output buffer's tolerance override, or <c>null</c>.</summary>
    public PtxTolerance? Tolerance { get; }
    /// <summary>Gets an integer scalar's value as an extent, or <c>null</c>.</summary>
    public PtxExtent? Value { get; }
    /// <summary>Gets a scalar's constant value, or <c>null</c>.</summary>
    public double? Constant { get; }

    /// <summary>A read-only buffer.</summary>
    /// <param name="name">The name.</param>
    /// <param name="elementType">The element type.</param>
    /// <param name="length">The element count.</param>
    /// <param name="distribution">How it is filled; defaults to uniform [-1, 1).</param>
    /// <returns>The parameter.</returns>
    public static PtxKernelParameter Input(string name, PtxElementType elementType, PtxExtent length, PtxValueDistribution? distribution = null) =>
        new(name, PtxParameterKind.Buffer, elementType, PtxBufferRole.Input, length, distribution, null, null, null);

    /// <summary>A write-only buffer compared against the reference.</summary>
    /// <param name="name">The name.</param>
    /// <param name="elementType">The element type.</param>
    /// <param name="length">The element count.</param>
    /// <param name="tolerance">A tolerance override.</param>
    /// <returns>The parameter.</returns>
    public static PtxKernelParameter Output(string name, PtxElementType elementType, PtxExtent length, PtxTolerance? tolerance = null) =>
        new(name, PtxParameterKind.Buffer, elementType, PtxBufferRole.Output, length, null, tolerance, null, null);

    /// <summary>A buffer updated in place and compared against the reference.</summary>
    /// <param name="name">The name.</param>
    /// <param name="elementType">The element type.</param>
    /// <param name="length">The element count.</param>
    /// <param name="distribution">How it is filled.</param>
    /// <param name="tolerance">A tolerance override.</param>
    /// <returns>The parameter.</returns>
    public static PtxKernelParameter InPlace(string name, PtxElementType elementType, PtxExtent length,
        PtxValueDistribution? distribution = null, PtxTolerance? tolerance = null) =>
        new(name, PtxParameterKind.Buffer, elementType, PtxBufferRole.InputOutput, length, distribution, tolerance, null, null);

    /// <summary>An integer scalar computed from the shape, such as an element count.</summary>
    /// <param name="name">The name.</param>
    /// <param name="elementType">An integer type.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter.</returns>
    public static PtxKernelParameter Scalar(string name, PtxElementType elementType, PtxExtent value) =>
        new(name, PtxParameterKind.Scalar, elementType, PtxBufferRole.Input, null, null, null, value, null);

    /// <summary>A constant scalar, such as an epsilon.</summary>
    /// <param name="name">The name.</param>
    /// <param name="elementType">The scalar type.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter.</returns>
    public static PtxKernelParameter ScalarConstant(string name, PtxElementType elementType, double value) =>
        new(name, PtxParameterKind.Scalar, elementType, PtxBufferRole.Input, null, null, null, null, value);

    internal bool IsOutput => Kind == PtxParameterKind.Buffer && Role != PtxBufferRole.Input;
    internal bool IsInput => Kind == PtxParameterKind.Buffer && Role != PtxBufferRole.Output;

    internal double ScalarValue(IReadOnlyDictionary<string, long> symbols) =>
        Constant ?? (Value ?? throw new InvalidOperationException("Not a scalar.")).Evaluate(symbols);
}

/// <summary>A named shape dimension and the range shape fuzzing draws it from.</summary>
public sealed class PtxShapeSymbol
{
    /// <summary>Creates a symbol.</summary>
    /// <param name="name">An identifier; not a reserved block symbol.</param>
    /// <param name="minimum">The smallest value fuzzing uses, at least 1; use 1 to cover degenerate dimensions.</param>
    /// <param name="maximum">The largest value fuzzing uses.</param>
    /// <param name="tileSizes">Tile or block widths whose boundaries are probed (t - 1, t, t + 1, 2t + 1).</param>
    /// <exception cref="ArgumentException">The name or range is invalid.</exception>
    [JsonConstructor]
    public PtxShapeSymbol(string name, long minimum, long maximum, IReadOnlyList<long>? tileSizes = null)
    {
        if (!PtxNames.IsIdentifier(name) || name is PtxExtent.BlockX or PtxExtent.BlockY or PtxExtent.BlockZ)
            throw new ArgumentException("A shape symbol needs a non-reserved identifier.", nameof(name));
        if (minimum < 1 || maximum < minimum) throw new ArgumentException("A shape symbol needs 1 <= minimum <= maximum.");
        TileSizes = Array.AsReadOnly((tileSizes ?? Array.Empty<long>()).ToArray());
        if (TileSizes.Count > 8 || TileSizes.Any(size => size < 1)) throw new ArgumentException("At most eight positive tile sizes.", nameof(tileSizes));
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; }
    /// <summary>Gets the smallest fuzzed value.</summary>
    public long Minimum { get; }
    /// <summary>Gets the largest fuzzed value.</summary>
    public long Maximum { get; }
    /// <summary>Gets the tile widths whose boundaries are probed.</summary>
    public IReadOnlyList<long> TileSizes { get; }
}

/// <summary>A kernel's launch: grid dimensions as extents, block dimensions, and dynamic shared memory.</summary>
public sealed class PtxLaunchConfiguration
{
    /// <summary>Creates a launch configuration.</summary>
    /// <param name="gridX">Grid x as an extent, typically <c>ceil(N / blockX)</c>.</param>
    /// <param name="gridY">Grid y, or <c>null</c> for 1.</param>
    /// <param name="gridZ">Grid z, or <c>null</c> for 1.</param>
    /// <param name="blockX">Threads per block in x.</param>
    /// <param name="blockY">Threads per block in y.</param>
    /// <param name="blockZ">Threads per block in z.</param>
    /// <param name="dynamicSharedMemoryBytes">Dynamic shared memory per block, or <c>null</c> for none.</param>
    /// <exception cref="ArgumentException">A block dimension is out of range.</exception>
    [JsonConstructor]
    public PtxLaunchConfiguration(PtxExtent gridX, PtxExtent? gridY, PtxExtent? gridZ, int blockX, int blockY, int blockZ,
        PtxExtent? dynamicSharedMemoryBytes)
    {
        GridX = gridX ?? throw new ArgumentNullException(nameof(gridX));
        GridY = gridY ?? PtxExtent.Constant(1);
        GridZ = gridZ ?? PtxExtent.Constant(1);
        if (blockX < 1 || blockY < 1 || blockZ < 1 || (long)blockX * blockY * blockZ > 1024 || blockZ > 64)
            throw new ArgumentException("A block needs 1 to 1024 threads with z at most 64.");
        BlockX = blockX;
        BlockY = blockY;
        BlockZ = blockZ;
        DynamicSharedMemoryBytes = dynamicSharedMemoryBytes ?? PtxExtent.Constant(0);
    }

    /// <summary>Gets grid x.</summary>
    public PtxExtent GridX { get; }
    /// <summary>Gets grid y.</summary>
    public PtxExtent GridY { get; }
    /// <summary>Gets grid z.</summary>
    public PtxExtent GridZ { get; }
    /// <summary>Gets threads per block in x.</summary>
    public int BlockX { get; }
    /// <summary>Gets threads per block in y.</summary>
    public int BlockY { get; }
    /// <summary>Gets threads per block in z.</summary>
    public int BlockZ { get; }
    /// <summary>Gets dynamic shared memory per block, in bytes.</summary>
    public PtxExtent DynamicSharedMemoryBytes { get; }

    /// <summary>Gets the threads in one block.</summary>
    [JsonIgnore]
    public int ThreadsPerBlock => BlockX * BlockY * BlockZ;

    /// <summary>Returns this launch with different block dimensions; grid extents that name the block symbols follow.</summary>
    /// <param name="blockX">Threads in x.</param>
    /// <param name="blockY">Threads in y.</param>
    /// <param name="blockZ">Threads in z.</param>
    /// <returns>The new configuration.</returns>
    public PtxLaunchConfiguration WithBlock(int blockX, int blockY, int blockZ) =>
        new(GridX, GridY, GridZ, blockX, blockY, blockZ, DynamicSharedMemoryBytes);

    internal Dictionary<string, long> WithBlockSymbols(IReadOnlyDictionary<string, long> shape)
    {
        var symbols = new Dictionary<string, long>(shape, StringComparer.Ordinal)
        {
            [PtxExtent.BlockX] = BlockX,
            [PtxExtent.BlockY] = BlockY,
            [PtxExtent.BlockZ] = BlockZ
        };
        return symbols;
    }

    internal PtxWorkerLaunch Resolve(IReadOnlyDictionary<string, long> shape)
    {
        Dictionary<string, long> symbols = WithBlockSymbols(shape);
        uint Dimension(PtxExtent extent, long limit)
        {
            long value = extent.Evaluate(symbols);
            if (value < 1 || value > limit) throw new ArgumentException("A grid dimension " + extent + " = " + value + " is outside [1, " + limit + "].");
            return (uint)value;
        }
        long shared = DynamicSharedMemoryBytes.Evaluate(symbols);
        if (shared < 0 || shared > 1024 * 1024) throw new ArgumentException("Dynamic shared memory is outside [0, 1 MiB].");
        return new PtxWorkerLaunch
        {
            GridX = Dimension(GridX, int.MaxValue),
            GridY = Dimension(GridY, 65535),
            GridZ = Dimension(GridZ, 65535),
            BlockX = (uint)BlockX,
            BlockY = (uint)BlockY,
            BlockZ = (uint)BlockZ,
            SharedBytes = (uint)shared
        };
    }
}

/// <summary>The streaming-multiprocessor target and the resource limits a candidate must respect.</summary>
public sealed class PtxTargetLimits
{
    /// <summary>Creates limits.</summary>
    /// <param name="smVersion">The compute capability as major * 10 + minor, such as 75.</param>
    /// <param name="maxRegistersPerThread">The most registers one thread may use, at most 255.</param>
    /// <param name="maxSharedMemoryPerBlockBytes">The most static plus dynamic shared memory one block may use.</param>
    /// <param name="maxThreadsPerBlock">The most threads one block may have.</param>
    /// <param name="maxLocalBytesPerThread">The most local (spill) memory one thread may use; 0 forbids spills.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    [JsonConstructor]
    public PtxTargetLimits(int smVersion, int maxRegistersPerThread, int maxSharedMemoryPerBlockBytes, int maxThreadsPerBlock,
        int maxLocalBytesPerThread)
    {
        if (smVersion is < 50 or > 200) throw new ArgumentOutOfRangeException(nameof(smVersion));
        if (maxRegistersPerThread is < 16 or > 255) throw new ArgumentOutOfRangeException(nameof(maxRegistersPerThread));
        if (maxSharedMemoryPerBlockBytes is < 0 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxSharedMemoryPerBlockBytes));
        if (maxThreadsPerBlock is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxThreadsPerBlock));
        if (maxLocalBytesPerThread is < 0 or > 512 * 1024) throw new ArgumentOutOfRangeException(nameof(maxLocalBytesPerThread));
        SmVersion = smVersion;
        MaxRegistersPerThread = maxRegistersPerThread;
        MaxSharedMemoryPerBlockBytes = maxSharedMemoryPerBlockBytes;
        MaxThreadsPerBlock = maxThreadsPerBlock;
        MaxLocalBytesPerThread = maxLocalBytesPerThread;
    }

    /// <summary>Gets the compute capability as major * 10 + minor.</summary>
    public int SmVersion { get; }
    /// <summary>Gets the register limit per thread.</summary>
    public int MaxRegistersPerThread { get; }
    /// <summary>Gets the shared memory limit per block.</summary>
    public int MaxSharedMemoryPerBlockBytes { get; }
    /// <summary>Gets the thread limit per block.</summary>
    public int MaxThreadsPerBlock { get; }
    /// <summary>Gets the local memory limit per thread.</summary>
    public int MaxLocalBytesPerThread { get; }

    /// <summary>Gets the PTX target name, such as <c>sm_75</c>.</summary>
    [JsonIgnore]
    public string TargetName => "sm_" + SmVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Architectural limits for a compute capability, with spills forbidden.</summary>
    /// <param name="smVersion">The compute capability as major * 10 + minor; 75 is the GTX 1660 Ti / Turing.</param>
    /// <returns>The limits: 255 registers, 1024 threads, and the architecture's opt-in shared memory per block.</returns>
    public static PtxTargetLimits ForSm(int smVersion)
    {
        int shared = smVersion switch
        {
            < 70 => 48 * 1024,
            70 or 72 => 96 * 1024,
            75 => 64 * 1024,
            80 or 87 => 163 * 1024,
            86 or 89 => 99 * 1024,
            _ => 227 * 1024
        };
        return new PtxTargetLimits(smVersion, 255, shared, 1024, 0);
    }
}

/// <summary>One concrete shape the kernel is checked or timed on.</summary>
public sealed class PtxShapeCase
{
    /// <summary>Creates a case.</summary>
    /// <param name="label">Why the case exists, such as <c>fixed</c>, <c>degenerate</c> or <c>tile-edge</c>.</param>
    /// <param name="symbols">A value for every contract symbol.</param>
    public PtxShapeCase(string label, IReadOnlyDictionary<string, long> symbols)
    {
        Label = string.IsNullOrWhiteSpace(label) ? throw new ArgumentException("A label is required.", nameof(label)) : label;
        Symbols = new SortedDictionary<string, long>(
            (symbols ?? throw new ArgumentNullException(nameof(symbols))).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>Gets why the case exists.</summary>
    public string Label { get; }
    /// <summary>Gets the symbol values, ordered by name.</summary>
    public IReadOnlyDictionary<string, long> Symbols { get; }

    /// <summary>Formats the case, such as <c>tile-edge(C=257,N=3)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Label + "(" + string.Join(",", Symbols.Select(p => p.Key + "=" + p.Value)) + ")";
}