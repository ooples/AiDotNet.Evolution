using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// <see cref="ProgramProposalOptions.Clone"/> overrides the base clone by listing properties, so a property
/// added to <see cref="ProgramTaskOptions"/> is copied only if someone remembers to add it here too. One was
/// not: <c>MetricDescriptors</c>, which the CLI fills with every declared OpenEvolve feature dimension.
/// <see cref="ProgramEvolutionTask"/> snapshots its options through <c>Clone</c>, so the promotion list was
/// empty by the first evaluation while the archive still demanded those coordinates - every candidate was
/// refused with <c>descriptor_missing</c> and the run ended with no candidates after one evaluation.
/// </summary>
public sealed class ProposalOptionsCloneTests
{
    /// <summary>The base properties, listed so that adding one fails this test instead of a run.</summary>
    private static readonly string[] BaseWritableProperties =
    [
        "Diff", "EnforceEvolveBlocks", "EvolveBlockEndMarker", "EvolveBlockStartMarker",
        "Language", "MaxProgramChars", "MetricDescriptors", "ResourceAccounting"
    ];

    [Fact]
    public void The_base_property_list_this_test_covers_is_still_complete()
    {
        string[] writable = typeof(ProgramTaskOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Inverted on purpose: a new base property must be added to BaseWritableProperties and to the
        // assertions below, rather than being silently exempt from the clone check.
        Assert.Equal(BaseWritableProperties, writable);
    }

    [Fact]
    public void Cloning_proposal_options_keeps_every_base_property()
    {
        var ledger = new EvolutionResourceLedger("clone-test",
            new EvolutionResources(new Dictionary<string, decimal> { ["cost_units"] = 10 }));
        var options = new ProgramProposalOptions
        {
            ResourceAccounting = new ProgramEvolutionResourceOptions(ledger, 1, "cost-units-v1"),
            Language = ProgramLanguage.Python,
            EvolveBlockStartMarker = "# START",
            EvolveBlockEndMarker = "# END",
            EnforceEvolveBlocks = true,
            MaxProgramChars = 4321,
            TaskDescription = "pack circles",
            MetricDescriptors = { "radius_variance", "spatial_spread" }
        };
        options.Diff.SearchMarker = "<<<<<<< MINE";

        ProgramProposalOptions clone = options.Clone();

        Assert.Equal(ProgramLanguage.Python, clone.Language);
        Assert.Equal("# START", clone.EvolveBlockStartMarker);
        Assert.Equal("# END", clone.EvolveBlockEndMarker);
        Assert.True(clone.EnforceEvolveBlocks);
        Assert.Equal(4321, clone.MaxProgramChars);
        Assert.Equal("<<<<<<< MINE", clone.Diff.SearchMarker);
        // Asserted against a real ledger: Assert.Null here would also pass for a Clone that omitted the property,
        // which is the exact defect this test exists to catch. The same instance, not a copy - the ledger is shared
        // state that charges one budget, so a clone holding a duplicate would let two snapshots spend it twice.
        Assert.Same(ledger, clone.ResourceAccounting?.Ledger);
        Assert.Equal("cost-units-v1", clone.ResourceAccounting?.CostUnitVersionHash);
        Assert.Equal("pack circles", clone.TaskDescription);
        Assert.Equal(new[] { "radius_variance", "spatial_spread" }, clone.MetricDescriptors);

        // A copy, not the same list: a clone that shared it would let one run's options mutate another's.
        clone.MetricDescriptors.Add("added-to-the-clone");
        Assert.Equal(new[] { "radius_variance", "spatial_spread" }, options.MetricDescriptors);
    }

}
