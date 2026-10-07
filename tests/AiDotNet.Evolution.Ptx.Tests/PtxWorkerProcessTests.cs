using System.Diagnostics;
using System.Text;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

/// <summary>The real isolated worker process. These run everywhere: none of them needs a GPU.</summary>
public sealed class PtxWorkerProcessTests
{
    private static PtxWorkerProcessTransport Transport(TimeSpan timeout) => new(new PtxIsolationOptions
    {
        Timeout = timeout,
        WorkerDirectory = Path.Combine(Path.GetTempPath(), "aidotnet-ptx-worker-tests")
    });

    [Fact]
    public void The_watchdog_kills_a_worker_that_never_answers()
    {
        PtxWorkerProcessTransport transport = Transport(TimeSpan.FromSeconds(2));
        var timer = Stopwatch.StartNew();
        // Input is left open, so the worker blocks reading its request forever: the same outcome as a kernel that spins.
        PtxWorkerExchange exchange = transport.Run(Encoding.UTF8.GetBytes("{\"Protocol\":1"), closeInput: false, CancellationToken.None);
        timer.Stop();
        Assert.Equal(PtxWorkerExchangeStatus.TimedOut, exchange.Status);
        Assert.Null(exchange.Response);
        Assert.InRange(timer.Elapsed.TotalSeconds, 1.9, 30);
        Assert.Contains("deadline", exchange.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_kills_the_worker_and_throws()
    {
        PtxWorkerProcessTransport transport = Transport(TimeSpan.FromSeconds(60));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var timer = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => transport.Run(Encoding.UTF8.GetBytes("{"), closeInput: false, cancel.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_malformed_request_gets_a_structured_refusal_from_the_real_worker()
    {
        PtxWorkerExchange exchange = Transport(TimeSpan.FromSeconds(60)).Run(Encoding.UTF8.GetBytes("this is not json"), closeInput: true, CancellationToken.None);
        Assert.Equal(PtxWorkerExchangeStatus.Completed, exchange.Status);
        Assert.Equal("invalid-request", exchange.Response?.Status);
        PtxWorkerExchange unsupported = Transport(TimeSpan.FromSeconds(60)).Exchange(new PtxWorkerRequest { Protocol = 99 }, CancellationToken.None);
        Assert.Equal("invalid-request", unsupported.Response?.Status);
    }

    [Fact]
    public void The_device_probe_reports_a_device_or_a_clear_reason_and_never_throws()
    {
        PtxDeviceProbe probe = PtxDeviceProbe.Run(new PtxIsolationOptions { Timeout = TimeSpan.FromSeconds(60) });
        if (probe.IsAvailable)
        {
            Assert.True(probe.Device?.SmVersion >= 50);
            Assert.Equal("ok", probe.Message);
        }
        else
        {
            Assert.Null(probe.Device);
            Assert.Matches("cuda-unavailable|no-device|CUDA_ERROR", probe.Message);
        }
    }

    [Fact]
    public void The_embedded_worker_is_written_once_and_a_tampered_copy_is_replaced()
    {
        string root = Path.Combine(Path.GetTempPath(), "aidotnet-ptx-worker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = PtxWorkerProcessTransport.ExtractWorker(root);
            byte[] original = File.ReadAllBytes(path);
            Assert.Equal(path, PtxWorkerProcessTransport.ExtractWorker(root));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Assert.Equal(path, PtxWorkerProcessTransport.ExtractWorker(root));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(File.Exists(Path.ChangeExtension(path, null) + ".runtimeconfig.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_dotnet_host_is_reported_as_unavailable()
    {
        var transport = new PtxWorkerProcessTransport(new PtxIsolationOptions { DotnetHostPath = Path.Combine(Path.GetTempPath(), "no-such-dotnet-host") });
        PtxWorkerExchange exchange = transport.Exchange(new PtxWorkerRequest { Operation = PtxWorkerOperation.Probe }, CancellationToken.None);
        Assert.Equal(PtxWorkerExchangeStatus.Unavailable, exchange.Status);
        Assert.False(PtxDeviceProbe.Run(new PtxIsolationOptions { DotnetHostPath = Path.Combine(Path.GetTempPath(), "no-such-dotnet-host") }).IsAvailable);
    }
}