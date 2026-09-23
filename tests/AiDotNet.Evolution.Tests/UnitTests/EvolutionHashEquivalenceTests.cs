// Disabled because one case passes a null component on purpose to check Combine's argument validation.
#nullable disable
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-20: the allocation-light hashing must stay bit-exact. Identities, checkpoints and state hashes persist these
/// digests, so every output is compared with the original algorithm (build the string, UTF-8, SHA-256, "x2").
/// </summary>
public sealed class EvolutionHashEquivalenceTests
{
    private static string ReferenceCompute(string value)
    {
        using SHA256 sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
    }

    private static string ReferenceCombine(IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        foreach (string value in values)
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        return ReferenceCompute(builder.ToString());
    }

    private static readonly string[] Edges =
    {
        "", "a", "ab;c", "12:x;", "😀", "\uD800", "\uDC00", "x\uD800", "\uDC00x", "\uD800𐀀", "é ü ß 中文",
        new string('z', 170), new string('z', 171), new string('€', 170), new string('€', 171), new string('q', 5000)
    };

    [Fact]
    public void Compute_matches_the_original_algorithm_on_edge_and_random_inputs()
    {
        foreach (string value in Edges.Concat(RandomStrings(2000, 7)))
            Assert.Equal(ReferenceCompute(value), EvolutionHash.Compute(value));
    }

    [Fact]
    public void Combine_matches_the_original_algorithm_including_boundaries_that_split_surrogates()
    {
        Assert.Equal(ReferenceCombine(Array.Empty<string>()), EvolutionHash.Combine(Array.Empty<string>()));
        // A pair split across components must hash as the two lone surrogates the concatenated string holds.
        Assert.Equal(ReferenceCombine(new[] { "x\uD83D", "\uDE00y" }), EvolutionHash.Combine(new[] { "x\uD83D", "\uDE00y" }));
        foreach (string edge in Edges)
            Assert.Equal(ReferenceCombine(new[] { edge, edge, "" }), EvolutionHash.Combine(new[] { edge, edge, "" }));
        var random = new Random(11);
        for (int trial = 0; trial < 500; trial++)
        {
            string[] parts = RandomStrings(random.Next(0, 40), trial).ToArray();
            Assert.Equal(ReferenceCombine(parts), EvolutionHash.Combine(parts));
        }
        string[] large = Enumerable.Range(0, 300).Select(i => new string((char)('a' + (i % 26)), 97 + i)).ToArray(); // grows the pooled buffer
        Assert.Equal(ReferenceCombine(large), EvolutionHash.Combine(large));
    }

    [Fact]
    public void CombineBytes_are_exactly_the_bytes_Combine_spells_in_hex()
    {
        var random = new Random(31);
        for (int trial = 0; trial < 300; trial++)
        {
            string[] parts = RandomStrings(random.Next(0, 30), 2000 + trial).ToArray();
            Assert.Equal(EvolutionHash.Combine(parts), string.Concat(EvolutionHash.CombineBytes(parts).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))));
        }
    }

    [Fact]
    public void Combine_enumerates_a_lazy_sequence_once_and_keeps_its_argument_checks()
    {
        int pulls = 0;
        IEnumerable<string> Lazy() { for (int i = 0; i < 5; i++) { pulls++; yield return "part" + i; } }
        Assert.Equal(ReferenceCombine(Enumerable.Range(0, 5).Select(i => "part" + i)), EvolutionHash.Combine(Lazy()));
        Assert.Equal(5, pulls);
        Assert.Equal("values", Assert.Throws<ArgumentException>(() => EvolutionHash.Combine(new[] { "a", null })).ParamName);
        Assert.Throws<ArgumentException>(() => EvolutionHash.Combine(Enumerable.Repeat("x", EvolutionCollectionLimits.MaximumHashComponents + 1)));
    }

    private static IEnumerable<string> RandomStrings(int count, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int length = random.Next(0, random.Next(2) == 0 ? 40 : 700);
            var chars = new char[length];
            for (int j = 0; j < length; j++)
            {
                int kind = random.Next(10);
                chars[j] = kind switch
                {
                    0 => (char)random.Next(0xD800, 0xE000),          // surrogates, paired or not
                    1 => (char)random.Next(0x80, 0x800),             // two-byte UTF-8
                    2 => (char)random.Next(0x800, 0xD800),           // three-byte UTF-8
                    _ => (char)random.Next(0x20, 0x7F)
                };
            }
            yield return new string(chars);
        }
    }
}
