using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxContractTests
{
    [Fact]
    public void Extents_round_up_and_follow_the_block_size()
    {
        PtxExtent grid = PtxExtent.Of("N").CeilDiv(PtxExtent.BlockX);
        Assert.Equal(4, grid.Evaluate(new Dictionary<string, long> { ["N"] = 1000, [PtxExtent.BlockX] = 256 }));
        Assert.Equal(8, grid.Evaluate(new Dictionary<string, long> { ["N"] = 1000, [PtxExtent.BlockX] = 128 }));
        Assert.Equal(1, grid.Evaluate(new Dictionary<string, long> { ["N"] = 1, [PtxExtent.BlockX] = 256 }));
        Assert.Equal("ceil(N/blockX)", grid.ToString());
        Assert.Equal(12, PtxExtent.Of("N", "C").Times(2).Evaluate(new Dictionary<string, long> { ["N"] = 2, ["C"] = 3 }));
        Assert.Throws<ArgumentException>(() => grid.Evaluate(new Dictionary<string, long> { ["N"] = 5 }));
        Assert.Throws<OverflowException>(() => PtxExtent.Of("N", "N", "N").Evaluate(new Dictionary<string, long> { ["N"] = long.MaxValue / 2 }));
    }

    [Fact]
    public void A_contract_round_trips_through_canonical_json_with_a_stable_fingerprint()
    {
        PtxKernelContract contract = Axpy.Contract();
        string json = contract.ToJson();
        PtxKernelContract copy = PtxKernelContract.FromJson(json);
        Assert.Equal(json, copy.ToJson());
        Assert.Equal(contract.Fingerprint, copy.Fingerprint);
        Assert.Equal(ProgramSnapshot.Digest(json), contract.Fingerprint);
        Assert.Contains("\"entryPoint\":\"axpy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"Output\"", json, StringComparison.Ordinal);
        Assert.NotEqual(contract.Fingerprint, Axpy.Contract(fuzzCases: 9).Fingerprint);
    }

    [Fact]
    public void Inconsistent_contracts_are_refused()
    {
        PtxLaunchConfiguration launch = new(PtxExtent.Of("N").CeilDiv(PtxExtent.BlockX), null, null, 256, 1, 1, null);
        var symbols = new[] { new PtxShapeSymbol("N", 1, 64) };
        var shape = new Dictionary<string, long> { ["N"] = 8 };
        PtxKernelContract Make(PtxKernelParameter[] parameters, PtxLaunchConfiguration? l = null, IReadOnlyDictionary<string, long>? timing = null) =>
            new("k", "k", null, PtxTargetLimits.ForSm(75), parameters, symbols, l ?? launch, PtxTolerance.Exact, timing ?? shape, null, 0, 1);
        PtxKernelParameter output = PtxKernelParameter.Output("y", PtxElementType.Float32, PtxExtent.Of("N"));
        Assert.Throws<ArgumentException>(() => Make(new[] { PtxKernelParameter.Input("x", PtxElementType.Float32, PtxExtent.Of("N")) }));
        Assert.Throws<ArgumentException>(() => Make(new[] { output, PtxKernelParameter.Output("y", PtxElementType.Float32, PtxExtent.Of("N")) }));
        Assert.Throws<ArgumentException>(() => Make(new[] { PtxKernelParameter.Output("y", PtxElementType.Float32, PtxExtent.Of("M")) }));
        Assert.Throws<ArgumentException>(() => Make(new[] { PtxKernelParameter.Output("y", PtxElementType.Float32, PtxExtent.Of("N", PtxExtent.BlockX)) }));
        Assert.Throws<ArgumentException>(() => Make(new[] { output }, timing: new Dictionary<string, long> { ["N"] = 0 }));
        Assert.Throws<ArgumentException>(() => Make(new[] { output }, timing: new Dictionary<string, long> { ["N"] = 8, ["Q"] = 1 }));
        Assert.Throws<ArgumentException>(() => PtxKernelParameter.Scalar("eps", PtxElementType.Float32, PtxExtent.Of("N")));
        Assert.Throws<ArgumentException>(() => PtxKernelParameter.ScalarConstant("n", PtxElementType.Int32, 1.5));
        Assert.Throws<ArgumentException>(() => new PtxShapeSymbol(PtxExtent.BlockX, 1, 2));
        Assert.Throws<ArgumentException>(() => new PtxLaunchConfiguration(PtxExtent.Constant(1), null, null, 2048, 1, 1, null));
        Assert.NotNull(Make(new[] { output }));
    }

    [Fact]
    public void Validation_cases_cover_fixed_timing_degenerate_and_tile_boundary_shapes_deterministically()
    {
        PtxKernelContract contract = Axpy.Contract(fuzzCases: 16);
        IReadOnlyList<PtxShapeCase> cases = contract.GetValidationCases();
        Assert.Equal(cases.Select(c => c.ToString()), contract.GetValidationCases().Select(c => c.ToString()));
        Assert.Equal("fixed", cases[0].Label);
        Assert.Equal(1000, cases[0].Symbols["N"]);
        Assert.Equal("timing", cases[1].Label);
        Assert.Contains(cases, c => c.Label == "degenerate" && c.Symbols["N"] == 1);
        long[] values = cases.Select(c => c.Symbols["N"]).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Subset(values.ToHashSet(), new HashSet<long> { 1, 255, 256, 257, 513, 4096 });
        Assert.All(values, v => Assert.InRange(v, 1, 1 << 16));
        Assert.Equal(2, Axpy.Contract(fuzzCases: 0).GetValidationCases().Count);
    }

    [Fact]
    public void Seeded_inputs_are_identical_on_both_sides_of_the_process_boundary()
    {
        var buffer = new PtxWorkerBuffer { Elements = 1000, ElementType = PtxWorkerElementType.Float32, IsInput = true, Fill = PtxWorkerFillKind.Uniform, Minimum = -1, Maximum = 1, Seed = 42 };
        byte[] first = PtxDeterministicFill.Generate(buffer), second = PtxDeterministicFill.Generate(buffer);
        Assert.Equal(first, second);
        buffer.Seed = 43;
        Assert.NotEqual(first, PtxDeterministicFill.Generate(buffer));
        for (int i = 0; i < 1000; i++) Assert.InRange(BitConverter.ToSingle(first, i * 4), -1f, 1f);
        var integers = new PtxWorkerBuffer { Elements = 64, ElementType = PtxWorkerElementType.Int32, Fill = PtxWorkerFillKind.Integers, Minimum = 3, Maximum = 5, Seed = 1 };
        byte[] ints = PtxDeterministicFill.Generate(integers);
        for (int i = 0; i < 64; i++) Assert.InRange(BitConverter.ToInt32(ints, i * 4), 3, 5);
    }
}