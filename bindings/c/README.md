# Durable worker C ABI

Build the optional shared library with .NET 10 NativeAOT and the platform linker:

```powershell
dotnet publish src/AiDotNet.Evolution.Native -c Release -r win-x64 -o TestResults/native-library
python bindings/c/test_native.py TestResults/native-library/aidotnet-evolution-native.dll
```

Use `linux-x64`/`.so` or `osx-arm64`/`.dylib` on those platforms. This is a
source-buildable artifact; no binary/package publication is implied.
Include [aidotnet_evolution_work.h](aidotnet_evolution_work.h), check ABI version 1,
create a handle, and submit the same UTF-8 JSON requests defined in the
[worker protocol](../../docs/DURABLE_WORKER_PROTOCOL.md). Request/reply version and
correlation still matter. Read into caller-owned memory and close every handle.
No pointer to managed memory escapes and no library allocator/free pairing is needed.

`submit` executes once and retains one reply. Query its size with `read(handle,NULL,0)`;
allocate up to that bounded size and call `read` again. A short buffer is untouched and
the reply remains pending. Submitting while a reply is pending returns -3 without
executing anything. A complete copy consumes the reply. A protocol `close` command
still has a reply; the ABI handle must subsequently be closed too.

Eight live handles and 16 MiB frames are the explicit limits, not a total-memory SLA:
coordinator state and transient JSON decoding also allocate. Calls are globally
serialized, including filesystem I/O; callers must serialize their own multi-call
request/reply sequences. This bounded adapter does not promise high parallel throughput.
Handles are never reused during the library lifetime. Do not unload the NativeAOT
runtime library while the process is running.

This is trusted in-process interop, not memory isolation or authentication. Callers
must supply valid buffers with the declared lengths. Blocking storage has no ABI
timeout; use the subprocess clients when process isolation/timeouts are required.
On -5, do not repeat physical work: close, reopen the original durable store, and
reconcile its identity and receipts. Closing or losing an unread response does not
cancel jobs, refund reservations, or restore the engine's pending search trajectory.

The ctypes suite calls actual exported native functions: exact 64-bit IDs/decimals,
recovery, retained liabilities, stale handles, UTF-8/buffer/handle bounds, and concurrent
submissions. It is correctness evidence, not a search-quality or throughput benchmark.
