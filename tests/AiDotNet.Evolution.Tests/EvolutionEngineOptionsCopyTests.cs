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
/// discovers them by reflection, and an option added tomorrow is covered without anyone remembering.</para>
/// </summary>
public sealed class EvolutionEngineOptionsCopyTests
{
    [Fact]
    public void CopyCarriesEverySettableOption()
    {
        // Reflection rather than a written-out list, for the same reason Copy exists: any list maintained by
        // hand can be forgotten, and forgetting is exactly the failure this guards against.
        EvolutionEngineOptions options = new();
        List<PropertyInfo> settable = Settable().ToList();
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
        // A shallow copy would let a consumer's later edit reach back into options a run is already using,
        // which is the sort of shared-state defect that only shows itself under concurrency.
        EvolutionEngineOptions options = new() { RunId = "original" };
        EvolutionEngineOptions copy = options.Copy();

        options.RunId = "changed-afterwards";

        Assert.Equal("original", copy.RunId);
    }

    /// <summary>Every public option that can be both read and written.</summary>
    private static IEnumerable<PropertyInfo> Settable() =>
        typeof(EvolutionEngineOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .Where(property => SupportedType(property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal);

    /// <summary>
    /// Types this test can set a distinct value for.
    /// </summary>
    /// <remarks>
    /// Nested option objects are excluded because <c>Copy</c> deep-copies rather than assigns them, so
    /// reference comparison would report a false drop. Their contents are covered by their own tests.
    /// </remarks>
    private static bool SupportedType(Type type) =>
        type == typeof(string)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(double)
        || type == typeof(bool)
        || type == typeof(TimeSpan)
        || type.IsEnum
        || (Nullable.GetUnderlyingType(type) is Type inner && SupportedType(inner));

    /// <summary>A value distinguishable from the default, so a dropped option cannot coincidentally match.</summary>
    private static object DistinctValueFor(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string)) return "copy-probe-" + property.Name;
        if (type == typeof(int)) return 7;
        if (type == typeof(long)) return 7L;
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

        throw new InvalidOperationException($"No probe value for {type}.");
    }
}
