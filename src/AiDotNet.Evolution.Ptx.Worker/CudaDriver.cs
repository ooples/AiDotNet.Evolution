using System.Reflection;
using System.Runtime.InteropServices;

namespace AiDotNet.Evolution.Ptx.Worker;

/// <summary>The few CUDA driver API entry points the worker needs. Only this isolated process ever loads the driver.</summary>
internal static class CudaDriver
{
    private const string Library = "cuda";

    internal const int CuJitMaxRegisters = 0;
    internal const int CuJitWallTime = 2;
    internal const int CuJitInfoLogBuffer = 3;
    internal const int CuJitInfoLogBufferSizeBytes = 4;
    internal const int CuJitErrorLogBuffer = 5;
    internal const int CuJitErrorLogBufferSizeBytes = 6;
    internal const int CuJitOptimizationLevel = 7;
    internal const int CuJitLogVerbose = 12;

    internal const int FuncMaxThreadsPerBlock = 0;
    internal const int FuncSharedSizeBytes = 1;
    internal const int FuncConstSizeBytes = 2;
    internal const int FuncLocalSizeBytes = 3;
    internal const int FuncNumRegs = 4;
    internal const int FuncPtxVersion = 5;
    internal const int FuncBinaryVersion = 6;
    internal const int FuncMaxDynamicSharedSizeBytes = 8;

    internal const int DeviceMaxThreadsPerBlock = 1;
    internal const int DeviceMaxSharedMemoryPerBlock = 8;
    internal const int DeviceMaxRegistersPerBlock = 12;
    internal const int DeviceMultiprocessorCount = 16;
    internal const int DeviceComputeCapabilityMajor = 75;
    internal const int DeviceComputeCapabilityMinor = 76;
    internal const int DeviceMaxThreadsPerMultiprocessor = 39;
    internal const int DeviceMaxSharedMemoryPerMultiprocessor = 81;
    internal const int DeviceMaxRegistersPerMultiprocessor = 82;
    internal const int DeviceMaxSharedMemoryPerBlockOptin = 97;

    internal static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(CudaDriver).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (string candidate in OperatingSystem.IsWindows() ? new[] { "nvcuda.dll" } : new[] { "libcuda.so.1", "libcuda.so" })
            if (NativeLibrary.TryLoad(candidate, assembly, path, out IntPtr handle)) return handle;
        return IntPtr.Zero;
    }

    [DllImport(Library)] internal static extern int cuInit(uint flags);
    [DllImport(Library)] internal static extern int cuDriverGetVersion(out int version);
    [DllImport(Library)] internal static extern int cuDeviceGetCount(out int count);
    [DllImport(Library)] internal static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport(Library)] internal static extern int cuDeviceGetName(byte[] name, int length, int device);
    [DllImport(Library)] internal static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport(Library)] internal static extern int cuDeviceTotalMem_v2(out nuint bytes, int device);
    [DllImport(Library)] internal static extern int cuDevicePrimaryCtxRetain(out IntPtr context, int device);
    [DllImport(Library)] internal static extern int cuCtxSetCurrent(IntPtr context);
    [DllImport(Library)] internal static extern int cuCtxSynchronize();
    [DllImport(Library)] internal static extern int cuModuleLoadDataEx(out IntPtr module, byte[] image, uint optionCount, int[] options, IntPtr[] values);
    [DllImport(Library)] internal static extern int cuModuleGetFunction(out IntPtr function, IntPtr module, byte[] name);
    [DllImport(Library)] internal static extern int cuFuncGetAttribute(out int value, int attribute, IntPtr function);
    [DllImport(Library)] internal static extern int cuFuncSetAttribute(IntPtr function, int attribute, int value);
    [DllImport(Library)] internal static extern int cuMemAlloc_v2(out ulong pointer, nuint bytes);
    [DllImport(Library)] internal static extern int cuMemFree_v2(ulong pointer);
    [DllImport(Library)] internal static extern int cuMemcpyHtoD_v2(ulong destination, byte[] source, nuint bytes);
    [DllImport(Library)] internal static extern int cuMemcpyDtoH_v2(byte[] destination, ulong source, nuint bytes);
    [DllImport(Library)] internal static extern int cuMemsetD8_v2(ulong destination, byte value, nuint count);
    [DllImport(Library)]
    internal static extern int cuLaunchKernel(IntPtr function, uint gridX, uint gridY, uint gridZ,
        uint blockX, uint blockY, uint blockZ, uint sharedBytes, IntPtr stream, IntPtr[] parameters, IntPtr extra);
    [DllImport(Library)] internal static extern int cuEventCreate(out IntPtr evt, uint flags);
    [DllImport(Library)] internal static extern int cuEventRecord(IntPtr evt, IntPtr stream);
    [DllImport(Library)] internal static extern int cuEventSynchronize(IntPtr evt);
    [DllImport(Library)] internal static extern int cuEventElapsedTime(out float milliseconds, IntPtr start, IntPtr end);
    [DllImport(Library)] internal static extern int cuEventDestroy_v2(IntPtr evt);
    [DllImport(Library)] internal static extern int cuGetErrorName(int error, out IntPtr name);

    internal static string ErrorName(int error)
    {
        try
        {
            if (cuGetErrorName(error, out IntPtr name) == 0 && name != IntPtr.Zero)
                return Marshal.PtrToStringAnsi(name) ?? "CUDA_ERROR_" + error;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        return "CUDA_ERROR_" + error.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static void Check(int error, string operation)
    {
        if (error != 0) throw new CudaException(error, operation);
    }
}

/// <summary>A failed driver call; carries the CUDA error name the host reports.</summary>
internal sealed class CudaException(int error, string operation)
    : Exception(operation + " failed: " + CudaDriver.ErrorName(error))
{
    internal string ErrorName { get; } = CudaDriver.ErrorName(error);
    internal int Error { get; } = error;
}
