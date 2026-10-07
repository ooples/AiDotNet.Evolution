namespace AiDotNet.Evolution.Ptx;

/// <summary>The element type of a kernel buffer or scalar argument.</summary>
public enum PtxElementType
{
    /// <summary>IEEE 754 binary32 (<c>.f32</c>).</summary>
    Float32 = 0,
    /// <summary>IEEE 754 binary64 (<c>.f64</c>).</summary>
    Float64 = 1,
    /// <summary>IEEE 754 binary16 (<c>.f16</c>); buffers only.</summary>
    Float16 = 2,
    /// <summary>Signed 32-bit integer (<c>.s32</c>).</summary>
    Int32 = 3,
    /// <summary>Unsigned 32-bit integer (<c>.u32</c>).</summary>
    UInt32 = 4,
    /// <summary>Signed 64-bit integer (<c>.s64</c>).</summary>
    Int64 = 5,
    /// <summary>Unsigned 8-bit integer (<c>.u8</c>); buffers only.</summary>
    UInt8 = 6
}

/// <summary>Whether a kernel argument is a device buffer or a by-value scalar.</summary>
public enum PtxParameterKind
{
    /// <summary>A global-memory pointer to a buffer the evaluator allocates, fills and guards.</summary>
    Buffer = 0,
    /// <summary>A by-value scalar, such as a length or an epsilon.</summary>
    Scalar = 1
}

/// <summary>How a kernel uses a buffer.</summary>
public enum PtxBufferRole
{
    /// <summary>Read only: filled from its seeded distribution.</summary>
    Input = 0,
    /// <summary>Written only: starts from a sentinel and is compared against the reference.</summary>
    Output = 1,
    /// <summary>Read and written in place: filled from its distribution and compared against the reference.</summary>
    InputOutput = 2
}

/// <summary>How a buffer's seeded contents are drawn.</summary>
public enum PtxDistributionKind
{
    /// <summary>Uniform real values in [minimum, maximum).</summary>
    Uniform = 0,
    /// <summary>Uniform integers in [minimum, maximum].</summary>
    Integers = 1,
    /// <summary>Every element equals the minimum.</summary>
    Constant = 2
}

internal static class PtxElementTypes
{
    internal static PtxWorkerElementType ToWire(this PtxElementType type) => (PtxWorkerElementType)(int)type;
    internal static int Size(this PtxElementType type) => PtxDeterministicFill.ElementSize(type.ToWire());
    internal static bool IsFloating(this PtxElementType type) =>
        type is PtxElementType.Float32 or PtxElementType.Float64 or PtxElementType.Float16;
    internal static bool IsScalarCapable(this PtxElementType type) =>
        type is PtxElementType.Float32 or PtxElementType.Float64 or PtxElementType.Int32 or PtxElementType.UInt32 or PtxElementType.Int64;
}