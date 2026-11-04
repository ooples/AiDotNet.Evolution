using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

public sealed class ProgramSourceCompressionTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ShortSharedSpansRetainBothBoundaryLines(int count)
    {
        string[] lines = Enumerable.Range(0, count).Select(i => $"line_{i}: " + new string('x', 80)).ToArray();
        string source = string.Join("\n", lines);
        string compressed = ProgramSourceCompression.Compress(source, source, "current program");
        Assert.StartsWith(lines[0] + "\n", compressed);
        Assert.EndsWith("\n" + lines[count - 1], compressed);
        Assert.Contains($"{count - 2} lines unchanged", compressed);
        Assert.True(compressed.Length < source.Length);
    }

    [Fact]
    public void ReorderedLinesAreNotMarkedAsAnUnchangedSpan()
    {
        string[] lines = Enumerable.Range(0, 8).Select(i => $"line_{i}: " + new string('x', 80)).ToArray();
        string source = string.Join("\n", lines.Reverse());
        Assert.Equal(source, ProgramSourceCompression.Compress(string.Join("\n", lines), source, "current program"));
    }

    [Fact]
    public void TrailingWhitespaceChangesRemainVisible()
    {
        string[] lines = Enumerable.Range(0, 6).Select(i => $"line_{i}: " + new string('x', 80)).ToArray();
        string source = string.Join("\n", lines.Select(line => line + "  "));
        Assert.Equal(source, ProgramSourceCompression.Compress(string.Join("\n", lines), source, "existing code"));
    }

    [Fact]
    public void RepeatedLinesExhaustTheBudgetWithoutDroppingSource()
    {
        string source = string.Join("\n", Enumerable.Repeat("repeat", 10_000));
        Assert.Equal(source, ProgramSourceCompression.Compress(source, source, "existing code"));
    }

    [Fact]
    public void CompressionNeverExpandsAShortSnippet()
    {
        const string source = "a\nb\nc\nd\ne";
        Assert.Equal(source, ProgramSourceCompression.Compress(source, source, "current program"));
    }
}
