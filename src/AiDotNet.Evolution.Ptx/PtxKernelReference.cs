using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace AiDotNet.Evolution.Ptx;

/// <summary>A trusted CPU implementation that defines a contract's right answer.</summary>
/// <remarks>When no CPU reference is supplied, the incumbent kernel is the reference instead: it runs on the same seeded
/// inputs in the same worker, and elements where its own two runs disagree are treated as unspecified.</remarks>
[Experimental("AIDEVO005")]
public interface IPtxKernelReference
{
    /// <summary>Gets a versioned identity; change it whenever the reference's results change.</summary>
    string Identity { get; }

    /// <summary>Computes every output buffer from the inputs.</summary>
    /// <param name="invocation">The shape, inputs and output buffers of one case.</param>
    void Compute(PtxReferenceInvocation invocation);
}

/// <summary>Creates references from delegates.</summary>
[Experimental("AIDEVO005")]
public static class PtxKernelReference
{
    /// <summary>Wraps a delegate as a reference.</summary>
    /// <param name="identity">A versioned identity, such as <c>layernorm-cpu-v1</c>.</param>
    /// <param name="compute">Fills the outputs.</param>
    /// <returns>The reference.</returns>
    public static IPtxKernelReference Create(string identity, Action<PtxReferenceInvocation> compute) =>
        new DelegateReference(string.IsNullOrWhiteSpace(identity) ? throw new ArgumentException("An identity is required.", nameof(identity)) : identity,
            compute ?? throw new ArgumentNullException(nameof(compute)));

    private sealed class DelegateReference(string identity, Action<PtxReferenceInvocation> compute) : IPtxKernelReference
    {
        public string Identity { get; } = identity;
        public void Compute(PtxReferenceInvocation invocation) => compute(invocation);
    }
}

/// <summary>The inputs, outputs and shape of one reference computation, as typed spans over contiguous buffers.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxReferenceInvocation
{
    private readonly PtxKernelContract _contract;
    private readonly Dictionary<string, byte[]> _buffers;

    internal PtxReferenceInvocation(PtxKernelContract contract, PtxShapeCase shape, Dictionary<string, byte[]> buffers)
    {
        _contract = contract;
        Shape = shape;
        _buffers = buffers;
    }

    /// <summary>Gets the shape.</summary>
    public PtxShapeCase Shape { get; }

    /// <summary>Gets a symbol's value.</summary>
    /// <param name="name">The symbol.</param>
    /// <returns>Its value in this case.</returns>
    public long Symbol(string name) => Shape.Symbols.TryGetValue(name, out long value) ? value : throw new ArgumentException("Unknown symbol '" + name + "'.", nameof(name));

    /// <summary>Gets a scalar argument's value.</summary>
    /// <param name="name">The scalar parameter.</param>
    /// <returns>Its value in this case.</returns>
    public double Scalar(string name)
    {
        PtxKernelParameter parameter = Find(name);
        if (parameter.Kind != PtxParameterKind.Scalar) throw new ArgumentException("'" + name + "' is not a scalar.", nameof(name));
        return parameter.ScalarValue(Shape.Symbols);
    }

    /// <summary>Gets an input or in-place buffer's seeded contents.</summary>
    /// <typeparam name="T">The element type: float, double, Half, int, uint, long or byte, matching the contract.</typeparam>
    /// <param name="name">The buffer parameter.</param>
    /// <returns>The contents.</returns>
    public ReadOnlySpan<T> Input<T>(string name) where T : unmanaged
    {
        PtxKernelParameter parameter = Find(name);
        if (!parameter.IsInput) throw new ArgumentException("'" + name + "' is not an input buffer.", nameof(name));
        return Typed<T>(parameter);
    }

    /// <summary>Gets an output or in-place buffer to fill; an in-place buffer starts with its input contents.</summary>
    /// <typeparam name="T">The element type, matching the contract.</typeparam>
    /// <param name="name">The buffer parameter.</param>
    /// <returns>The writable buffer.</returns>
    public Span<T> Output<T>(string name) where T : unmanaged
    {
        PtxKernelParameter parameter = Find(name);
        if (!parameter.IsOutput) throw new ArgumentException("'" + name + "' is not an output buffer.", nameof(name));
        return Typed<T>(parameter);
    }

    private Span<T> Typed<T>(PtxKernelParameter parameter) where T : unmanaged
    {
        Type expected = parameter.ElementType switch
        {
            PtxElementType.Float32 => typeof(float),
            PtxElementType.Float64 => typeof(double),
            PtxElementType.Float16 => typeof(Half),
            PtxElementType.Int32 => typeof(int),
            PtxElementType.UInt32 => typeof(uint),
            PtxElementType.Int64 => typeof(long),
            _ => typeof(byte)
        };
        if (typeof(T) != expected) throw new ArgumentException("'" + parameter.Name + "' holds " + expected.Name + ", not " + typeof(T).Name + ".");
        return MemoryMarshal.Cast<byte, T>(_buffers[parameter.Name].AsSpan());
    }

    private PtxKernelParameter Find(string name) =>
        _contract.Parameters.FirstOrDefault(p => p.Name == name) ?? throw new ArgumentException("Unknown parameter '" + name + "'.", nameof(name));
}