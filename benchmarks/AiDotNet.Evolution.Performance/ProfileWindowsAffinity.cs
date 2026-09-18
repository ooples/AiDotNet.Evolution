using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiDotNet.Evolution.Performance;

/// <summary>Group-aware affinity for this owned worker only; never enumerates or modifies another process.</summary>
internal static class ProfileWindowsAffinity
{
    public static ushort CurrentThreadGroup()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (!GetThreadGroupAffinity(GetCurrentThread(), out var affinity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return affinity.Group;
    }

    public static SafeFileHandle? BindOwnedWorker(ushort group, ulong mask)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var process = Process.GetCurrentProcess();
        var affinity = new GroupAffinity { Mask = new UIntPtr(mask), Group = group, Reserved1 = 0, Reserved2 = 0, Reserved3 = 0 };
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid || !SetJobGroup(job, 11, ref group, sizeof(ushort)) ||
                !SetJobAffinity(job, 14, ref affinity, (uint)Marshal.SizeOf<GroupAffinity>()) ||
                !AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group, Reserved1, Reserved2, Reserved3;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJobGroup(SafeFileHandle job, int informationClass, ref ushort group, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJobAffinity(SafeFileHandle job, int informationClass, ref GroupAffinity affinity, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
