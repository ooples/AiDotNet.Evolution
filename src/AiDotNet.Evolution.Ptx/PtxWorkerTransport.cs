using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Ptx;

/// <summary>How a worker invocation ended, before its response is interpreted.</summary>
internal enum PtxWorkerExchangeStatus
{
    /// <summary>The worker exited and returned a well-formed response (which may itself report a failure).</summary>
    Completed,
    /// <summary>The watchdog killed the worker at its deadline: a hung kernel or a stalled worker.</summary>
    TimedOut,
    /// <summary>The worker exited without a usable response.</summary>
    Crashed,
    /// <summary>The worker wrote more than the response bound and was killed.</summary>
    OutputTooLarge,
    /// <summary>The worker could not be started: no <c>dotnet</c> host or no embedded worker.</summary>
    Unavailable
}

internal sealed record PtxWorkerExchange(PtxWorkerExchangeStatus Status, PtxWorkerResponse? Response, int? ExitCode,
    string StandardError, TimeSpan Elapsed)
{
    /// <summary>A one-line explanation of a failed exchange, or of the worker's own failure status.</summary>
    internal string Describe() => Status switch
    {
        PtxWorkerExchangeStatus.Completed when Response is { } response && response.Status != "ok" =>
            response.Status + (string.IsNullOrEmpty(response.Message) ? string.Empty : ": " + response.Message),
        PtxWorkerExchangeStatus.Completed => "ok",
        PtxWorkerExchangeStatus.TimedOut => "The worker exceeded its " + Elapsed.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s deadline and was killed.",
        PtxWorkerExchangeStatus.OutputTooLarge => "The worker exceeded its output bound and was killed.",
        PtxWorkerExchangeStatus.Unavailable => "The PTX worker could not start: " + StandardError,
        _ => "The worker exited with code " + (ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown") + " and no usable response."
    };
}

/// <summary>Carries one request to wherever kernels run. The production transport is a watched child process.</summary>
internal interface IPtxWorkerTransport
{
    /// <summary>Gets an identity for fingerprints and evidence.</summary>
    string Identity { get; }

    /// <summary>Runs one request to completion, timeout or failure. Cancellation kills the worker and throws.</summary>
    PtxWorkerExchange Exchange(PtxWorkerRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs the embedded worker in a child process with a watchdog, bounded output and a scrubbed exit path.</summary>
internal sealed class PtxWorkerProcessTransport : IPtxWorkerTransport
{
    internal static readonly JsonSerializerOptions WireJson = new() { MaxDepth = 16 };
    private const string AssemblyResource = "AiDotNet.Evolution.Ptx.Worker.dll";
    private const string RuntimeConfigResource = "AiDotNet.Evolution.Ptx.Worker.runtimeconfig.json";
    private const int StandardErrorBytes = 16 * 1024;
    private static readonly object ExtractionGate = new();
    private readonly PtxIsolationOptions _options;

    internal PtxWorkerProcessTransport(PtxIsolationOptions options)
    {
        _options = options.Snapshot();
        Identity = "ptx-worker-process-v1:" + WorkerDigest();
    }

    public string Identity { get; }

    public PtxWorkerExchange Exchange(PtxWorkerRequest request, CancellationToken cancellationToken)
    {
        request.DeviceOrdinal = _options.DeviceOrdinal;
        request.MaxDeviceBytes = _options.MaxDeviceBytes;
        request.MaxReadbackBytes = _options.MaxReadbackBytes;
        return Run(JsonSerializer.SerializeToUtf8Bytes(request, WireJson), closeInput: true, cancellationToken);
    }

    /// <summary>Runs the worker on raw input. Leaving the input open models a worker that never answers.</summary>
    internal PtxWorkerExchange Run(byte[] input, bool closeInput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        string? host = FindDotnetHost(_options.DotnetHostPath);
        if (host is null) return new(PtxWorkerExchangeStatus.Unavailable, null, null, "no dotnet host was found", timer.Elapsed);
        string worker;
        try
        {
            worker = ExtractWorker(_options.WorkerDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(PtxWorkerExchangeStatus.Unavailable, null, null, "the embedded worker could not be written (" + exception.GetType().Name + ")", timer.Elapsed);
        }
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(worker) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(worker);
        // The worker must not inherit a host's diagnostics or startup hooks.
        foreach (string variable in new[] { "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "DOTNET_EnableEventPipe" })
            start.Environment.Remove(variable);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new(PtxWorkerExchangeStatus.Unavailable, null, null, "the dotnet host could not start (" + exception.GetType().Name + ")", timer.Elapsed);
        }
        using (process)
        {
            using var overflow = new CancellationTokenSource();
            Task<byte[]> output = ReadBoundedAsync(process.StandardOutput.BaseStream, _options.MaxResponseBytes, overflow);
            Task<byte[]> error = ReadBoundedAsync(process.StandardError.BaseStream, StandardErrorBytes, null);
            Task writer = Task.Run(() =>
            {
                try
                {
                    process.StandardInput.BaseStream.Write(input, 0, input.Length);
                    process.StandardInput.BaseStream.Flush();
                    if (closeInput) process.StandardInput.Close();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The worker died or was killed while reading; its exit status reports what happened.
                }
            }, CancellationToken.None);
            PtxWorkerExchangeStatus? killedFor = null;
            while (!process.WaitForExit(25))
            {
                if (cancellationToken.IsCancellationRequested || overflow.IsCancellationRequested || timer.Elapsed >= _options.Timeout)
                {
                    killedFor = overflow.IsCancellationRequested ? PtxWorkerExchangeStatus.OutputTooLarge : PtxWorkerExchangeStatus.TimedOut;
                    Kill(process);
                    break;
                }
            }
            process.WaitForExit(5000);
            Task.WaitAll(new Task[] { output, error, writer }, TimeSpan.FromSeconds(5));
            string standardError = error.IsCompletedSuccessfully ? Encoding.UTF8.GetString(error.Result) : string.Empty;
            TimeSpan elapsed = timer.Elapsed;
            cancellationToken.ThrowIfCancellationRequested();
            if (killedFor is { } status) return new(status, null, null, standardError, elapsed);
            if (overflow.IsCancellationRequested) return new(PtxWorkerExchangeStatus.OutputTooLarge, null, null, standardError, elapsed);
            int? exitCode = process.HasExited ? process.ExitCode : null;
            if (!output.IsCompletedSuccessfully || output.Result.Length == 0) return new(PtxWorkerExchangeStatus.Crashed, null, exitCode, standardError, elapsed);
            try
            {
                PtxWorkerResponse? response = JsonSerializer.Deserialize<PtxWorkerResponse>(output.Result, WireJson);
                if (response is null || response.Protocol != PtxWorkerRequest.CurrentProtocol || exitCode != 0)
                    return new(PtxWorkerExchangeStatus.Crashed, null, exitCode, standardError, elapsed);
                return new(PtxWorkerExchangeStatus.Completed, response, exitCode, standardError, elapsed);
            }
            catch (JsonException)
            {
                return new(PtxWorkerExchangeStatus.Crashed, null, exitCode, standardError, elapsed);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long limit, CancellationTokenSource? overflow)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length)).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                if (overflow is not null)
                {
                    overflow.Cancel();
                    return Array.Empty<byte>();
                }
                buffer.Write(chunk, 0, (int)Math.Max(0, limit - buffer.Length));
                continue; // Keep draining so the child never blocks on a full pipe; the excess is discarded.
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static string? FindDotnetHost(string? configured)
    {
        if (configured is not null) return File.Exists(configured) ? configured : null;
        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)) return hostPath;
        if (Environment.ProcessPath is { } processPath &&
            string.Equals(Path.GetFileName(processPath), executable, StringComparison.OrdinalIgnoreCase)) return processPath;
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, executable)))
            return Path.Combine(root, executable);
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (entry.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(entry.Trim('"'), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    private static byte[] Resource(string name)
    {
        using Stream stream = typeof(PtxWorkerProcessTransport).GetTypeInfo().Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("The embedded worker resource " + name + " is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string WorkerDigest()
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(Resource(AssemblyResource))).ToLowerInvariant().Substring(0, 16);
        }
        catch (InvalidDataException)
        {
            return "missing";
        }
    }

    /// <summary>Writes the worker into a content-addressed directory and verifies the bytes on every use.</summary>
    internal static string ExtractWorker(string? root)
    {
        byte[] assembly = Resource(AssemblyResource), runtimeConfig = Resource(RuntimeConfigResource);
        string digest = Convert.ToHexString(SHA256.HashData(assembly)).ToLowerInvariant();
        string directory = Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "aidotnet-ptx-worker"), digest.Substring(0, 32));
        lock (ExtractionGate)
        {
            CreatePrivateDirectory(directory);
            string assemblyPath = Path.Combine(directory, "AiDotNet.Evolution.Ptx.Worker.dll");
            WriteVerified(assemblyPath, assembly);
            WriteVerified(Path.Combine(directory, "AiDotNet.Evolution.Ptx.Worker.runtimeconfig.json"), runtimeConfig);
            return assemblyPath;
        }
    }

    private static void WriteVerified(string path, byte[] content)
    {
        if (File.Exists(path))
        {
            if (File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
            File.Delete(path); // A stale or altered copy is replaced, never run.
        }
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        File.WriteAllBytes(pending, content);
        try
        {
            File.Move(pending, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(pending); // Another process wrote it first.
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) throw new InvalidDataException("The written worker does not match its embedded bytes.");
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        Directory.CreateDirectory(directory);
    }
}