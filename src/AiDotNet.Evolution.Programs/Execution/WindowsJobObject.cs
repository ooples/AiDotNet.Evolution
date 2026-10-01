// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/WindowsJobObject.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Runtime.InteropServices;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Wraps a Windows job object that caps the memory and user-mode CPU time of a sandboxed child process, reports
/// which cap it hit, and terminates whatever is still running in it when the handle closes.
/// </summary>
/// <remarks>
/// The type is Windows-only and every entry point is guarded, so on any other platform the factory returns
/// <c>null</c> and no interop is attempted. Failures are also non-fatal: a machine or container policy can forbid
/// job assignment, and a sandbox that still enforces its wall-clock limit and its output caps is far better than a
/// sandbox that refuses to run. Callers therefore treat a <c>null</c> result as "no memory cap available here" and
/// report that honestly rather than pretending the cap was applied.
/// </remarks>
internal sealed class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitProcessMemory = 0x0000_0100;
    private const uint JobObjectLimitJobMemory = 0x0000_0200;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
    private const uint JobObjectLimitProcessTime = 0x0000_0002;
    private const uint JobObjectLimitJobTime = 0x0000_0004;
    private const int JobObjectAssociateCompletionPortInformation = 7;
    private const uint MessageEndOfJobTime = 1;
    private const uint MessageEndOfProcessTime = 3;
    private const uint MessageProcessMemoryLimit = 9;
    private const uint MessageJobMemoryLimit = 10;
    // Posted once the job has no live process. The port delivers in order, so every earlier notification precedes it.
    private const uint MessageActiveProcessZero = 4;

    private IntPtr _handle;
    private IntPtr _port;
    private ProgramExecuteErrorCode? _violation;
    private bool _disposed;

    private WindowsJobObject(IntPtr handle, IntPtr port)
    {
        _handle = handle;
        _port = port;
    }

    /// <summary>Gets whether the job can report which limit it hit.</summary>
    public bool ReportsViolations => _port != IntPtr.Zero;

    /// <summary>Creates a job object that limits committed memory and CPU time and kills its members when disposed.</summary>
    /// <param name="memoryLimitBytes">The per-process and per-job commit limit in bytes; values below one are ignored.</param>
    /// <param name="cpuTimeLimit">The per-process and per-job user-mode CPU time limit; zero or less is ignored.</param>
    /// <returns>The job object, or <c>null</c> when the platform is not Windows or the operating system refused.</returns>
    public static WindowsJobObject? TryCreate(long memoryLimitBytes, TimeSpan cpuTimeLimit)
    {
        if (!IsWindows() || memoryLimitBytes <= 0)
        {
            return null;
        }

        IntPtr handle;
        try
        {
            handle = CreateJobObject(IntPtr.Zero, null);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var information = default(JobObjectExtendedLimitInformationNative);
        information.BasicLimitInformation.LimitFlags =
            JobObjectLimitProcessMemory | JobObjectLimitJobMemory | JobObjectLimitKillOnJobClose;
        information.ProcessMemoryLimit = new UIntPtr((ulong)memoryLimitBytes);
        information.JobMemoryLimit = new UIntPtr((ulong)memoryLimitBytes);
        if (cpuTimeLimit > TimeSpan.Zero)
        {
            // Both are in 100-nanosecond ticks, which is TimeSpan's unit.
            information.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessTime | JobObjectLimitJobTime;
            information.BasicLimitInformation.PerProcessUserTimeLimit = cpuTimeLimit.Ticks;
            information.BasicLimitInformation.PerJobUserTimeLimit = cpuTimeLimit.Ticks;
        }

        int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationNative));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            CloseHandle(handle);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new WindowsJobObject(handle, TryAssociatePort(handle));
    }

    /// <summary>Reports the first limit the job's members hit, reading any notifications posted since the last call.</summary>
    /// <returns>The violated limit, or <c>null</c> when none was hit or the job cannot report violations.</returns>
    public ProgramExecuteErrorCode? ReadViolation()
    {
        if (_disposed || _port == IntPtr.Zero) return _violation;
        try
        {
            while (GetQueuedCompletionStatus(_port, out uint message, out _, out _, 0)) Record(message);
        }
        catch (EntryPointNotFoundException)
        {
            // Without the port the job still enforces its limits; it just cannot say which one fired.
        }

        return _violation;
    }

    /// <summary>
    /// Reports the first limit hit once the job's processes have exited, waiting at most <paramref name="bound"/> for
    /// the notifications the kernel has yet to deliver.
    /// </summary>
    /// <remarks>
    /// A notification is posted asynchronously, so a candidate whose children failed an allocation and exited can be
    /// observed as exited before the memory-limit message arrives; reading the port once then reported a clean run (seen
    /// on GitHub's Windows runners). The port delivers in order and the no-active-process message comes last, so reading
    /// until it arrives sees every limit any process hit. The bound only matters while a detached process is still alive.
    /// </remarks>
    /// <param name="bound">The longest to wait for the job to report that no process is left.</param>
    /// <returns>The violated limit, or <c>null</c> when none was hit or the job cannot report violations.</returns>
    public ProgramExecuteErrorCode? ReadViolationAfterExit(TimeSpan bound)
    {
        if (_disposed || _port == IntPtr.Zero) return _violation;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!_noActiveProcess)
            {
                TimeSpan remaining = bound - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                if (!GetQueuedCompletionStatus(_port, out uint message, out _, out _, (uint)Math.Ceiling(remaining.TotalMilliseconds)))
                    break;
                Record(message);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Without the port the job still enforces its limits; it just cannot say which one fired.
        }

        return _violation;
    }

    private bool _noActiveProcess;

    private void Record(uint message)
    {
        if (message == MessageActiveProcessZero) _noActiveProcess = true;
        ProgramExecuteErrorCode? observed = message switch
        {
            MessageProcessMemoryLimit or MessageJobMemoryLimit => ProgramExecuteErrorCode.MemoryLimitExceeded,
            MessageEndOfProcessTime or MessageEndOfJobTime => ProgramExecuteErrorCode.CpuTimeLimitExceeded,
            _ => null
        };
        _violation ??= observed;
    }

    private static IntPtr TryAssociatePort(IntPtr job)
    {
        IntPtr port;
        try
        {
            port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }

        if (port == IntPtr.Zero) return IntPtr.Zero;

        var association = new JobObjectAssociateCompletionPortNative { CompletionKey = job, CompletionPort = port };
        int size = Marshal.SizeOf(typeof(JobObjectAssociateCompletionPortNative));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(association, buffer, fDeleteOld: false);
            if (SetInformationJobObject(job, JobObjectAssociateCompletionPortInformation, buffer, (uint)size)) return port;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        CloseHandle(port);
        return IntPtr.Zero;
    }

    /// <summary>Adds a running process to this job so the memory cap applies to it and to whatever it starts.</summary>
    /// <param name="processHandle">The native handle of the process to assign.</param>
    /// <returns><c>true</c> when the process joined the job.</returns>
    public bool TryAssign(IntPtr processHandle)
    {
        if (_disposed || _handle == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_handle, processHandle);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Closes the job handle, which terminates any process still running inside it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        if (_port != IntPtr.Zero)
        {
            CloseHandle(_port);
            _port = IntPtr.Zero;
        }
    }

    private static bool IsWindows()
    {
#if NET5_0_OR_GREATER
        return OperatingSystem.IsWindows();
#else
        return Environment.OSVersion.Platform == PlatformID.Win32NT;
#endif
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(
        IntPtr fileHandle, IntPtr existingCompletionPort, UIntPtr completionKey, uint numberOfConcurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(
        IntPtr completionPort, out uint numberOfBytes, out UIntPtr completionKey, out IntPtr overlapped, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectAssociateCompletionPortNative
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
