// Migrated from ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d.
// Original license retained in AIDOTNET-LICENSE.txt.
using Xunit;
using static AiDotNet.Evolution.CSharp.Tests.CompilerTestSupport;
namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class CompilerOptionsMigrationTests
{
    [Theory]
    [InlineData("MaxSourceChars", 255)]
    [InlineData("MaxSourceChars", 65537)]
    [InlineData("MaxResponseChars", 255)]
    [InlineData("MaxResponseChars", 262145)]
    [InlineData("MaxRepairs", -1)]
    [InlineData("MaxRepairs", 8)]
    [InlineData("MaxEdits", 0)]
    [InlineData("MaxEdits", 17)]
    [InlineData("MaxCatalogNodes", 0)]
    [InlineData("MaxCatalogNodes", 65)]
    [InlineData("MaxInputTokens", 0)]
    [InlineData("MaxInputTokens", 1048577)]
    [InlineData("MaxOutputTokens", 0)]
    [InlineData("MaxOutputTokens", 65537)]
    [InlineData("CompilationTimeoutSeconds", 0)]
    [InlineData("CompilationTimeoutSeconds", 31)]
    public void Unsupported_work_bounds_are_rejected(string property, int value)
    {
        var options = Options();
        typeof(CSharpProgramEvolutionOptions).GetProperty(property)!.SetValue(options, value);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
    }

    [Fact]
    public void Identities_prices_paths_and_every_semantic_option_are_validated_and_fingerprinted()
    {
        var options = Options();
        string baseline = options.ConfigurationHash;
        foreach (var property in typeof(CSharpProgramEvolutionOptions).GetProperties())
        {
            var changed = options.Snapshot();
            if (property.PropertyType == typeof(int)) property.SetValue(changed, (int)property.GetValue(changed)! + 1);
            else if (property.PropertyType == typeof(decimal)) property.SetValue(changed, (decimal)property.GetValue(changed)! + 0.001m);
            else if (property.PropertyType == typeof(string) && property.Name != "AuditDirectory") property.SetValue(changed, (string)property.GetValue(changed)! + "changed");
            else continue; // Audit location is operational, reference content is fingerprinted by the compiler.
            Assert.NotEqual(baseline, changed.ConfigurationHash);
        }
        foreach (string name in new[] { "Id", "TargetIdentity", "ModelVersionIdentity", "CostUnitVersionHash" })
            foreach (string invalid in new[] { "", " ", "bad\n", new string('x', 257), "bad" + '\ud800' })
            {
                var changed = Options();
                typeof(CSharpProgramEvolutionOptions).GetProperty(name)!.SetValue(changed, invalid);
                Assert.ThrowsAny<ArgumentException>(() => changed.Snapshot());
            }
        foreach (string name in new[] { "SetupCostUnits", "ModelCallCostUnits", "CompilationCostUnits", "ParseCostUnits", "AuditCostUnits", "InputTokenCostUnits", "OutputTokenCostUnits" })
        {
            var changed = Options();
            var property = typeof(CSharpProgramEvolutionOptions).GetProperty(name)!;
            property.SetValue(changed, -1m);
            Assert.Throws<ArgumentOutOfRangeException>(() => changed.Snapshot());
            property.SetValue(changed, 1_000_000_001m);
            Assert.Throws<ArgumentOutOfRangeException>(() => changed.Snapshot());
        }
        options.Id = new string('x', 65);
        Assert.Throws<ArgumentException>(() => options.Snapshot());
        options = Options(); options.AuditDirectory = " ";
        Assert.Throws<ArgumentException>(() => options.Snapshot());
        options = Options(); options.ReferencePaths = Array.Empty<string>();
        Assert.Throws<ArgumentException>(() => options.Snapshot());
        options.ReferencePaths = Enumerable.Repeat("path", 65).ToArray();
        Assert.Throws<ArgumentException>(() => options.Snapshot());
        options.ReferencePaths = new[] { " " };
        Assert.Throws<ArgumentException>(() => options.Snapshot());
    }

}
