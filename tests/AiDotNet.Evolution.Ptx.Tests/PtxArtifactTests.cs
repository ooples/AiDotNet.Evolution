using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxArtifactTests
{
    internal static PtxKernelArtifact Winner(out PtxProgramCompiler compiler)
    {
        var worker = new FakeWorkerTransport();
        compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        PtxCorrectnessReport correctness = new PtxCorrectnessEvaluator(compiler, Axpy.Reference, null).Evaluate(Axpy.Block128);
        var timing = new PtxTimingEvaluator(compiler, new PtxTimingOptions { MachineLockName = null });
        PtxTimingReport report = timing.Measure(Axpy.Block128, Axpy.Source);
        return PtxKernelArtifact.Create(compiler, correctness, report, Axpy.Source, timing.Identity);
    }

    [Fact]
    public void A_winner_round_trips_through_its_content_addressed_file()
    {
        PtxKernelArtifact artifact = Winner(out PtxProgramCompiler compiler);
        string directory = Path.Combine(Path.GetTempPath(), "ptx-artifact-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = artifact.Save(directory);
            Assert.Equal(path, artifact.Save(directory));
            Assert.Equal(artifact.ArtifactId + ".ptx-artifact.json", Path.GetFileName(path));
            PtxKernelArtifact loaded = PtxKernelArtifact.Load(path);
            Assert.Equal(artifact.ArtifactId, loaded.ArtifactId);
            Assert.Equal(Axpy.Block128, loaded.Ptx);
            Assert.Equal(75, loaded.SmVersion);
            Assert.Equal(128, loaded.Launch.BlockX);
            Assert.Equal(compiler.Contract.Fingerprint, loaded.ContractFingerprint);
            Assert.Equal(12, loaded.Resources.Registers);
            Assert.Equal(artifact.Evidence.MedianSpeedup, loaded.Evidence.MedianSpeedup);
            Assert.Equal(artifact.Evidence.Samples, loaded.Evidence.Samples);
            Assert.True(loaded.QualifiedForPromotion);
            Assert.Equal("axpy-cpu-v1", loaded.ReferenceIdentity);
            Assert.Equal(compiler.Contract.GetValidationCases().Count, loaded.CorrectnessCases);
            Assert.Contains("Simulated GPU|sm_75", loaded.DeviceIdentity, StringComparison.Ordinal);
            Assert.Equal(artifact.ToBytes(), loaded.ToBytes());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Tampered_or_renamed_artifacts_are_refused()
    {
        PtxKernelArtifact artifact = Winner(out _);
        byte[] bytes = artifact.ToBytes();
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<InvalidDataException>(() => PtxKernelArtifact.FromBytes(System.Text.Encoding.UTF8.GetBytes(json.Replace("fma.rn.f32", "fma.rz.f32", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PtxKernelArtifact.FromBytes(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PtxKernelArtifact.FromBytes(System.Text.Encoding.UTF8.GetBytes(json + " ")));
        Assert.Throws<InvalidDataException>(() => PtxKernelArtifact.FromBytes(Array.Empty<byte>()));
        string directory = Path.Combine(Path.GetTempPath(), "ptx-artifact-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = artifact.Save(directory);
            string renamed = Path.Combine(directory, new string('0', 64) + ".ptx-artifact.json");
            File.Copy(path, renamed);
            Assert.Throws<InvalidDataException>(() => PtxKernelArtifact.Load(renamed));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Only_verified_evidence_becomes_an_artifact()
    {
        var worker = new FakeWorkerTransport();
        var compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        var timing = new PtxTimingEvaluator(compiler, new PtxTimingOptions { MachineLockName = null });
        PtxTimingReport report = timing.Measure(Axpy.Block128, Axpy.Source);
        PtxCorrectnessReport failed = new PtxCorrectnessEvaluator(compiler, Axpy.Reference, null)
            .Evaluate(Axpy.Source + "\n" + FakeWorkerTransport.FaultMarker + SimulatedFault.WrongValue + "\n");
        Assert.Throws<ArgumentException>(() => PtxKernelArtifact.Create(compiler, failed, report, Axpy.Source, timing.Identity));
        worker.ExchangeStatus = PtxWorkerExchangeStatus.TimedOut;
        PtxTimingReport incomplete = timing.Measure(Axpy.Block128, Axpy.Source);
        worker.ExchangeStatus = PtxWorkerExchangeStatus.Completed;
        PtxCorrectnessReport passed = new PtxCorrectnessEvaluator(compiler, Axpy.Reference, null).Evaluate(Axpy.Block128);
        Assert.Throws<ArgumentException>(() => PtxKernelArtifact.Create(compiler, passed, incomplete, Axpy.Source, timing.Identity));
        PtxTimingReport otherCandidate = timing.Measure(Axpy.Source + "\n// another candidate\n", Axpy.Source);
        Assert.True(otherCandidate.Completed);
        Assert.Throws<ArgumentException>(() => PtxKernelArtifact.Create(compiler, passed, otherCandidate, Axpy.Source, timing.Identity));
        Assert.Throws<ArgumentException>(() => PtxKernelArtifact.Create(compiler, passed, report, Axpy.Wrong, timing.Identity));
        Assert.NotNull(PtxKernelArtifact.Create(compiler, passed, report, Axpy.Source, timing.Identity));
    }

    [Fact]
    public void The_configuration_codec_is_canonical_for_the_tensors_tuning_registry()
    {
        PtxKernelConfiguration configuration = Winner(out _).ToConfiguration();
        var codec = new PtxKernelConfigurationCodec();
        string payload = codec.Serialize(configuration);
        PtxKernelConfiguration decoded = codec.Deserialize(payload);
        Assert.Equal(configuration, decoded);
        Assert.Equal(payload, codec.Serialize(decoded));
        Assert.Equal(128, decoded.BlockX);
        Assert.Throws<InvalidDataException>(() => codec.Deserialize(payload.Replace("{", "{ ", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Deserialize("{}"));
        Assert.False(string.IsNullOrWhiteSpace(codec.Id));
        Assert.False(string.IsNullOrWhiteSpace(codec.VersionHash));
    }
}