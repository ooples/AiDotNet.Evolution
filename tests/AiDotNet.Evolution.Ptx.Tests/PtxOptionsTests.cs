using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

/// <summary>
/// Every option is bounded when a snapshot is taken, and every proposal option is part of the configuration hash, so
/// changing one can never reuse a proposal source (or cached work) made under the old value.
/// </summary>
public sealed class PtxOptionsTests
{
    private static PtxProgramEvolutionOptions Evolution() => new()
    {
        ModelVersionIdentity = "scripted-ptx-v1",
        AuditDirectory = Path.Combine(Path.GetTempPath(), "aidotnet-ptx-tests", "options"),
    };

    [Fact]
    public void Proposal_bounds_reject_values_outside_their_range_and_accept_the_edges()
    {
        AssertRange(o => o.MaxEdits = 0, o => o.MaxEdits = 1, o => o.MaxEdits = 256, o => o.MaxEdits = 257);
        AssertRange(o => o.MaxResponseChars = 255, o => o.MaxResponseChars = 256, o => o.MaxResponseChars = 524_288, o => o.MaxResponseChars = 524_289);
        AssertRange(o => o.MaxInputTokens = 0, o => o.MaxInputTokens = 1, o => o.MaxInputTokens = 2_097_152, o => o.MaxInputTokens = 2_097_153);
        AssertRange(o => o.MaxOutputTokens = 0, o => o.MaxOutputTokens = 1, o => o.MaxOutputTokens = 131_072, o => o.MaxOutputTokens = 131_073);
        AssertRange(o => o.Temperature = -0.01, o => o.Temperature = 0, o => o.Temperature = 2, o => o.Temperature = 2.01);

        var notFinite = Evolution();
        notFinite.Temperature = double.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => notFinite.Snapshot());

        static void AssertRange(Action<PtxProgramEvolutionOptions> below, Action<PtxProgramEvolutionOptions> low,
            Action<PtxProgramEvolutionOptions> high, Action<PtxProgramEvolutionOptions> above)
        {
            foreach (var reject in new[] { below, above })
            {
                var options = Evolution();
                reject(options);
                Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
            }
            foreach (var accept in new[] { low, high })
            {
                var options = Evolution();
                accept(options);
                options.Snapshot();
            }
        }
    }

    [Fact]
    public void Fixed_work_prices_must_be_positive_and_token_prices_nonnegative()
    {
        foreach (Action<PtxProgramEvolutionOptions> zero in new Action<PtxProgramEvolutionOptions>[]
                 {
                     o => o.SetupCostUnits = 0, o => o.ModelCallCostUnits = 0, o => o.CompilationCostUnits = 0,
                     o => o.ParseCostUnits = 0, o => o.AuditCostUnits = 0,
                 })
        {
            var options = Evolution();
            zero(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
        }

        foreach (Action<PtxProgramEvolutionOptions> negative in new Action<PtxProgramEvolutionOptions>[]
                 {
                     o => o.InputTokenCostUnits = -0.000001m, o => o.OutputTokenCostUnits = -0.000001m,
                 })
        {
            var options = Evolution();
            negative(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
        }

        var free = Evolution();
        free.InputTokenCostUnits = 0;
        free.OutputTokenCostUnits = 0;
        PtxProgramEvolutionOptions snapshot = free.Snapshot();
        Assert.Equal(0, snapshot.InputTokenCostUnits);
        Assert.Equal(0, snapshot.OutputTokenCostUnits);
    }

    [Fact]
    public void The_operator_id_is_bounded_and_printable()
    {
        var longId = Evolution();
        longId.Id = new string('k', 65);
        Assert.Throws<ArgumentException>(() => longId.Snapshot());

        var control = Evolution();
        control.Id = "ptx\u0001rewrite";
        Assert.Throws<ArgumentException>(() => control.Snapshot());

        var ok = Evolution();
        ok.Id = new string('k', 64);
        Assert.Equal(ok.Id, ok.Snapshot().Id);
    }

    [Fact]
    public void Every_proposal_option_changes_the_configuration_hash()
    {
        string baseline = Evolution().ConfigurationHash;
        var changes = new (string Name, Action<PtxProgramEvolutionOptions> Change)[]
        {
            ("Id", o => o.Id = "ptx-kernel-rewrite-b"),
            ("MaxEdits", o => o.MaxEdits = 31),
            ("MaxResponseChars", o => o.MaxResponseChars = 262_143),
            ("MaxInputTokens", o => o.MaxInputTokens = 131_071),
            ("MaxOutputTokens", o => o.MaxOutputTokens = 16_383),
            ("Temperature", o => o.Temperature = 0.31),
            ("SetupCostUnits", o => o.SetupCostUnits = 0.2m),
            ("ModelCallCostUnits", o => o.ModelCallCostUnits = 2m),
            ("InputTokenCostUnits", o => o.InputTokenCostUnits = 0.00003m),
            ("OutputTokenCostUnits", o => o.OutputTokenCostUnits = 0.00004m),
            ("CompilationCostUnits", o => o.CompilationCostUnits = 0.5m),
            ("ParseCostUnits", o => o.ParseCostUnits = 0.02m),
            ("AuditCostUnits", o => o.AuditCostUnits = 0.02m),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal) { baseline };
        foreach (var (name, change) in changes)
        {
            var options = Evolution();
            change(options);
            Assert.True(seen.Add(options.ConfigurationHash), name + " does not change the configuration hash");
        }
    }

    [Fact]
    public void Isolation_limits_reject_values_outside_their_range_and_keep_values_inside_it()
    {
        foreach (Action<PtxIsolationOptions> reject in new Action<PtxIsolationOptions>[]
                 {
                     o => o.DeviceOrdinal = -1, o => o.DeviceOrdinal = 64,
                     o => o.MaxDeviceBytes = (1 << 20) - 1, o => o.MaxDeviceBytes = (1L << 40) + 1,
                     o => o.MaxReadbackBytes = (1 << 10) - 1, o => o.MaxReadbackBytes = (4L << 30) + 1,
                     o => o.MaxResponseBytes = (1 << 16) - 1, o => o.MaxResponseBytes = (8L << 30) + 1,
                 })
        {
            var options = new PtxIsolationOptions();
            reject(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
        }

        var edges = new PtxIsolationOptions
        {
            DeviceOrdinal = 63,
            MaxDeviceBytes = 1L << 40,
            MaxReadbackBytes = 1 << 10,
            MaxResponseBytes = 1 << 16,
        };
        PtxIsolationOptions snapshot = edges.Snapshot();
        Assert.Equal(63, snapshot.DeviceOrdinal);
        Assert.Equal(1L << 40, snapshot.MaxDeviceBytes);
        Assert.Equal(1 << 10, snapshot.MaxReadbackBytes);
        Assert.Equal(1 << 16, snapshot.MaxResponseBytes);
    }

    [Fact]
    public void Timing_promotion_and_lock_settings_are_bounded()
    {
        foreach (Action<PtxTimingOptions> reject in new Action<PtxTimingOptions>[]
                 {
                     o => o.MinimumPromotionRatio = 0.99, o => o.MinimumPromotionRatio = double.PositiveInfinity,
                     o => o.MaximumP95LatencyRatio = 0, o => o.MaximumP95LatencyRatio = double.NaN,
                     o => o.MachineLockTimeout = TimeSpan.FromTicks(-1), o => o.MachineLockTimeout = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1),
                 })
        {
            var options = new PtxTimingOptions();
            reject(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
        }

        var edges = new PtxTimingOptions
        {
            MinimumPromotionRatio = 1,
            MaximumP95LatencyRatio = 0.5,
            MachineLockTimeout = TimeSpan.Zero,
        };
        PtxTimingOptions snapshot = edges.Snapshot();
        Assert.Equal(1, snapshot.MinimumPromotionRatio);
        Assert.Equal(0.5, snapshot.MaximumP95LatencyRatio);
        Assert.Equal(TimeSpan.Zero, snapshot.MachineLockTimeout);
    }
}
