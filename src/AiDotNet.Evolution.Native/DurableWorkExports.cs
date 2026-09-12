using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Native;

// A trusted in-process ABI, not a network service or an untrusted-pointer sandbox.
// Global serialization deliberately bounds concurrent allocation and serializes close
// with submit/read. Callers still own sequencing of each submit/read pair.
internal static unsafe class DurableWorkExports
{
    private const int InvalidHandle = -1, InvalidArgument = -2, PendingReply = -3, NoReply = -4, Faulted = -5;
    private const int MaximumHandles = 8;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<ulong, Endpoint> Endpoints = new();
    private static ulong _lastHandle;

    private sealed class Endpoint
    {
        internal EvolutionWorkProtocol Protocol { get; } = new();
        internal byte[]? Reply { get; set; }
        internal bool Failed { get; set; }
    }

    [UnmanagedCallersOnly(EntryPoint = "aiev_work_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Version() => 1;

    [UnmanagedCallersOnly(EntryPoint = "aiev_work_create", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static ulong Create()
    {
        lock (Sync)
        {
            if (Endpoints.Count >= MaximumHandles || _lastHandle == ulong.MaxValue) return 0;
            try
            {
                ulong handle = ++_lastHandle; // Never reuse a stale caller's handle.
                Endpoints.Add(handle, new Endpoint());
                return handle;
            }
            catch (Exception) { return 0; }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "aiev_work_submit", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Submit(ulong handle, byte* request, int length)
    {
        if (request == null || length <= 0 || length > EvolutionWorkProtocol.MaximumFrameBytes) return InvalidArgument;
        lock (Sync)
        {
            if (!Endpoints.TryGetValue(handle, out Endpoint? endpoint)) return InvalidHandle;
            if (endpoint.Failed) return Faulted;
            if (endpoint.Reply is not null) return PendingReply;
            string json;
            try { json = Utf8.GetString(new ReadOnlySpan<byte>(request, length)); }
            catch (DecoderFallbackException) { return InvalidArgument; }
            catch (Exception) { endpoint.Failed = true; return Faulted; }
            try
            {
                string reply = endpoint.Protocol.ProcessJson(json);
                if (Utf8.GetByteCount(reply) > EvolutionWorkProtocol.MaximumFrameBytes)
                {
                    endpoint.Failed = true;
                    return Faulted;
                }
                endpoint.Reply = Utf8.GetBytes(reply);
                return 0;
            }
            catch (Exception)
            {
                // Execution may have committed before response encoding failed. Never rerun
                // implicitly: close/reopen the original store and reconcile its durable result.
                endpoint.Failed = true;
                return Faulted;
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "aiev_work_read", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Read(ulong handle, byte* destination, int capacity)
    {
        if (capacity < 0 || capacity > EvolutionWorkProtocol.MaximumFrameBytes || (destination == null && capacity != 0))
            return InvalidArgument;
        lock (Sync)
        {
            if (!Endpoints.TryGetValue(handle, out Endpoint? endpoint)) return InvalidHandle;
            if (endpoint.Failed) return Faulted;
            if (endpoint.Reply is not { } reply) return NoReply;
            int length = reply.Length;
            if (capacity < length) return length; // Query/short buffer: retain reply, execute nothing.
            reply.AsSpan().CopyTo(new Span<byte>(destination, capacity));
            endpoint.Reply = null; // Consume only after a complete copy to caller-owned memory.
            return length;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "aiev_work_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Close(ulong handle)
    {
        lock (Sync)
        {
            if (!Endpoints.Remove(handle, out Endpoint? endpoint)) return InvalidHandle;
            try { endpoint.Protocol.Dispose(); return 0; }
            catch (Exception) { return Faulted; }
        }
    }
}
