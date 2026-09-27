using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public class EvolutionHashFormatTests
{
    // The pre-optimization implementation, kept as the oracle: every identity and state hash depends on this exact output.
    private static string Reference(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var result = new StringBuilder();
        foreach (byte item in hash) result.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
    }

    [Fact]
    public void Compute_matches_the_sha256_test_vector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", EvolutionHash.Compute("abc"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", EvolutionHash.Compute(string.Empty));
    }

    [Fact]
    public void Compute_is_byte_identical_to_the_previous_encoding_for_varied_input()
    {
        var random = new Random(7);
        foreach (string value in new[] { "a", "ünïcödé ✓ \u0000 \uD83D\uDE00", new string('x', 10000) })
            Assert.Equal(Reference(value), EvolutionHash.Compute(value));
        for (int i = 0; i < 200; i++)
        {
            var chars = new char[random.Next(0, 300)];
            for (int j = 0; j < chars.Length; j++) chars[j] = (char)random.Next(32, 0xD7FF);
            string value = new(chars);
            Assert.Equal(Reference(value), EvolutionHash.Compute(value));
        }
    }
}
