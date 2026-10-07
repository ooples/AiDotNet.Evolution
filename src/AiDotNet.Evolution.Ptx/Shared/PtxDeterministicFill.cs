// Compiled into both AiDotNet.Evolution.Ptx and AiDotNet.Evolution.Ptx.Worker: the worker fills device inputs and the host
// regenerates the identical bytes for its CPU reference, so the generator must be exactly the same code on both sides.
using System.Buffers.Binary;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Seeded, platform-independent buffer contents (SplitMix64; IEEE conversions only).</summary>
internal static class PtxDeterministicFill
{
    internal static int ElementSize(PtxWorkerElementType type) => type switch
    {
        PtxWorkerElementType.Float32 or PtxWorkerElementType.Int32 or PtxWorkerElementType.UInt32 => 4,
        PtxWorkerElementType.Float64 or PtxWorkerElementType.Int64 => 8,
        PtxWorkerElementType.Float16 => 2,
        PtxWorkerElementType.UInt8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static byte[] Generate(PtxWorkerBuffer buffer)
    {
        int size = ElementSize(buffer.ElementType);
        long bytes = checked(buffer.Elements * size);
        if (bytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(buffer), "A buffer exceeds 2 GiB.");
        var result = new byte[bytes];
        ulong state = buffer.Seed;
        for (long i = 0; i < buffer.Elements; i++)
        {
            double value = buffer.Fill switch
            {
                PtxWorkerFillKind.Constant => buffer.Minimum,
                PtxWorkerFillKind.Integers => Math.Floor(buffer.Minimum) +
                    (double)(Next(ref state) % (ulong)(Math.Floor(buffer.Maximum) - Math.Floor(buffer.Minimum) + 1)),
                _ => buffer.Minimum + (buffer.Maximum - buffer.Minimum) * ((Next(ref state) >> 11) * (1.0 / 9007199254740992.0))
            };
            Write(result.AsSpan(checked((int)(i * size)), size), buffer.ElementType, value);
        }
        return result;
    }

    internal static void Write(Span<byte> target, PtxWorkerElementType type, double value)
    {
        switch (type)
        {
            case PtxWorkerElementType.Float32: BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits((float)value)); break;
            case PtxWorkerElementType.Float64: BinaryPrimitives.WriteInt64LittleEndian(target, BitConverter.DoubleToInt64Bits(value)); break;
            case PtxWorkerElementType.Float16: BinaryPrimitives.WriteInt16LittleEndian(target, BitConverter.HalfToInt16Bits((Half)value)); break;
            case PtxWorkerElementType.Int32: BinaryPrimitives.WriteInt32LittleEndian(target, checked((int)value)); break;
            case PtxWorkerElementType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(target, checked((uint)value)); break;
            case PtxWorkerElementType.Int64: BinaryPrimitives.WriteInt64LittleEndian(target, checked((long)value)); break;
            case PtxWorkerElementType.UInt8: target[0] = checked((byte)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    internal static double Read(ReadOnlySpan<byte> source, PtxWorkerElementType type) => type switch
    {
        PtxWorkerElementType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source)),
        PtxWorkerElementType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source)),
        PtxWorkerElementType.Float16 => (double)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(source)),
        PtxWorkerElementType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(source),
        PtxWorkerElementType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(source),
        PtxWorkerElementType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(source),
        PtxWorkerElementType.UInt8 => source[0],
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static ulong Next(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
