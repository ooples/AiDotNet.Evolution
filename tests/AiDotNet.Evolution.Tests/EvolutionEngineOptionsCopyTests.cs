using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// <see cref="EvolutionEngineOptions.Copy"/> must carry <b>every</b> option, and must be reachable from
/// another assembly.
///
/// <para><b>Why this exists.</b> Before <c>Copy</c> was written, a consumer maintained its own hand-written
/// copy of these options and it silently dropped 19 of the 41 — so a run discarded its cascade, early
/// stopping, target quality, migration topology, selection policy and output directory without reporting
/// anything. Nothing failed; the run simply used defaults nobody chose.</para>
///
/// <para>A hand-maintained copy is wrong by construction: adding an option means remembering to add it in a
/// second place, and forgetting is silent. So the test below does not enumerate the options either — it
/// discovers them by reflection.</para>
///
/// <para><b>And it refuses to skip anything quietly.</b> An earlier version of this file used an allowlist of
/// probeable types, which silently omitted <see cref="EvolutionEngineOptions.Seed"/> because it is
/// <c>ulong</c> — the root determinism seed, whose loss would make runs unreproducible while every test still
/// passed. An allowlist that silently drops what it does not recognise reproduces the very defect this method
/// exists to prevent, so unprobeable options now FAIL rather than disappear.</para>
/// </summary>
public sealed class EvolutionEngineOptionsCopyTests
{
    /// <summary>
    /// Nested subsystems, excluded from value probing because <c>Copy</c> deep-copies rather than assigns
    /// them, so comparing by value would report a false drop.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than matched by shape. Their independence is asserted separately in
    /// <see cref="CopyDeepCopiesEveryNestedSubsystem"/>, so being excluded here does not mean untested.
    /// </remarks>
    private static readonly HashSet<string> NestedSubsystems = new(StringComparer.Ordinal)
    {
        nameof(EvolutionEngineOptions.Selection),
        nameof(EvolutionEngineOptions.Cascade),
        nameof(EvolutionEngineOptions.Artifacts),
        nameof(EvolutionEngineOptions.EarlyStopping),
        nameof(EvolutionEngineOptions.Pipeline),
    };

    [Fact]
    public void CopyCarriesEverySettableOption()
    {
        // Reflection rather than a written-out list, for the same reason Copy exists: any list maintained by
        // hand can be forgotten, and forgetting is silent.
        EvolutionEngineOptions options = new();
        List<PropertyInfo> settable = ValueOptions().ToList();
        Assert.NotEmpty(settable);

        foreach (PropertyInfo property in settable)
        {
            property.SetValue(options, DistinctValueFor(property));
        }

        EvolutionEngineOptions copy = options.Copy();

        List<string> dropped = settable
            .Where(property => !Equals(property.GetValue(copy), property.GetValue(options)))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dropped.Count == 0,
            "Copy must carry every option; these were lost, which is how a run silently reverts to defaults "
            + "nobody chose:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", dropped));
    }

    [Fact]
    public void EverySettableOptionIsEitherProbedOrAKnownNestedSubsystem()
    {
        // THE GUARD ON THE GUARD. CopyCarriesEverySettableOption can only check what it knows how to set, so
        // a new option of an unfamiliar type would be skipped in silence and its loss would go unnoticed -
        // which is exactly what happened to Seed (ulong) before this test existed.
        List<string> unprobeable = typeof(EvolutionEngineOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .Where(property => !NestedSubsystems.Contains(property.Name))
            .Where(property => !CanProbe(property.PropertyType))
            .Select(property => $"{property.Name} ({property.PropertyType.Name})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unprobeable.Count == 0,
            "these options cannot be probed by the copy test, so a Copy that dropped them would go unnoticed. "
            + "Add a probe value for the type, or list it as a nested subsystem and assert its independence:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", unprobeable));
    }

    [Fact]
    public void CopyDeepCopiesEveryNestedSubsystem()
    {
        // Changing a scalar afterwards only proves a value was assigned. A nested subsystem copied by
        // REFERENCE would still pass that, and a later edit through the original would then reach into the
        // options a run is already using - a shared-state defect that surfaces only under concurrency.
        EvolutionEngineOptions options = new();
        EvolutionEngineOptions copy = options.Copy();

        Assert.NotSame(options.Selection, copy.Selection);
        Assert.NotSame(options.Cascade, copy.Cascade);
        Assert.NotSame(options.Artifacts, copy.Artifacts);
        Assert.NotSame(options.EarlyStopping, copy.EarlyStopping);
        Assert.NotSame(options.Pipeline, copy.Pipeline);
        options.Pipeline.WaveSize = 7;
        Assert.Equal(16, copy.Pipeline.WaveSize);
    }

    [Fact]
    public void CopyIsReachableFromAnotherAssembly()
    {
        // The method was internal while the engine lived inside its only consumer. It is public now precisely
        // because that consumer moved out; if this ever reverts, the consumer's only options are to
        // hand-maintain a second copy — the defect above — or to lose the options entirely.
        MethodInfo? method = typeof(EvolutionEngineOptions).GetMethod(
            nameof(EvolutionEngineOptions.Copy), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(EvolutionEngineOptions), method!.ReturnType);
    }

    [Fact]
    public void CopyIsIndependentOfTheOriginal()
    {
        EvolutionEngineOptions options = new() { RunId = "original" };
        EvolutionEngineOptions copy = options.Copy();

        options.RunId = "changed-afterwards";

        Assert.Equal("original", copy.RunId);
    }

    /// <summary>Every public option this test can set a distinct value for.</summary>
    private static IEnumerable<PropertyInfo> ValueOptions() =>
        typeof(EvolutionEngineOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .Where(property => !NestedSubsystems.Contains(property.Name))
            .Where(property => CanProbe(property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal);

    private static bool CanProbe(Type type) =>
        type == typeof(string)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(double)
        || type == typeof(bool)
        || type == typeof(TimeSpan)
        || type.IsEnum
        || (Nullable.GetUnderlyingType(type) is Type inner && CanProbe(inner));

    /// <summary>A value distinguishable from the default, so a dropped option cannot coincidentally match.</summary>
    private static object DistinctValueFor(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string)) return "copy-probe-" + property.Name;
        if (type == typeof(int)) return 7;
        if (type == typeof(long)) return 7L;
        if (type == typeof(ulong)) return 7UL;
        if (type == typeof(double)) return 0.5d;
        if (type == typeof(bool)) return true;
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(7);

        if (type.IsEnum)
        {
            // The LAST declared value, not the first: the first is usually the default, so a dropped option
            // that reverted to its default would compare equal and the test would pass while broken.
            Array values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }

        throw new InvalidOperationException(
            $"No probe value for {type}. EverySettableOptionIsEitherProbedOrAKnownNestedSubsystem should have "
            + "caught this first.");
    }
}
