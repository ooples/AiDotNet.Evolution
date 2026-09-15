#if NET10_0
using System.Text.Json;
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class FixedAnalysisCampaignTests
{
    private static object Request(uint[]? seeds = null, int budget = 8, string[]? methods = null) => new
    {
        RegistrationSha256 = new string('a', 64),
        SearchSeeds = seeds ?? new uint[] { 4000000001, 4000000002 },
        AnalysisPlan = new
        {
            SourceRevision = new string('b', 40),
            SeedCount = 2,
            Budget = budget,
            Tasks = new[] { new { Name = "Sphere", Scale = 1 } },
            Methods = methods ?? new[] { "RandomSearch", "HillClimb" },
            Protocol = "numeric-development-v3-diagonal-cma",
            Purpose = "retrospective-development"
        }
    };

    [Fact]
    public async Task ExecutesExactFreshSeedsAndRetainsPairedIdentityAndCounters()
    {
        string root = Path.Combine(Path.GetTempPath(), "evolution-fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "request.json"), output = Path.Combine(root, "result.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Request()));
            Assert.Equal(0, await FixedAnalysisCampaign.RunAsync(new[] { input, output }));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var rows = result.RootElement.GetProperty("Runs").EnumerateArray().ToArray();
            Assert.Equal(4, rows.Length);
            foreach (uint seed in new uint[] { 4000000001, 4000000002 })
            {
                var pair = rows.Where(r => r.GetProperty("Seed").GetUInt32() == seed).ToArray();
                Assert.Equal(2, pair.Length);
                Assert.Single(pair.Select(r => r.GetProperty("InitialPopulationHash").GetString()).Distinct());
                Assert.All(pair, row => Assert.Equal(8, row.GetProperty("EvaluatorCalls").GetInt32()));
            }
            byte[] before = await File.ReadAllBytesAsync(output);
            await Assert.ThrowsAsync<IOException>(() => FixedAnalysisCampaign.RunAsync(new[] { input, output }));
            Assert.Equal(before, await File.ReadAllBytesAsync(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("duplicate-seeds")]
    [InlineData("wrong-count")]
    [InlineData("over-budget")]
    [InlineData("unknown-method")]
    [InlineData("duplicate-json")]
    public async Task InvalidScheduleFailsBeforeCreatingOutput(string corruption)
    {
        string root = Path.Combine(Path.GetTempPath(), "evolution-fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            object request = corruption switch
            {
                "duplicate-seeds" => Request(new uint[] { 10, 10 }),
                "wrong-count" => Request(new uint[] { 10 }),
                "over-budget" => Request(budget: 1000000),
                "unknown-method" => Request(methods: new[] { "NoSuchMethod" }),
                _ => Request()
            };
            string input = Path.Combine(root, "request.json"), output = Path.Combine(root, "result.json");
            string json = JsonSerializer.Serialize(request);
            if (corruption == "duplicate-json") json = json.Replace("\"Budget\":8", "\"Budget\":8,\"Budget\":8");
            await File.WriteAllTextAsync(input, json);
            await Assert.ThrowsAsync<InvalidDataException>(() => FixedAnalysisCampaign.RunAsync(new[] { input, output }));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }
}
#endif
