using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class EvolutionPersistentEvaluationTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("\\\\server\\share\\record")]
    public async Task Path_like_identity_fields_only_produce_hashed_sibling_files(string identity)
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionEvaluationStore(directory.Path);
        var key = Key(genome: identity, payload: identity, measurement: identity);
        var first = Record(key: key);

        Assert.Matches("^[0-9a-fA-F]{64}$", key.StableKey);
        Assert.True(await store.TryWriteAsync(first));
        string path = Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal(key.StableKey + ".json", Path.GetFileName(path));
        Assert.Equal(store.DirectoryPath, Path.GetDirectoryName(path));
        var restored = Assert.IsType<EvolutionEvaluationCacheRecord>(await store.ReadAsync(key));
        Assert.Equal(first.ToJson(), restored.ToJson());

        var refreshed = Record(key: key, sample: "new-sample", when: Observed.AddMinutes(1));
        Assert.True(await store.TryWriteAsync(refreshed));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.Path)));
        restored = Assert.IsType<EvolutionEvaluationCacheRecord>(await store.ReadAsync(key));
        Assert.Equal(refreshed.ToJson(), restored.ToJson());
    }

    [Fact]
    public async Task Canonical_directory_aliases_share_capacity_without_changing_the_owned_root()
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionEvaluationStore(directory.Path + Path.DirectorySeparatorChar, 1);
        var alias = new DirectoryEvolutionEvaluationStore(Path.Combine(directory.Path, "."), 1);

        Assert.Equal(store.DirectoryPath, alias.DirectoryPath);
        Assert.True(await store.TryWriteAsync(Record()));
        Assert.False(await alias.TryWriteAsync(Record(key: Key(genome: "other"))));
        Assert.Single(Directory.GetFiles(directory.Path));
    }
}
