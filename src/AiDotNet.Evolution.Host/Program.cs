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

        HostSession? session = null;
        try
        {
            string? line;
            while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;

                Request? request = Protocol.ParseRequest(line, out string? parseError);
                if (request is null)
                {
                    await Write(stdout, new Response { Ok = false, Error = parseError }).ConfigureAwait(false);
                    continue;
                }

                Response response = await Handle(request, session, s => session = s).ConfigureAwait(false);
                response.Id = request.Id;
                await Write(stdout, response).ConfigureAwait(false);

                if (string.Equals(request.Op, "close", StringComparison.Ordinal)) break;
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
            switch (request.Op)
            {
                case "ping":
                    return new Response { Ok = true, Version = Version };

                case "open":
                    if (session is not null)
                        return Fail("a run is already open on this process");
                    if (request.Config is null)
                        return Fail("open needs a 'config'");
                    setSession(HostSession.Open(request.Config));
                    return new Response { Ok = true, Version = Version };

                // Ask and close have bodies rather than expressions, and the .editorconfig
                // indents a braced case block twice. They are methods instead: the same code,
                // without the switch growing an extra level of indentation.
                case "ask":
                    return session is null
                        ? Fail("no run is open")
                        : await Ask(request, session).ConfigureAwait(false);

                case "tell":
                    if (session is null) return Fail("no run is open");
                    if (request.Results is null) return Fail("tell needs 'results'");
                    return new Response { Ok = true, Accepted = session.Tell(request.Results) };

                case "status":
                    if (session is null) return Fail("no run is open");
                    return new Response { Ok = true, Complete = session.IsComplete };

                case "close":
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
