using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxTimingTests
{
    [Fact]
    public void Statistics_report_the_median_quartiles_iqr_and_nearest_rank_p95()
    {
        PtxTimingStatistics odd = PtxTimingStatistics.FromSamples(new[] { 5.0, 1, 4, 2, 3 });
        Assert.Equal(3, odd.Median);
        Assert.Equal(2, odd.Q1);
        Assert.Equal(4, odd.Q3);
        Assert.Equal(2, odd.Iqr);
        Assert.Equal(5, odd.P95);
        Assert.Equal(new[] { 1.0, 2, 3, 4, 5 }, odd.Samples);
        PtxTimingStatistics even = PtxTimingStatistics.FromSamples(Enumerable.Range(1, 20).Select(i => (double)i));
        Assert.Equal(10.5, even.Median);
        Assert.Equal(19, even.P95);
        Assert.Equal(5.75, even.Q1, 10);
        Assert.Equal(15.25, even.Q3, 10);
        Assert.Throws<ArgumentException>(() => PtxTimingStatistics.FromSamples(new[] { 1.0, 2 }));
        Assert.Throws<ArgumentException>(() => PtxTimingStatistics.FromSamples(new[] { 1.0, 2, double.NaN }));
        Assert.Throws<ArgumentException>(() => PtxTimingStatistics.FromSamples(new[] { 1.0, 2, 0 }));
    }

    [Fact]
    public void Paired_evidence_applies_the_tensors_promotion_gate_including_the_noise_floor()
    {
        PtxPairedSample[] pairs = Enumerable.Range(0, 11).Select(i => new PtxPairedSample(1.0, 1.2 + i * 0.001)).ToArray();
        var evidence = new PtxPairedTimingEvidence(pairs, 1.02);
        Assert.InRange(evidence.MedianSpeedup, 1.204, 1.206);
        Assert.Equal(1.2, evidence.LowerSpeedupBound, 10);
        Assert.True(evidence.QualifiesForPromotion(1.05, 1.0));
        Assert.False(new PtxPairedTimingEvidence(pairs, 1.3).QualifiesForPromotion(1.05, 1.0));
        Assert.False(evidence.QualifiesForPromotion(1.3, 1.0));
        PtxPairedSample[] tailRegression = pairs.Take(10).Append(new PtxPairedSample(5.0, 1.2)).ToArray();
        Assert.False(new PtxPairedTimingEvidence(tailRegression, 1.0).QualifiesForPromotion(1.05, 1.0));
        double noise = PtxPairedTimingEvidence.NoiseRatio(Enumerable.Range(0, 20).Select(i => new PtxPairedSample(1.0, i == 19 ? 1.5 : 1.01)));
        Assert.Equal(1.01, noise, 10);
        Assert.Throws<ArgumentException>(() => new PtxPairedTimingEvidence(pairs.Take(6).ToArray(), 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtxPairedTimingEvidence(pairs, 0.9));
    }

    [Fact]
    public void The_timing_evaluator_requests_interleaved_pairs_and_builds_evidence_from_them()
    {
        var worker = new FakeWorkerTransport();
        var compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        var timing = new PtxTimingEvaluator(compiler, new PtxTimingOptions { MachineLockName = null, Pairs = 9, ControlPairs = 7, Warmup = 2, LaunchesPerSample = 4 });
        PtxTimingReport report = timing.Measure(Axpy.Block128, Axpy.Source);
        Assert.True(report.Completed, report.Message);
        Assert.True(report.QualifiesForPromotion, report.Message);
        Assert.Equal(9, report.Evidence?.Samples.Count);
        Assert.InRange(report.Evidence?.MedianSpeedup ?? 0, 1.2, 1.3);
        PtxWorkerRequest request = Assert.Single(worker.Requests);
        Assert.Equal(PtxWorkerOperation.Time, request.Operation);
        Assert.Equal(2, request.Kernels.Count);
        Assert.Equal(new uint[] { 128, 256 }, request.Cases[0].Launches.Select(l => l.BlockX));
        Assert.Equal(4, request.Timing?.LaunchesPerSample);
        Assert.Equal(1 << 16, request.Cases[0].Buffers[0].Elements);
        Assert.False(request.Cases[0].Buffers[2].IsOutput);
    }

    [Fact]
    public void A_regressing_or_noisy_candidate_is_measured_but_not_qualified()
    {
        var worker = new FakeWorkerTransport { Pair = i => (1.3, 1.25) };
        var timing = new PtxTimingEvaluator(new PtxProgramCompiler(Axpy.Contract(), worker), new PtxTimingOptions { MachineLockName = null });
        PtxTimingReport report = timing.Measure(Axpy.Block128, Axpy.Source);
        Assert.True(report.Completed);
        Assert.False(report.QualifiesForPromotion);
        worker.ExchangeStatus = PtxWorkerExchangeStatus.TimedOut;
        PtxTimingReport timedOut = timing.Measure(Axpy.Block128, Axpy.Source);
        Assert.False(timedOut.Completed);
        Assert.Null(timedOut.Evidence);
        Assert.Contains("deadline", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Timing_holds_the_machine_lock_and_the_lock_is_reentrant_on_its_thread()
    {
        string name = "AiDotNetPtxTest-" + Guid.NewGuid().ToString("N");
        var worker = new FakeWorkerTransport();
        var timing = new PtxTimingEvaluator(new PtxProgramCompiler(Axpy.Contract(), worker), new PtxTimingOptions { MachineLockName = name });
        bool heldDuringExchange = false;
        PtxTimingReport report = PtxMachineLock.Run(name, TimeSpan.FromSeconds(5), CancellationToken.None, _ =>
        {
            heldDuringExchange = true;
            return timing.Measure(Axpy.Block128, Axpy.Source);
        });
        Assert.True(heldDuringExchange);
        Assert.True(report.Completed, report.Message);
        Exception? contended = null;
        using (var mutex = new Mutex(false, name))
        {
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(5)));
            var other = new Thread(() =>
            {
                try { PtxMachineLock.Run(name, TimeSpan.FromMilliseconds(300), CancellationToken.None, _ => 0); }
                catch (TimeoutException exception) { contended = exception; }
            });
            other.Start();
            other.Join();
            mutex.ReleaseMutex();
        }
        Assert.IsType<TimeoutException>(contended);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtxTimingEvaluator(new PtxProgramCompiler(Axpy.Contract(), worker), new PtxTimingOptions { Pairs = 3 }));
    }
}