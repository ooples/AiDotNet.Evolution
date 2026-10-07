using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AiDotNet.Evolution.Ptx.Worker;

/// <summary>Executes one request against the CUDA driver. Every failure becomes a response status, never a crash.</summary>
internal sealed class PtxWorkerExecutor
{
    /// <summary>Bytes of guard memory on each side of every buffer; a kernel that writes there wrote out of bounds.</summary>
    internal const int GuardBytes = 4096;
    private const byte GuardPattern = 0xA5;
    private const int LogBytes = 64 * 1024;

    private readonly PtxWorkerRequest _request;
    private readonly List<IntPtr> _functions = new();

    internal PtxWorkerExecutor(PtxWorkerRequest request) => _request = request;

    internal PtxWorkerResponse Execute()
    {
        var response = new PtxWorkerResponse();
        string? invalid = Validate(_request);
        if (invalid is not null) return Fail(response, "invalid-request", invalid);
        try
        {
            int init = CudaDriver.cuInit(0);
            if (init != 0) return Fail(response, "cuda-unavailable", "cuInit returned " + CudaDriver.ErrorName(init) + ".");
            CudaDriver.Check(CudaDriver.cuDeviceGetCount(out int count), "cuDeviceGetCount");
            if (count <= _request.DeviceOrdinal)
                return Fail(response, "no-device", "The driver reports " + count + " device(s); ordinal " + _request.DeviceOrdinal + " is absent.");
            CudaDriver.Check(CudaDriver.cuDeviceGet(out int device, _request.DeviceOrdinal), "cuDeviceGet");
            response.Device = Describe(device, _request.DeviceOrdinal);
            if (_request.Operation == PtxWorkerOperation.Probe) return response;
            CudaDriver.Check(CudaDriver.cuDevicePrimaryCtxRetain(out IntPtr context, device), "cuDevicePrimaryCtxRetain");
            CudaDriver.Check(CudaDriver.cuCtxSetCurrent(context), "cuCtxSetCurrent");
            bool allLoaded = true;
            foreach (PtxWorkerKernel kernel in _request.Kernels)
            {
                PtxWorkerCompiled compiled = Compile(kernel, out IntPtr function);
                response.Kernels.Add(compiled);
                _functions.Add(function);
                allLoaded &= compiled.Loaded;
            }
            if (_request.Operation == PtxWorkerOperation.Compile) return response;
            if (!allLoaded) return Fail(response, "compile-failed", "At least one kernel did not load; nothing was launched.");
            if (_request.Operation == PtxWorkerOperation.Validate)
            {
                foreach (PtxWorkerCase item in _request.Cases)
                {
                    PtxWorkerCaseResult result = RunCase(item, out bool contextLost);
                    response.Cases.Add(result);
                    if (contextLost) return Fail(response, "context-lost", "A launch failed and left the CUDA context unusable.");
                }
                return response;
            }
            response.Timing = Time(_request.Cases[0], _request.Timing ?? throw new InvalidOperationException("Validated timing plan is missing."));
            return response;
        }
        catch (DllNotFoundException)
        {
            return Fail(response, "cuda-unavailable", "The CUDA driver library is not installed on this machine.");
        }
        catch (EntryPointNotFoundException exception)
        {
            return Fail(response, "cuda-unavailable", "The CUDA driver is too old: " + exception.Message);
        }
        catch (CudaException exception)
        {
            return Fail(response, exception.ErrorName, exception.Message);
        }
    }

    private static PtxWorkerResponse Fail(PtxWorkerResponse response, string status, string message)
    {
        response.Status = status;
        response.Message = message.Length > 1024 ? message.Substring(0, 1024) : message;
        return response;
    }

    private static string? Validate(PtxWorkerRequest request)
    {
        if (request.Protocol != PtxWorkerRequest.CurrentProtocol) return "Unsupported protocol version.";
        if (!Enum.IsDefined(request.Operation)) return "Unknown operation.";
        if (request.Operation == PtxWorkerOperation.Probe) return null;
        if (request.Kernels.Count is < 1 or > 2) return "One or two kernels are required.";
        if (request.Kernels.Any(k => string.IsNullOrEmpty(k.Ptx) || string.IsNullOrEmpty(k.EntryPoint) || k.MaxRegisters is < 0 or > 255 || k.OptimizationLevel is < 0 or > 4))
            return "A kernel is malformed.";
        if (request.Operation == PtxWorkerOperation.Compile) return null;
        if (request.Cases.Count < 1) return "At least one case is required.";
        if (request.Operation == PtxWorkerOperation.Time && (request.Kernels.Count != 2 || request.Timing is null ||
            request.Timing.Pairs < 1 || request.Timing.ControlPairs < 1 || request.Timing.Warmup < 0 || request.Timing.LaunchesPerSample < 1))
            return "Timing needs two kernels and a positive plan.";
        foreach (PtxWorkerCase item in request.Cases)
        {
            if (item.Launches.Count != request.Kernels.Count) return "Every case needs one launch per kernel.";
            if (item.Buffers.Any(b => b.Elements < 1 || !Enum.IsDefined(b.ElementType) || !Enum.IsDefined(b.Fill)))
                return "A buffer is malformed.";
            if (item.Arguments.Any(a => a.BufferIndex >= item.Buffers.Count || (a.BufferIndex < 0 && string.IsNullOrEmpty(a.ScalarBase64))))
                return "An argument is malformed.";
            long device = item.Buffers.Sum(b => checked(b.Elements * PtxDeterministicFill.ElementSize(b.ElementType) + 2L * GuardBytes + 256));
            long readback = item.Buffers.Where(b => b.IsOutput).Sum(b => checked(b.Elements * PtxDeterministicFill.ElementSize(b.ElementType)));
            if (device > request.MaxDeviceBytes) return "A case exceeds the device memory bound.";
            if (readback * 2 * request.Kernels.Count > request.MaxReadbackBytes) return "A case exceeds the readback bound.";
        }
        return null;
    }

    private static PtxWorkerDevice Describe(int device, int ordinal)
    {
        var name = new byte[256];
        CudaDriver.Check(CudaDriver.cuDeviceGetName(name, name.Length, device), "cuDeviceGetName");
        CudaDriver.Check(CudaDriver.cuDriverGetVersion(out int driver), "cuDriverGetVersion");
        CudaDriver.Check(CudaDriver.cuDeviceTotalMem_v2(out nuint memory, device), "cuDeviceTotalMem");
        int Attribute(int attribute)
        {
            CudaDriver.Check(CudaDriver.cuDeviceGetAttribute(out int value, attribute, device), "cuDeviceGetAttribute");
            return value;
        }
        int nameLength = Array.IndexOf(name, (byte)0);
        return new PtxWorkerDevice
        {
            Ordinal = ordinal,
            Name = Encoding.ASCII.GetString(name, 0, nameLength < 0 ? name.Length : nameLength),
            DriverVersion = driver,
            TotalMemoryBytes = (long)memory,
            ComputeMajor = Attribute(CudaDriver.DeviceComputeCapabilityMajor),
            ComputeMinor = Attribute(CudaDriver.DeviceComputeCapabilityMinor),
            MultiprocessorCount = Attribute(CudaDriver.DeviceMultiprocessorCount),
            MaxThreadsPerBlock = Attribute(CudaDriver.DeviceMaxThreadsPerBlock),
            MaxThreadsPerMultiprocessor = Attribute(CudaDriver.DeviceMaxThreadsPerMultiprocessor),
            MaxSharedMemoryPerBlock = Attribute(CudaDriver.DeviceMaxSharedMemoryPerBlock),
            MaxSharedMemoryPerBlockOptin = Attribute(CudaDriver.DeviceMaxSharedMemoryPerBlockOptin),
            MaxSharedMemoryPerMultiprocessor = Attribute(CudaDriver.DeviceMaxSharedMemoryPerMultiprocessor),
            MaxRegistersPerBlock = Attribute(CudaDriver.DeviceMaxRegistersPerBlock),
            MaxRegistersPerMultiprocessor = Attribute(CudaDriver.DeviceMaxRegistersPerMultiprocessor)
        };
    }

    private static PtxWorkerCompiled Compile(PtxWorkerKernel kernel, out IntPtr function)
    {
        function = IntPtr.Zero;
        var info = new byte[LogBytes];
        var error = new byte[LogBytes];
        var options = new List<int> { CudaDriver.CuJitInfoLogBuffer, CudaDriver.CuJitInfoLogBufferSizeBytes,
            CudaDriver.CuJitErrorLogBuffer, CudaDriver.CuJitErrorLogBufferSizeBytes, CudaDriver.CuJitLogVerbose,
            CudaDriver.CuJitOptimizationLevel };
        GCHandle infoHandle = GCHandle.Alloc(info, GCHandleType.Pinned), errorHandle = GCHandle.Alloc(error, GCHandleType.Pinned);
        try
        {
            var values = new List<IntPtr> { infoHandle.AddrOfPinnedObject(), (IntPtr)LogBytes, errorHandle.AddrOfPinnedObject(),
                (IntPtr)LogBytes, (IntPtr)1, (IntPtr)kernel.OptimizationLevel };
            if (kernel.MaxRegisters > 0)
            {
                options.Add(CudaDriver.CuJitMaxRegisters);
                values.Add((IntPtr)kernel.MaxRegisters);
            }
            byte[] image = Encoding.UTF8.GetBytes(kernel.Ptx + "\0");
            IntPtr[] valueArray = values.ToArray();
            var timer = Stopwatch.StartNew();
            int loaded = CudaDriver.cuModuleLoadDataEx(out IntPtr module, image, (uint)options.Count, options.ToArray(), valueArray);
            timer.Stop();
            var compiled = new PtxWorkerCompiled
            {
                InfoLog = Log(info, valueArray[1]),
                ErrorLog = Log(error, valueArray[3]),
                WallTimeMilliseconds = timer.Elapsed.TotalMilliseconds
            };
            if (loaded != 0)
            {
                compiled.ErrorCode = CudaDriver.ErrorName(loaded);
                return compiled;
            }
            int found = CudaDriver.cuModuleGetFunction(out function, module, Encoding.ASCII.GetBytes(kernel.EntryPoint + "\0"));
            if (found != 0)
            {
                compiled.ErrorCode = CudaDriver.ErrorName(found);
                function = IntPtr.Zero;
                return compiled;
            }
            int Attribute(int attribute, IntPtr target)
            {
                CudaDriver.Check(CudaDriver.cuFuncGetAttribute(out int value, attribute, target), "cuFuncGetAttribute");
                return value;
            }
            compiled.Loaded = true;
            compiled.Registers = Attribute(CudaDriver.FuncNumRegs, function);
            compiled.StaticSharedBytes = Attribute(CudaDriver.FuncSharedSizeBytes, function);
            compiled.ConstantBytes = Attribute(CudaDriver.FuncConstSizeBytes, function);
            compiled.LocalBytes = Attribute(CudaDriver.FuncLocalSizeBytes, function);
            compiled.MaxThreadsPerBlock = Attribute(CudaDriver.FuncMaxThreadsPerBlock, function);
            compiled.PtxVersion = Attribute(CudaDriver.FuncPtxVersion, function);
            compiled.BinaryVersion = Attribute(CudaDriver.FuncBinaryVersion, function);
            return compiled;
        }
        finally
        {
            infoHandle.Free();
            errorHandle.Free();
        }
    }

    private static string Log(byte[] buffer, IntPtr reported)
    {
        long length = Math.Clamp((long)reported, 0, buffer.Length);
        int end = Array.IndexOf(buffer, (byte)0, 0, (int)length);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? (int)length : end);
    }

    private PtxWorkerCaseResult RunCase(PtxWorkerCase item, out bool contextLost)
    {
        contextLost = false;
        var result = new PtxWorkerCaseResult();
        using var buffers = new DeviceBuffers(item);
        for (int kernel = 0; kernel < _functions.Count; kernel++)
        {
            var run = new PtxWorkerRun();
            result.Runs.Add(run);
            if (contextLost)
            {
                run.Status = "not-run";
                continue;
            }
            try
            {
                // Two launches onto opposite sentinels: an output element the kernel never writes differs between them.
                RunOnce(kernel, item, buffers, 0xFF, run, run.FirstOutputs);
                RunOnce(kernel, item, buffers, 0x00, run, run.SecondOutputs);
            }
            catch (CudaException exception)
            {
                run.Status = exception.ErrorName;
                run.Message = exception.Message;
                run.FirstOutputs.Clear();
                run.SecondOutputs.Clear();
                // Launch-time argument errors leave the context usable; execution faults are sticky and poison it.
                contextLost = exception.ErrorName is not ("CUDA_ERROR_INVALID_VALUE" or "CUDA_ERROR_LAUNCH_OUT_OF_RESOURCES" or "CUDA_ERROR_INVALID_HANDLE");
            }
        }
        return result;
    }

    private void RunOnce(int kernel, PtxWorkerCase item, DeviceBuffers buffers, byte sentinel, PtxWorkerRun run, List<string> outputs)
    {
        buffers.Reset(sentinel);
        Launch(_functions[kernel], item.Launches[kernel], buffers.Arguments);
        CudaDriver.Check(CudaDriver.cuCtxSynchronize(), "kernel execution");
        foreach (int violation in buffers.GuardViolations())
            if (!run.GuardViolations.Contains(violation)) run.GuardViolations.Add(violation);
        outputs.AddRange(buffers.ReadOutputs().Select(Convert.ToBase64String));
    }

    private PtxWorkerTimingResult Time(PtxWorkerCase item, PtxWorkerTimingPlan plan)
    {
        var timing = new PtxWorkerTimingResult();
        using var buffers = new DeviceBuffers(item);
        buffers.Reset(0);
        CudaDriver.Check(CudaDriver.cuEventCreate(out IntPtr start, 0), "cuEventCreate");
        CudaDriver.Check(CudaDriver.cuEventCreate(out IntPtr end, 0), "cuEventCreate");
        try
        {
            double Sample(int kernel)
            {
                buffers.RestoreInPlaceInputs();
                CudaDriver.Check(CudaDriver.cuEventRecord(start, IntPtr.Zero), "cuEventRecord");
                for (int i = 0; i < plan.LaunchesPerSample; i++) Launch(_functions[kernel], item.Launches[kernel], buffers.Arguments);
                CudaDriver.Check(CudaDriver.cuEventRecord(end, IntPtr.Zero), "cuEventRecord");
                CudaDriver.Check(CudaDriver.cuEventSynchronize(end), "kernel execution");
                CudaDriver.Check(CudaDriver.cuEventElapsedTime(out float ms, start, end), "cuEventElapsedTime");
                return ms / plan.LaunchesPerSample;
            }
            for (int i = 0; i < plan.Warmup; i++)
            {
                Sample(0);
                Sample(1);
            }
            // Incumbent against itself first: the spread of identical pairs is the calibrated noise floor.
            for (int i = 0; i < plan.ControlPairs; i++)
            {
                timing.ControlFirstMilliseconds.Add(Sample(1));
                timing.ControlSecondMilliseconds.Add(Sample(1));
            }
            // The interleaving the Tensors paired replay uses: alternate which side runs first in each pair.
            for (int i = 0; i < plan.Pairs; i++)
            {
                double candidate, incumbent;
                if ((i & 1) == 0)
                {
                    candidate = Sample(0);
                    incumbent = Sample(1);
                }
                else
                {
                    incumbent = Sample(1);
                    candidate = Sample(0);
                }
                timing.CandidateMilliseconds.Add(candidate);
                timing.IncumbentMilliseconds.Add(incumbent);
            }
            return timing;
        }
        finally
        {
            CudaDriver.cuEventDestroy_v2(start);
            CudaDriver.cuEventDestroy_v2(end);
        }
    }

    private static void Launch(IntPtr function, PtxWorkerLaunch launch, IntPtr[] arguments)
    {
        if (launch.SharedBytes > 48 * 1024)
            CudaDriver.Check(CudaDriver.cuFuncSetAttribute(function, CudaDriver.FuncMaxDynamicSharedSizeBytes, (int)launch.SharedBytes), "cuFuncSetAttribute");
        CudaDriver.Check(CudaDriver.cuLaunchKernel(function, launch.GridX, launch.GridY, launch.GridZ,
            launch.BlockX, launch.BlockY, launch.BlockZ, launch.SharedBytes, IntPtr.Zero, arguments, IntPtr.Zero), "cuLaunchKernel");
    }

    /// <summary>Guarded device allocations, their host-side initial contents and the packed launch arguments.</summary>
    private sealed class DeviceBuffers : IDisposable
    {
        private readonly PtxWorkerCase _case;
        private readonly ulong[] _allocations;
        private readonly long[] _payloadBytes;
        private readonly long[] _allocationBytes;
        private readonly byte[]?[] _inputs;
        private readonly IntPtr[] _argumentStorage;

        internal DeviceBuffers(PtxWorkerCase item)
        {
            _case = item;
            int count = item.Buffers.Count;
            _allocations = new ulong[count];
            _payloadBytes = new long[count];
            _allocationBytes = new long[count];
            _inputs = new byte[]?[count];
            _argumentStorage = new IntPtr[item.Arguments.Count];
            Arguments = new IntPtr[item.Arguments.Count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    PtxWorkerBuffer buffer = item.Buffers[i];
                    _payloadBytes[i] = buffer.Elements * PtxDeterministicFill.ElementSize(buffer.ElementType);
                    _allocationBytes[i] = GuardBytes + ((_payloadBytes[i] + 255) / 256 * 256) + GuardBytes;
                    CudaDriver.Check(CudaDriver.cuMemAlloc_v2(out _allocations[i], (nuint)_allocationBytes[i]), "cuMemAlloc");
                    if (buffer.IsInput) _inputs[i] = PtxDeterministicFill.Generate(buffer);
                }
                for (int i = 0; i < item.Arguments.Count; i++)
                {
                    PtxWorkerArgument argument = item.Arguments[i];
                    byte[] value = argument.BufferIndex >= 0
                        ? BitConverter.GetBytes(_allocations[argument.BufferIndex] + GuardBytes)
                        : Convert.FromBase64String(argument.ScalarBase64 ?? string.Empty);
                    _argumentStorage[i] = Marshal.AllocHGlobal(Math.Max(8, value.Length));
                    Marshal.Copy(value, 0, _argumentStorage[i], value.Length);
                    Arguments[i] = _argumentStorage[i];
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr[] Arguments { get; }

        /// <summary>Writes guards, inputs and an output sentinel, so an element a kernel never writes is visible.</summary>
        internal void Reset(byte sentinel)
        {
            for (int i = 0; i < _allocations.Length; i++)
            {
                CudaDriver.Check(CudaDriver.cuMemsetD8_v2(_allocations[i], GuardPattern, (nuint)_allocationBytes[i]), "cuMemsetD8");
                if (_inputs[i] is { } input)
                    CudaDriver.Check(CudaDriver.cuMemcpyHtoD_v2(_allocations[i] + GuardBytes, input, (nuint)input.Length), "cuMemcpyHtoD");
                else
                    CudaDriver.Check(CudaDriver.cuMemsetD8_v2(_allocations[i] + GuardBytes, sentinel, (nuint)_payloadBytes[i]), "cuMemsetD8");
            }
        }

        /// <summary>Re-uploads buffers the kernel both reads and writes, so every timed sample sees the same data.</summary>
        internal void RestoreInPlaceInputs()
        {
            for (int i = 0; i < _allocations.Length; i++)
                if (_case.Buffers[i].IsOutput && _inputs[i] is { } input)
                    CudaDriver.Check(CudaDriver.cuMemcpyHtoD_v2(_allocations[i] + GuardBytes, input, (nuint)input.Length), "cuMemcpyHtoD");
        }

        internal List<int> GuardViolations()
        {
            var violations = new List<int>();
            var guard = new byte[GuardBytes];
            for (int i = 0; i < _allocations.Length; i++)
            {
                long payloadEnd = GuardBytes + _payloadBytes[i];
                var tail = new byte[_allocationBytes[i] - payloadEnd];
                CudaDriver.Check(CudaDriver.cuMemcpyDtoH_v2(guard, _allocations[i], (nuint)guard.Length), "cuMemcpyDtoH");
                CudaDriver.Check(CudaDriver.cuMemcpyDtoH_v2(tail, _allocations[i] + (ulong)payloadEnd, (nuint)tail.Length), "cuMemcpyDtoH");
                if (guard.AsSpan().IndexOfAnyExcept(GuardPattern) >= 0 || tail.AsSpan().IndexOfAnyExcept(GuardPattern) >= 0)
                    violations.Add(i);
            }
            return violations;
        }

        internal List<byte[]> ReadOutputs()
        {
            var outputs = new List<byte[]>();
            for (int i = 0; i < _allocations.Length; i++)
            {
                if (!_case.Buffers[i].IsOutput) continue;
                var bytes = new byte[_payloadBytes[i]];
                CudaDriver.Check(CudaDriver.cuMemcpyDtoH_v2(bytes, _allocations[i] + GuardBytes, (nuint)bytes.Length), "cuMemcpyDtoH");
                outputs.Add(bytes);
            }
            return outputs;
        }

        public void Dispose()
        {
            foreach (ulong allocation in _allocations)
                if (allocation != 0) CudaDriver.cuMemFree_v2(allocation);
            foreach (IntPtr storage in _argumentStorage)
                if (storage != IntPtr.Zero) Marshal.FreeHGlobal(storage);
        }
    }
}