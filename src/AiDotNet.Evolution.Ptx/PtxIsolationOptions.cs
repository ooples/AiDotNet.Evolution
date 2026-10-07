namespace AiDotNet.Evolution.Ptx;

/// <summary>How candidates are isolated: every driver call runs in a separate worker process under a watchdog.</summary>
/// <remarks>
/// <para>PTX cannot touch files, the network or the host's memory, so the worker is not a security sandbox; it is a
/// failure boundary. A kernel that loops forever, faults, or corrupts the CUDA context ends only the worker, which the
/// watchdog kills at <see cref="Timeout"/>, and the host reports the outcome as an ordinary rejection.</para>
/// <para>The worker ships embedded in this assembly and is written to <see cref="WorkerDirectory"/> on first use; it
/// runs on the installed <c>dotnet</c> host (8 or later).</para>
/// </remarks>
public sealed class PtxIsolationOptions
{
    /// <summary>Gets or sets the wall-clock limit for one worker invocation, 1 second to 1 hour. Default: 2 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Gets or sets the CUDA device ordinal. Default: 0.</summary>
    public int DeviceOrdinal { get; set; }
    /// <summary>Gets or sets the device memory one case may allocate, including guard regions. Default: 1 GiB.</summary>
    public long MaxDeviceBytes { get; set; } = 1L << 30;
    /// <summary>Gets or sets the output bytes one invocation may read back. Default: 256 MiB.</summary>
    public long MaxReadbackBytes { get; set; } = 256L << 20;
    /// <summary>Gets or sets the most bytes of worker output the host accepts before killing it. Default: 512 MiB.</summary>
    public long MaxResponseBytes { get; set; } = 512L << 20;
    /// <summary>Gets or sets the <c>dotnet</c> host, or <c>null</c> to find it (DOTNET_HOST_PATH, DOTNET_ROOT, PATH).</summary>
    public string? DotnetHostPath { get; set; }
    /// <summary>Gets or sets where the worker is written, or <c>null</c> for a directory under the system temp path.</summary>
    public string? WorkerDirectory { get; set; }

    internal PtxIsolationOptions Snapshot()
    {
        var copy = (PtxIsolationOptions)MemberwiseClone();
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (DeviceOrdinal is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(DeviceOrdinal));
        if (MaxDeviceBytes is < 1 << 20 or > 1L << 40) throw new ArgumentOutOfRangeException(nameof(MaxDeviceBytes));
        if (MaxReadbackBytes is < 1 << 10 or > 4L << 30) throw new ArgumentOutOfRangeException(nameof(MaxReadbackBytes));
        if (MaxResponseBytes is < 1 << 16 or > 8L << 30) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (DotnetHostPath is not null && !Path.IsPathRooted(DotnetHostPath)) throw new ArgumentException("DotnetHostPath must be absolute.");
        if (WorkerDirectory is not null) copy.WorkerDirectory = Path.GetFullPath(WorkerDirectory);
        return copy;
    }
}