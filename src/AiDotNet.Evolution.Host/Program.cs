using System.Text;
using AiDotNet.Evolution.Host;

/// <summary>
/// A NativeAOT binary that speaks JSON-lines on stdin and stdout.
///
/// WHY A PROCESS AND NOT A BINDING. The library's ask/tell surface is coarse-grained -- one
/// call per BATCH, not per evaluation -- so a process hop costs almost nothing, and buying
/// that cheaply removes an ABI to keep in sync, a C++ toolchain in CI, a per-Node-version
/// prebuild matrix, and the possibility of a fault in native code taking the host runtime
/// down with it. An FFI binding would have been the reflex; it is the wrong trade at this
/// granularity.
///
/// Protocol: one JSON object per line in, one per line out, `id` echoed so a client can
/// correlate without depending on ordering.
///
///   {"op":"open","id":1,"config":{...}}      -> {"id":1,"ok":true,"version":"..."}
///   {"op":"ask","id":2,"max":8}              -> {"id":2,"ok":true,"candidates":[...]}
///   {"op":"tell","id":3,"results":[...]}     -> {"id":3,"ok":true,"accepted":8}
///   {"op":"close","id":4}                    -> {"id":4,"ok":true,"best":{...}}
///
/// An empty `candidates` array means the run is over; that is the completion signal for a
/// client that cannot await anything.
/// </summary>
internal static class Program
{
    private const string Version = "0.1.0";

    // A peer may pipeline asks, but waiting requests must not grow without a bound.
    internal const int MaxPendingAsks = 32;

    private static async Task<int> Main(string[] args)
    {
        // UTF-8 WITHOUT A BOM, EXPLICITLY. The default console encoding on Windows is the
        // active code page, which mangles any non-ASCII a client sends back in a reason
        // string, and a BOM on the first line makes the client's JSON.parse fail on a
        // document that is otherwise perfectly valid.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        if (args.Length == 1 && args[0] == "--durable") return await ServeDurableAsync(stdin, stdout).ConfigureAwait(false);
        if (args.Length != 0) { await Console.Error.WriteLineAsync("Usage: aidotnet-evolution-host [--durable]").ConfigureAwait(false); return 2; }
        return await ServeAsync(stdin, stdout).ConfigureAwait(false);
    }

    /// <summary>Trusted local IPC for durable delivery; it does not instantiate or restore an evolution engine.</summary>
    internal static async Task<int> ServeDurableAsync(TextReader stdin, TextWriter stdout)
    {
        using var endpoint = new AiDotNet.Evolution.EvolutionWorkProtocol();
        var frames = new FrameReader(stdin);
        while (!endpoint.IsClosed)
        {
            (string? line, bool overlong) = await frames.NextAsync().ConfigureAwait(false);
            if (line is null) break;
            if (!overlong && string.IsNullOrWhiteSpace(line)) continue;
            // An oversized frame has no recoverable correlation ID. The dispatcher returns a
            // bounded id:0 error; clients must reconcile, never infer that a mutation failed.
            string response = endpoint.ProcessJson(overlong ? string.Empty : line);
            await stdout.WriteLineAsync(response).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>Reads frames from one stream and answers on the other until it ends.</summary>
    /// <remarks>
    /// SEPARATED FROM Main SO THE FRAMING CAN BE TESTED. Everything interesting here --
    /// an oversized frame answered and skipped, a malformed one echoing the id it could
    /// recover, blank lines ignored, `close` ending the loop -- was reachable only by
    /// spawning the binary, which no in-process test can observe. Main now does nothing
    /// but choose the encoding and hand over the streams.
    /// </remarks>
    internal static async Task<int> ServeAsync(TextReader stdin, TextWriter stdout)
    {
        HostSession? session = null;
        Action<HostSession?> setSession = value => session = value;
        try
        {
            return await ServeRequestsAsync(stdin, stdout,
                (request, cancellationToken) => Handle(request, session, setSession, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            session?.Dispose();
        }
    }

    // The scheduler owns all read/write operations; the caller owns the session. Keeping
    // dispatch separate also lets transport-fault tests control when a waiting ask finishes.
    internal static async Task<int> ServeRequestsAsync(
        TextReader stdin,
        TextWriter stdout,
        Func<Request, CancellationToken, Task<Response>> handle)
    {
        var frames = new FrameReader(stdin);
        var pendingAsks = new List<Task<Response>>();
        using var askCancellation = new CancellationTokenSource();
        using var readCancellation = new CancellationTokenSource();
        Task<(string? Line, bool Overlong)>? nextFrame = null;
        try
        {
            while (true)
            {
                // Only this loop writes responses or changes session ownership. An ask
                // may wait for a tell, so it must not prevent that tell (or close) being read.
                for (int i = 0; i < pendingAsks.Count;)
                {
                    Task<Response> pending = pendingAsks[i];
                    if (!pending.IsCompleted)
                    {
                        i += 1;
                        continue;
                    }
                    await Write(stdout, await pending.ConfigureAwait(false)).ConfigureAwait(false);
                    pendingAsks.RemoveAt(i);
                }

                nextFrame ??= frames.NextAsync(readCancellation.Token);
                if (!nextFrame.IsCompleted && pendingAsks.Count > 0)
                {
                    var ready = new Task[pendingAsks.Count + 1];
                    ready[0] = nextFrame;
                    for (int i = 0; i < pendingAsks.Count; i += 1) ready[i + 1] = pendingAsks[i];
                    await Task.WhenAny(ready).ConfigureAwait(false);
                    if (!nextFrame.IsCompleted) continue;
                }

                (string? line, bool overlong) = await nextFrame.ConfigureAwait(false);
                nextFrame = null;
                if (line is null)
                {
                    askCancellation.Cancel();
                    await DrainAsks(stdout, pendingAsks).ConfigureAwait(false);
                    break;
                }
                if (overlong)
                {
                    // The rest of that frame was discarded, so there is no id to echo and
                    // no way to resynchronise except by saying so and reading on.
                    await Write(stdout, new Response
                    {
                        Ok = false,
                        Error = $"frame exceeds the {ProtocolLimits.MaxFrameChars} character limit and was discarded",
                    }).ConfigureAwait(false);
                    continue;
                }
                // WHITESPACE COUNTS AS BLANK. Skipping empty lines but answering a line
                // of spaces with "malformed JSON" is an inconsistency a client can trip
                // over for no reason -- a CR-only line already arrives here empty and is
                // skipped, so padding should behave the same way.
                if (line.Trim().Length == 0) continue;

                Request? request = Protocol.ParseRequest(line, out string? parseError, out long id);
                if (request is null)
                {
                    await Write(stdout, new Response { Ok = false, Error = parseError, Id = id })
                        .ConfigureAwait(false);
                    continue;
                }

                Protocol.Op? op = Protocol.ParseOp(request.Op);
                if (op == Protocol.Op.Ask)
                {
                    if (pendingAsks.Count == MaxPendingAsks)
                    {
                        await Write(stdout, new Response
                        {
                            Id = request.Id,
                            Ok = false,
                            Error = $"at most {MaxPendingAsks} asks may wait for results at once",
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        pendingAsks.Add(Dispatch(request, handle, askCancellation.Token));
                    }
                    continue;
                }

                if (op == Protocol.Op.Close)
                {
                    // Settle callers before Close disposes the semaphore they await.
                    // Cancellation is an error response, never a false end-of-run batch.
                    askCancellation.Cancel();
                    await DrainAsks(stdout, pendingAsks).ConfigureAwait(false);
                }

                Response response = await Dispatch(request, handle).ConfigureAwait(false);
                await Write(stdout, response).ConfigureAwait(false);

                if (op == Protocol.Op.Close) break;
            }
        }
        finally
        {
            askCancellation.Cancel();
            readCancellation.Cancel();
            try
            {
                await Task.WhenAll(pendingAsks).ConfigureAwait(false);
            }
            finally
            {
                ObserveAbandonedRead(nextFrame);
            }
        }

        return 0;
    }

    private static void ObserveAbandonedRead(Task<(string? Line, bool Overlong)>? pendingRead)
    {
        if (pendingRead is null) return;
        if (pendingRead.IsCompleted)
        {
            _ = pendingRead.Exception;
            return;
        }

        // Normal Close/EOF has already consumed and cleared nextFrame. A read remains
        // only when the loop is unwinding an exception, such as a broken stdout pipe.
        // Cancellation is best effort for a borrowed TextReader: Windows console-pipe
        // reads can ignore cancellation after starting. Awaiting that read here would
        // prevent the failing host from exiting until its peer also closed stdin.
        // Observe a late fault without replacing the primary transport failure. The
        // continuation also covers completion racing the IsCompleted check above.
        _ = pendingRead.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static async Task DrainAsks(TextWriter stdout, List<Task<Response>> pendingAsks)
    {
        foreach (Task<Response> pending in pendingAsks)
            await Write(stdout, await pending.ConfigureAwait(false)).ConfigureAwait(false);
        pendingAsks.Clear();
    }

    private static async Task<Response> Dispatch(
        Request request,
        Func<Request, CancellationToken, Task<Response>> handle,
        CancellationToken cancellationToken = default)
    {
        Response response = await handle(request, cancellationToken).ConfigureAwait(false);
        response.Id = request.Id;
        return response;
    }

    // Internal so the dispatch can be tested without pipes. Main's loop is framing;
    // this is the protocol, and every branch below is an error path a client can reach.
    internal static async Task<Response> Handle(
        Request request,
        HostSession? session,
        Action<HostSession?> setSession,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // MAPPED TO AN ENUM, so the compiler checks this switch rather than a string
            // comparison chain. An op that names nothing is refused here, with the op
            // quoted, instead of reaching the client as a JSON parse failure.
            switch (Protocol.ParseOp(request.Op))
            {
                case Protocol.Op.Ping:
                    return new Response { Ok = true, Version = Version };

                case Protocol.Op.Open:
                    if (session is not null)
                        return Fail("a run is already open on this process");
                    if (request.Config is null)
                        return Fail("open needs a 'config'");
                    HostSession opened = HostSession.Open(request.Config);
                    setSession(opened);
                    return new Response
                    {
                        Ok = true,
                        Version = Version,
                        WorkIdentityVersion = 1,
                        RequiresWorkIdentity = opened.RequiresWorkIdentity,
                        CompatibilityHash = opened.CompatibilityHash,
                    };

                // Ask and close have bodies rather than expressions, and the .editorconfig
                // indents a braced case block twice. They are methods instead: the same code,
                // without the switch growing an extra level of indentation.
                case Protocol.Op.Ask:
                    return session is null
                        ? Fail("no run is open")
                        : await Ask(request, session, cancellationToken).ConfigureAwait(false);

                case Protocol.Op.Tell:
                    if (session is null) return Fail("no run is open");
                    if (request.Results is null) return Fail("tell needs 'results'");
                    if (request.Results.Count > ProtocolLimits.MaxResults)
                        return Fail($"tell carries {request.Results.Count} results, more than the {ProtocolLimits.MaxResults} limit");
                    return new Response { Ok = true, Accepted = session.Tell(request.Results) };

                case Protocol.Op.Status:
                    if (session is null) return Fail("no run is open");
                    return new Response { Ok = true, Complete = session.IsComplete };

                case Protocol.Op.Close:
                    if (session is null) return new Response { Ok = true };
                    return await Close(session, setSession).ConfigureAwait(false);

                default:
                    return Fail($"unknown op '{request.Op}'");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail("request was canceled");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // DELIBERATELY GENERAL. This is a protocol boundary: whatever went wrong belongs on
        // the wire as an error the client can read, not on stderr where a spawned process
        // sends it nowhere. An unhandled throw here would kill the process mid-run and the
        // client would see a closed pipe instead of a reason.
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private static async Task<Response> Ask(
        Request request, HostSession session, CancellationToken cancellationToken)
    {
        List<Candidate> candidates =
            await session.AskAsync(request.Max <= 0 ? 1 : request.Max, cancellationToken).ConfigureAwait(false);
        return new Response
        {
            Ok = true,
            Candidates = candidates,
            // An empty batch is the completion signal, so it is stated rather than left for
            // the client to infer from the array being empty.
            Complete = candidates.Count == 0,
        };
    }

    private static async Task<Response> Close(HostSession session, Action<HostSession?> setSession)
    {
        (Candidate? best, string? stopReason) = await session.FinishAsync().ConfigureAwait(false);
        session.Dispose();
        setSession(null);
        return new Response { Ok = true, Best = best, StopReason = stopReason };
    }

    private static Response Fail(string error) => new() { Ok = false, Error = error };

    private static Task Write(TextWriter stdout, Response response) =>
        stdout.WriteLineAsync(Protocol.Serialize(response));
}

/// <summary>Reads newline-terminated frames, refusing to assemble an oversized one.</summary>
/// <remarks>
/// <para>
/// NOT <c>ReadLineAsync</c>, which grows a string until it finds a newline. A peer that
/// never sends one -- a bug, a wedged client, a hostile one -- walks this process out of
/// memory, and all anyone sees is that the host died. Here the frame is abandoned at the
/// limit and the remainder skipped, so the connection resynchronises on the next newline
/// and the client is told why.
/// </para>
/// <para>
/// CHUNKED, NOT CHARACTER BY CHARACTER. Awaiting one character at a time is correct and
/// unusably slow at the sizes this limit permits: 16 million awaits to reject one frame
/// turns the memory bound into a CPU bound. Reading blocks and scanning them keeps the
/// leftover in a buffer between calls, which is the only state this needs.
/// </para>
/// </remarks>
internal sealed class FrameReader
{
    private const int ChunkChars = 8192;

    private readonly TextReader _reader;
    private readonly char[] _chunk = new char[ChunkChars];
    private readonly StringBuilder _frame = new();

    private int _length;
    private int _offset;
    private bool _overlong;

    /// <summary>A CR that may turn out to be the first half of the delimiter.</summary>
    /// <remarks>
    /// INSTANCE STATE, because a CRLF can straddle two reads. Held rather than dropped:
    /// discarding every CR before the size check let a peer send more than
    /// <see cref="ProtocolLimits.MaxFrameChars"/> carriage returns followed by valid
    /// JSON, and the frame sailed past the limit it was supposed to be measured
    /// against. Only the CR immediately before the LF is free; every other one is
    /// content and is charged as content.
    /// </remarks>
    private bool _pendingCarriageReturn;

    internal FrameReader(TextReader reader) => _reader = reader;

    /// <summary>The next frame, or a null line at end of input.</summary>
    internal async Task<(string? Line, bool Overlong)> NextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_offset >= _length)
            {
                _length = await _reader.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_length == 0)
                {
                    // A CR held at end of input was never a delimiter, so it is content.
                    if (_pendingCarriageReturn)
                    {
                        Charge('\r');
                        _pendingCarriageReturn = false;
                    }
                    // End of input. A frame without a trailing newline is still a frame.
                    if (_frame.Length == 0 && !_overlong) return (null, false);
                    return Take();
                }
            }

            for (; _offset < _length; _offset += 1)
            {
                char c = _chunk[_offset];

                if (c == '\r')
                {
                    // Might be the delimiter's CR, might be content. Two in a row means
                    // the first was content, so it is charged before the second is held.
                    if (_pendingCarriageReturn) Charge('\r');
                    _pendingCarriageReturn = true;
                    continue;
                }

                if (c == '\n')
                {
                    // The held CR was the delimiter's after all, and costs nothing.
                    _pendingCarriageReturn = false;
                    _offset += 1;
                    return Take();
                }

                // A CR followed by anything but LF was content.
                if (_pendingCarriageReturn)
                {
                    Charge('\r');
                    _pendingCarriageReturn = false;
                }
                Charge(c);
            }
        }
    }

    /// <summary>Adds one character to the frame, or marks it overlong.</summary>
    private void Charge(char c)
    {
        if (_overlong) return;
        if (_frame.Length >= ProtocolLimits.MaxFrameChars)
        {
            // Released now rather than at the newline: the point of the limit is not to
            // be holding this much.
            _frame.Clear();
            _overlong = true;
            return;
        }
        _frame.Append(c);
    }

    private (string? Line, bool Overlong) Take()
    {
        string line = _overlong ? string.Empty : _frame.ToString();
        bool overlong = _overlong;
        _frame.Clear();
        _overlong = false;
        _pendingCarriageReturn = false;
        return (line, overlong);
    }
}
