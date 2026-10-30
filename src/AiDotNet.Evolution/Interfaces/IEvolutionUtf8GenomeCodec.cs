namespace AiDotNet.Evolution;

/// <summary>A genome codec that can also read a payload from its UTF-8 bytes.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// A checkpoint stores each archived genome's payload as UTF-8 text. When the configured codec implements this
/// interface, a resume hands it those bytes directly instead of first building the payload as a string, which at
/// 10,000 archived genomes was about a fifth of everything a resume allocated (E-1). Codecs that do not implement it
/// keep receiving strings through <see cref="IEvolutionGenomeCodec{TGenome}.Deserialize(string)"/>.
/// </para>
/// <para>
/// Contract: for every payload <c>p</c>, <c>Deserialize(UTF8(p))</c> must return the same genome as
/// <c>Deserialize(p)</c>, or both must throw <see cref="System.IO.InvalidDataException"/>. Bytes that are not valid
/// UTF-8 must also raise <see cref="System.IO.InvalidDataException"/>. Like the string overload, this one is called
/// concurrently during a resume and must be safe to call in parallel.
/// </para>
/// <para><b>For Beginners:</b> Checkpoint files hold text as UTF-8 bytes, while .NET strings hold UTF-16. Converting
/// every saved genome to a string just to read it again costs memory and time on a large resume. Implement this only
/// if your codec can parse bytes directly; the plain string codec stays correct without it.</para>
/// </remarks>
public interface IEvolutionUtf8GenomeCodec<TGenome> : IEvolutionGenomeCodec<TGenome>
{
    /// <summary>Deserializes a genome from the UTF-8 encoding of a payload.</summary>
    /// <param name="utf8Payload">The UTF-8 bytes of text previously produced by
    /// <see cref="IEvolutionGenomeCodec{TGenome}.Serialize"/> under the same version hash.</param>
    /// <returns>The reconstructed genome; never <c>null</c>.</returns>
    /// <exception cref="System.IO.InvalidDataException">The payload cannot be decoded or is not valid UTF-8.</exception>
    TGenome Deserialize(ReadOnlySpan<byte> utf8Payload);
}