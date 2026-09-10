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

    private static async Task<int> Main()
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

        var frames = new FrameReader(stdin);

        HostSession? session = null;
        try
        {
            while (true)
            {
                (string? line, bool overlong) = await frames.NextAsync().ConfigureAwait(false);
                if (line is null) break;
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
                if (line.Length == 0) continue;

                Request? request = Protocol.ParseRequest(line, out string? parseError, out long id);
                if (request is null)
                {
                    await Write(stdout, new Response { Ok = false, Error = parseError, Id = id })
                        .ConfigureAwait(false);
                    continue;
                }

                Response response = await Handle(request, session, s => session = s).ConfigureAwait(false);
                response.Id = request.Id;
                await Write(stdout, response).ConfigureAwait(false);

                if (Protocol.ParseOp(request.Op) == Protocol.Op.Close) break;
            }
        }
        finally
        {
            session?.Dispose();
        }

        return 0;
    }

    private static async Task<Response> Handle(
        Request request,
        HostSession? session,
        Action<HostSession?> setSession)
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
                    setSession(HostSession.Open(request.Config));
                    return new Response { Ok = true, Version = Version };

                // Ask and close have bodies rather than expressions, and the .editorconfig
                // indents a braced case block twice. They are methods instead: the same code,
                // without the switch growing an extra level of indentation.
                case Protocol.Op.Ask:
                    return session is null
                        ? Fail("no run is open")
                        : await Ask(request, session).ConfigureAwait(false);

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

    private static async Task<Response> Ask(Request request, HostSession session)
    {
        List<Candidate> candidates =
            await session.AskAsync(request.Max <= 0 ? 1 : request.Max, CancellationToken.None).ConfigureAwait(false);
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

    internal FrameReader(TextReader reader) => _reader = reader;

    /// <summary>The next frame, or a null line at end of input.</summary>
    internal async Task<(string? Line, bool Overlong)> NextAsync()
    {
        while (true)
        {
            if (_offset >= _length)
            {
                _length = await _reader.ReadAsync(_chunk, 0, ChunkChars).ConfigureAwait(false);
                _offset = 0;
                if (_length == 0)
                {
                    // End of input. A frame without a trailing newline is still a frame.
                    if (_frame.Length == 0 && !_overlong) return (null, false);
                    return Take();
                }
            }

            for (; _offset < _length; _offset += 1)
            {
                char c = _chunk[_offset];
                if (c == '\n')
                {
                    _offset += 1;
                    return Take();
                }
                // A lone CR before the LF is dropped: a client on Windows may send CRLF,
                // and the payload is JSON, where trailing whitespace is insignificant.
                if (c == '\r') continue;
                if (_overlong) continue;

                if (_frame.Length >= ProtocolLimits.MaxFrameChars)
                {
                    // Released now rather than at the newline: the point of the limit is
                    // not to be holding this much.
                    _frame.Clear();
                    _overlong = true;
                    continue;
                }
                _frame.Append(c);
            }
        }
    }

    private (string? Line, bool Overlong) Take()
    {
        string line = _overlong ? string.Empty : _frame.ToString();
        bool overlong = _overlong;
        _frame.Clear();
        _overlong = false;
        return (line, overlong);
    }
}
