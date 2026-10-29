using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNetTests.UnitTests.Evolution.Programs;

public sealed class ProgramGenomeTests
{
    [Fact]
    public void NormalizationUnifiesLineEndingsAndTrailingWhitespace()
    {
        var unix = new ProgramGenome("def solve():\n    return 1\n");
        var windows = new ProgramGenome("def solve():  \r\n    return 1\t\r\n\r\n");
        var oldMac = new ProgramGenome("def solve():\r    return 1\r");

        Assert.Equal("def solve():\n    return 1", unix.NormalizedSource);
        Assert.Equal(unix.NormalizedSource, windows.NormalizedSource);
        Assert.Equal(unix.NormalizedSource, oldMac.NormalizedSource);
        Assert.NotEqual(unix.Id, windows.Id);
        Assert.NotEqual(unix.Id, oldMac.Id);
    }

    [Fact]
    public void NormalizationPreservesLeadingIndentation()
    {
        var genome = new ProgramGenome("if x:\n        return 1\n");
        Assert.Equal("if x:\n        return 1", genome.NormalizedSource);
    }

    [Fact]
    public void ByteOrderMarkIsDisplayOnlyNormalization()
    {
        var withMark = new ProgramGenome("\uFEFFprint(1)");
        var without = new ProgramGenome("print(1)");
        Assert.Equal(without.NormalizedSource, withMark.NormalizedSource);
        Assert.NotEqual(without.Id, withMark.Id);
    }

    [Fact]
    public void IdIsStableLowercaseHexOverExactSourceAndLanguage()
    {
        var genome = new ProgramGenome("print(1)\n");
        Assert.Equal(64, genome.Id.Length);
        foreach (char character in genome.Id)
        {
            Assert.True((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'),
                "Identity must be lowercase hexadecimal.");
        }

        Assert.Equal(genome.Id, ProgramGenome.ComputeId(genome.Source));
        Assert.NotEqual(genome.Id, new ProgramGenome("print(1)").Id);
    }

    [Fact]
    public void IdIsUnchangedAcrossRepeatedConstruction()
    {
        const string Source = "class A:\n    def run(self):\n        return 42\n";
        string first = new ProgramGenome(Source).Id;
        for (int index = 0; index < 5; index++) Assert.Equal(first, new ProgramGenome(Source).Id);
    }

    [Fact]
    public void UnicodeSourcesAreSupportedAndDistinguished()
    {
        var accented = new ProgramGenome("caf\u00E9 = 1\n");
        var plain = new ProgramGenome("cafe = 1\n");
        var emoji = new ProgramGenome("x = \"\uD83D\uDE80\"\n");

        Assert.NotEqual(plain.Id, accented.Id);
        Assert.NotEqual(plain.Id, emoji.Id);
        Assert.Equal(accented.Id, new ProgramGenome("caf\u00E9 = 1\n").Id);
    }

    [Fact]
    public void ValueEqualityCoversSourceAndLanguageButNotTheDescription()
    {
        var first = new ProgramGenome("print(1)", ProgramLanguage.Python, "seed");
        var second = new ProgramGenome("print(1)", ProgramLanguage.Python, "seed");
        var otherLanguage = new ProgramGenome("print(1)", ProgramLanguage.Generic, "seed");
        var otherDescription = new ProgramGenome("print(1)", ProgramLanguage.Python, "child");

        Assert.True(first.Equals(second));
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first == otherLanguage);
        Assert.False(first != otherDescription);
        Assert.False(first.Equals(null));
    }

    [Fact]
    public void IdentityAgreesWithValueEqualityInBothDirections()
    {
        var genomes = new[]
        {
            new ProgramGenome("print(1)\n", ProgramLanguage.Python, "seed"),
            new ProgramGenome("print(1)", ProgramLanguage.Python, "child"),
            new ProgramGenome("print(1)", ProgramLanguage.Generic, "seed"),
            new ProgramGenome("print(2)", ProgramLanguage.Python, "seed"),
            new ProgramGenome("print(2)", ProgramLanguage.Generic, "seed")
        };

        foreach (ProgramGenome left in genomes)
        {
            foreach (ProgramGenome right in genomes)
            {
                Assert.Equal(left.Equals(right), string.Equals(left.Id, right.Id, StringComparison.Ordinal));
            }
        }

        Assert.Equal(5, genomes.Select(genome => genome.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(genomes[0].Id, ProgramGenome.ComputeId(genomes[0].Source, ProgramLanguage.Python));
        Assert.NotEqual(genomes[0].Id, ProgramGenome.ComputeId("print(1)"));
    }

    [Fact]
    public void WithSourceAndWithDescriptionProduceNewInstances()
    {
        var original = new ProgramGenome("print(1)", ProgramLanguage.Python, "seed");
        ProgramGenome rewritten = original.WithSource("print(2)");
        ProgramGenome described = original.WithDescription("child");

        Assert.Equal(ProgramLanguage.Python, rewritten.Language);
        Assert.Equal("seed", rewritten.Description);
        Assert.NotEqual(original.Id, rewritten.Id);
        Assert.Equal(original.Id, described.Id);
        Assert.Equal("child", described.Description);
        Assert.Equal("print(1)", original.Source);
    }

    [Fact]
    public void LineCountCountsNormalizedLines()
    {
        Assert.Equal(1, new ProgramGenome("a").LineCount);
        Assert.Equal(3, new ProgramGenome("a\r\nb\r\nc\r\n").LineCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData("\t \n \t")]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF \r\n\t")]
    [InlineData("\u00A0\u2028\r")]
    public void BlankSourcesAreRejected(string source) =>
        Assert.Throws<ArgumentException>(() => new ProgramGenome(source));

    // E-1 builds NormalizedSource on first read and decides blankness without it, so the decision must agree with the
    // normalized text for every source: accepted exactly when normalization leaves something.
    [Theory]
    [InlineData("\uFEFFx")]
    [InlineData(" \u00A0x\u00A0 ")]
    [InlineData("\r\n\tx")]
    [InlineData("\uFEFF\uFEFF")]
    [InlineData("x\r\n\r\n   ")]
    public void AcceptedSourcesNormalizeToTheSameNonEmptyTextOnEveryRead(string source)
    {
        var genome = new ProgramGenome(source, ProgramLanguage.Python);
        string normalized = genome.NormalizedSource;

        Assert.NotEmpty(normalized);
        Assert.Equal(ProgramText.Normalize(source), normalized);
        Assert.Same(normalized, genome.NormalizedSource);
        Assert.Same(normalized, genome.CreateOwnedSnapshot().NormalizedSource);
    }

    // E-1 computes Id on first read. A snapshot taken before anyone read it, and one taken after, must carry the identity
    // a freshly built genome computes, and a genome's identity must not change once read.
    [Fact]
    public void IdentityIsTheSameWhetherReadBeforeOrAfterASnapshot()
    {
        var unread = new ProgramGenome("print(1)\n", ProgramLanguage.Python);
        ProgramGenome snapshotBeforeRead = unread.CreateOwnedSnapshot();
        string expected = new ProgramGenome("print(1)\n", ProgramLanguage.Python).Id;

        Assert.Equal(expected, snapshotBeforeRead.Id);
        Assert.Equal(expected, unread.Id);
        Assert.Same(unread.Id, unread.Id);
        Assert.Equal(expected, unread.CreateOwnedSnapshot().Id);
        Assert.NotEqual(expected, new ProgramGenome("print(1)\n", ProgramLanguage.CSharp).Id);
    }

    [Fact]
    public void NullSourceIsRejected()
    {
#pragma warning disable CS8600, CS8625
        Assert.Throws<ArgumentNullException>(() => new ProgramGenome(null));
#pragma warning restore CS8600, CS8625
    }

    [Fact]
    public void HugeSourcesAreRejectedBeforeHashing()
    {
        string oversized = new string('x', ProgramGenome.MaxSourceLength + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramGenome(oversized));
    }

    [Fact]
    public void SourcesAtTheLimitAreAccepted()
    {
        string atLimit = new string('x', ProgramGenome.MaxSourceLength);
        var genome = new ProgramGenome(atLimit);
        Assert.Equal(ProgramGenome.MaxSourceLength, genome.NormalizedSource.Length);
    }

    [Fact]
    public void OversizedDescriptionsAreRejected()
    {
        string description = new string('d', ProgramGenome.MaxDescriptionLength + 1);
        Assert.Throws<ArgumentException>(() => new ProgramGenome("print(1)", ProgramLanguage.Python, description));
    }

    [Fact]
    public void UndefinedLanguageIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramGenome("print(1)", (ProgramLanguage)999));

    [Fact]
    public void ToStringDoesNotEchoSource()
    {
        var genome = new ProgramGenome("secret_token = 'abc'\n");
        string text = genome.ToString();
        Assert.DoesNotContain("secret_token", text, StringComparison.Ordinal);
        Assert.Contains(genome.Id.Substring(0, 12), text, StringComparison.Ordinal);
    }
}
