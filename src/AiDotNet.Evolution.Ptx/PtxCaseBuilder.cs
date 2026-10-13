namespace AiDotNet.Evolution.Ptx;

/// <summary>Turns a contract shape into the worker's buffers, arguments and launches, and regenerates inputs on the host.</summary>
internal static class PtxCaseBuilder
{
    internal static PtxWorkerCase Build(PtxKernelContract contract, PtxShapeCase shape, int caseIndex, IEnumerable<PtxLaunchConfiguration> launches)
    {
        var item = new PtxWorkerCase();
        for (int index = 0; index < contract.Parameters.Count; index++)
        {
            PtxKernelParameter parameter = contract.Parameters[index];
            if (parameter.Kind == PtxParameterKind.Buffer)
            {
                item.Arguments.Add(new PtxWorkerArgument { BufferIndex = item.Buffers.Count });
                item.Buffers.Add(Buffer(contract, parameter, index, shape, caseIndex));
            }
            else
            {
                item.Arguments.Add(new PtxWorkerArgument { ScalarBase64 = Convert.ToBase64String(ScalarBytes(parameter, shape.Symbols)) });
            }
        }
        foreach (PtxLaunchConfiguration launch in launches) item.Launches.Add(launch.Resolve(shape.Symbols));
        return item;
    }

    internal static PtxWorkerBuffer Buffer(PtxKernelContract contract, PtxKernelParameter parameter, int parameterIndex, PtxShapeCase shape, int caseIndex)
    {
        PtxValueDistribution distribution = parameter.Distribution ?? PtxValueDistribution.Constant(0);
        return new PtxWorkerBuffer
        {
            Elements = PtxKernelContract.BufferElements(parameter, shape.Symbols),
            ElementType = parameter.ElementType.ToWire(),
            IsInput = parameter.IsInput,
            IsOutput = parameter.IsOutput,
            Fill = (PtxWorkerFillKind)(int)distribution.Kind,
            Minimum = distribution.Minimum,
            Maximum = distribution.Maximum,
            Seed = Mix(contract.Seed, (ulong)caseIndex, (ulong)parameterIndex)
        };
    }

    internal static byte[] ScalarBytes(PtxKernelParameter parameter, IReadOnlyDictionary<string, long> symbols)
    {
        var bytes = new byte[parameter.ElementType.Size()];
        PtxDeterministicFill.Write(bytes, parameter.ElementType.ToWire(), parameter.ScalarValue(symbols));
        return bytes;
    }

    /// <summary>Runs the CPU reference on exactly the bytes the worker generates for the same case.</summary>
    internal static Dictionary<string, byte[]> RunReference(PtxKernelContract contract, IPtxKernelReference reference, PtxShapeCase shape, PtxWorkerCase item)
    {
        var buffers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int bufferIndex = 0;
        foreach (PtxKernelParameter parameter in contract.Parameters.Where(p => p.Kind == PtxParameterKind.Buffer))
        {
            PtxWorkerBuffer buffer = item.Buffers[bufferIndex++];
            buffers[parameter.Name] = buffer.IsInput
                ? PtxDeterministicFill.Generate(buffer)
                : new byte[checked((int)(buffer.Elements * PtxDeterministicFill.ElementSize(buffer.ElementType)))];
        }
        reference.Compute(new PtxReferenceInvocation(contract, shape, buffers));
        return buffers;
    }

    private static ulong Mix(ulong seed, ulong a, ulong b)
    {
        ulong z = seed ^ (a * 0x9E3779B97F4A7C15UL) ^ (b * 0xC2B2AE3D27D4EB4FUL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

}