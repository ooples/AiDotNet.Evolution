using System.Globalization;

namespace AiDotNet.Evolution.Programs;

/// <summary>When a program task re-reads its relative descriptors, such as <see cref="ProgramDiversityDescriptor"/>.</summary>
/// <remarks>
/// <para>
/// OpenEvolve's <c>diversity</c> axis measures a program against up to <c>diversity_reference_size</c> (20) programs
/// picked from the live database by a greedy max-min walk that starts at a random program. It rebuilds that set only
/// while it holds fewer than 20 programs, so in practice the set is drawn once, early, and then frozen, and nothing
/// already archived is re-filed when it changes. Both modes here pick the set deterministically: the walk starts at
/// the best archived program, and ties go to the ordinally smaller genome identifier. Both re-file every archive when
/// the set changes.
/// </para>
/// <para>
/// <see cref="ProgramDescriptorRebaseMode.Frozen"/> picks the set once. That happens at the first committed batch
/// after which the islands hold at least <see cref="ReferenceSize"/> distinct programs, or after which the run has
/// completed that many evaluations, whichever comes first.
/// <see cref="ProgramDescriptorRebaseMode.Periodic"/> also re-picks it every <see cref="Interval"/> completed
/// evaluations, so the axis keeps measuring against the population the search is in now. Until the first pick, the
/// references are the ones the descriptor was built with, typically the run's seeds.
/// </para>
/// <para><b>For Beginners:</b> A "how different is this program" axis needs something to compare against. These
/// options decide when that comparison set is chosen from the programs found so far: once, early (like OpenEvolve), or
/// again every so often as the search moves on.</para>
/// </remarks>
public sealed class ProgramDescriptorRebaseOptions
{
    /// <summary>OpenEvolve's default <c>diversity_reference_size</c>.</summary>
    public const int DefaultReferenceSize = 20;

    /// <summary>Gets or sets when the reference set is picked.</summary>
    public ProgramDescriptorRebaseMode Mode { get; set; } = ProgramDescriptorRebaseMode.Frozen;

    /// <summary>Gets or sets how many programs the reference set holds.</summary>
    public int ReferenceSize { get; set; } = DefaultReferenceSize;

    /// <summary>Gets or sets the completed evaluations between picks in <see cref="ProgramDescriptorRebaseMode.Periodic"/>
    /// mode; ignored when frozen.</summary>
    public int Interval { get; set; } = DefaultReferenceSize;

    /// <summary>Returns a validated copy.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public ProgramDescriptorRebaseOptions Validated()
    {
        if (!Enum.IsDefined(typeof(ProgramDescriptorRebaseMode), Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (ReferenceSize < 1 || ReferenceSize > 1024) throw new ArgumentOutOfRangeException(nameof(ReferenceSize), "Between 1 and 1024.");
        if (Interval < 1) throw new ArgumentOutOfRangeException(nameof(Interval));
        return new ProgramDescriptorRebaseOptions { Mode = Mode, ReferenceSize = ReferenceSize, Interval = Interval };
    }

    internal string Canonical() => Mode == ProgramDescriptorRebaseMode.Periodic
        ? "periodic|" + ReferenceSize.ToString(CultureInfo.InvariantCulture) + "|" + Interval.ToString(CultureInfo.InvariantCulture)
        : "frozen|" + ReferenceSize.ToString(CultureInfo.InvariantCulture);
}

/// <summary>When a relative descriptor's reference set is picked from the population.</summary>
public enum ProgramDescriptorRebaseMode
{
    /// <summary>Once, when the population first reaches the reference size; OpenEvolve's behaviour, made deterministic.</summary>
    Frozen = 0,
    /// <summary>At that point and again every <see cref="ProgramDescriptorRebaseOptions.Interval"/> evaluations.</summary>
    Periodic = 1
}
