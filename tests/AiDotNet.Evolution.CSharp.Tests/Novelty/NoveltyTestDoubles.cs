// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:tests/AiDotNet.Tests/UnitTests/Evolution/Programs/Novelty/NoveltyTestDoubles.cs
// Original license retained in Programs/Legacy/AIDOTNET-LICENSE.txt.
using AiDotNet.Evolution.Programs.Novelty;
using AiDotNet.Evolution.CSharp.Tests.ModelRuntime;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.CSharp.Tests.Novelty;

internal sealed class RecordingProgramFitnessEvaluator : IProgramFitnessEvaluator
{
    private readonly double _quality;
    private int _calls;

    public RecordingProgramFitnessEvaluator(double quality = 0.5) => _quality = quality;

    public string Id => "recording-evaluator";

    public string VersionHash => "recording-evaluator-v1";

    public int Calls => _calls;

    public List<string> SeenIds { get; } = new();

    public ValueTask<EvolutionTaskResult> EvaluateAsync(
        ProgramGenome candidate,
        EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        SeenIds.Add(candidate.Id);
        return new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
            EvolutionEvaluationStatus.Completed, _quality));
    }
}

internal sealed class ScriptedNoveltyJudge : IProgramNoveltyJudge
{
    private readonly ProgramNoveltyVerdict[] _verdicts;
    private int _calls;

    public ScriptedNoveltyJudge(params ProgramNoveltyVerdict[] verdicts) => _verdicts = verdicts;

    public string Id => "scripted-novelty-judge";
    public string VersionHash => "scripted-v1";

    public int Calls => _calls;

    public ValueTask<ProgramNoveltyVerdict> JudgeAsync(
        ProgramGenome candidate,
        ProgramGenome incumbent,
        CancellationToken cancellationToken = default)
    {
        int index = _calls;
        _calls++;
        ProgramNoveltyVerdict verdict = _verdicts.Length == 0
            ? ProgramNoveltyVerdict.Unavailable
            : _verdicts[Math.Min(index, _verdicts.Length - 1)];
        return new ValueTask<ProgramNoveltyVerdict>(verdict);
    }
}

internal sealed class UnavailableEmbeddingClient : IProgramEmbeddingClient
{
    private int _calls;

    public string ModelId => "unavailable-embedding";
    public string VersionHash => ModelId + "-v1";

    public int Calls => _calls;

    public ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        return new ValueTask<EmbeddingBatch>(EmbeddingBatch.Failure("the provider is unreachable in this test"));
    }
}

internal sealed class FlakyEmbeddingClient : IProgramEmbeddingClient
{
    private readonly DeterministicEmbeddingClient _inner = new(dimensions: 16);
    private int _remainingFailures;
    private int _calls;

    public FlakyEmbeddingClient(int failures) => _remainingFailures = failures;

    public string ModelId => "flaky-embedding";
    public string VersionHash => ModelId + "-v1";

    public int Calls => _calls;

    public ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            return new ValueTask<EmbeddingBatch>(EmbeddingBatch.Failure("transient"));
        }

        return _inner.EmbedAsync(texts, cancellationToken);
    }
}

internal sealed class ConstantEmbeddingClient : IProgramEmbeddingClient
{
    private int _calls;

    public string ModelId => "constant-embedding";
    public string VersionHash => ModelId + "-v1";

    public int Calls => _calls;

    public ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        var vectors = new List<EmbeddingVector>(texts.Count);
        for (int index = 0; index < texts.Count; index++)
        {
            vectors.Add(new EmbeddingVector(new[] { 1.0, 0.0, 0.0, 0.0 }));
        }

        return new ValueTask<EmbeddingBatch>(EmbeddingBatch.Success(vectors));
    }
}

internal sealed class OrthogonalEmbeddingClient : IProgramEmbeddingClient
{
    private int _nextAxis;
    private int _calls;
    private readonly Dictionary<string, EmbeddingVector> _assigned = new(StringComparer.Ordinal);

    public string ModelId => "orthogonal-embedding";
    public string VersionHash => ModelId + "-v1";

    public int Calls => _calls;

    public ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        var vectors = new List<EmbeddingVector>(texts.Count);
        foreach (string text in texts)
        {
            if (!_assigned.TryGetValue(text, out EmbeddingVector? vector))
            {
                var components = new double[32];
                components[_nextAxis % components.Length] = 1.0;
                _nextAxis++;
                vector = new EmbeddingVector(components);
                _assigned[text] = vector;
            }

            vectors.Add(vector);
        }

        return new ValueTask<EmbeddingBatch>(EmbeddingBatch.Success(vectors));
    }
}
