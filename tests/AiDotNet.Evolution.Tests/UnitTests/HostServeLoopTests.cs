#if !NET471
using System.Text;
using System.Threading.Channels;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The framing loop: what a client gets back for each line it sends.
/// </summary>
/// <remarks>
/// Driven through <see cref="Program.ServeAsync"/> with string streams rather than by
/// spawning the binary. Every case below is a MALFORMED or hostile input -- the paths a
/// well-behaved client never takes and a broken one always does, and the ones where the
/// difference between a usable error and a closed pipe is decided.
/// </remarks>
public sealed class HostServeLoopTests
{
    /// <summary>Feeds the loop a script and returns one parsed response per line out.</summary>
    private static async Task<List<Response>> ServeAsync(params string[] lines)
    {
        var input = new StringReader(string.Join("\n", lines) + "\n");
        var output = new StringWriter();

        int code = await Program.ServeAsync(input, output);
        Assert.Equal(0, code);

        var responses = new List<Response>();
        foreach (string line in output.ToString().Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            responses.Add(Assert.IsType<Response>(System.Text.Json.JsonSerializer.Deserialize<Response>(line)));
        }
        return responses;
    }

    [Fact]
    public async Task PingIsAnsweredAndTheIdComesBack()
    {
        List<Response> out_ = await ServeAsync("{\"op\":\"ping\",\"id\":11}");

        Assert.Single(out_);
        Assert.True(out_[0].Ok);
        Assert.Equal(11, out_[0].Id);
    }

    [Fact]
    public async Task BlankLinesAreIgnoredRatherThanAnswered()
    {
        // A client that writes a trailing newline, or a heartbeat, must not receive an
        // error for it -- and must not have its next real request thrown off.
        List<Response> out_ = await ServeAsync("", "   ", "{\"op\":\"ping\",\"id\":1}", "");

        Assert.Single(out_);
        Assert.Equal(1, out_[0].Id);
    }

    [Fact]
    public async Task MalformedJsonIsAnsweredWithoutKillingTheLoop()
    {
        List<Response> out_ = await ServeAsync("{\"op\":", "{\"op\":\"ping\",\"id\":2}");

        Assert.Equal(2, out_.Count);
        Assert.False(out_[0].Ok);
        Assert.Contains("malformed JSON", out_[0].Error, StringComparison.Ordinal);
        Assert.True(out_[1].Ok);
        Assert.Equal(2, out_[1].Id);
    }

    [Fact]
    public async Task AParseableRequestWithNoOpEchoesItsId()
    {
        // The whole reason the parse and the validation are separate: this frame is good
        // JSON with a bad op, and answering it with id 0 leaves the client unable to
        // match the error to the request.
        List<Response> out_ = await ServeAsync("{\"id\":42,\"op\":\"\"}");

        Assert.Single(out_);
        Assert.False(out_[0].Ok);
        Assert.Equal(42, out_[0].Id);
    }

    [Fact]
    public async Task AnOversizedFrameIsRefusedAndTheNextOneIsStillServed()
    {
        // The frame is abandoned rather than assembled, so there is no id to echo; what
        // matters is that the connection resynchronises on the next newline instead of
        // the process dying with the client seeing only a closed pipe.
        var oversized = new StringBuilder();
        oversized.Append('x', ProtocolLimits.MaxFrameChars + 5);

        List<Response> out_ = await ServeAsync(oversized.ToString(), "{\"op\":\"ping\",\"id\":3}");

        Assert.Equal(2, out_.Count);
        Assert.False(out_[0].Ok);
        Assert.Contains("frame exceeds", out_[0].Error, StringComparison.Ordinal);
        Assert.True(out_[1].Ok);
        Assert.Equal(3, out_[1].Id);
    }

    [Fact]
    public async Task CloseEndsTheLoopAndAnythingAfterItIsNotRead()
    {
        // `close` is the client saying it is done. Continuing to serve after it would
        // mean answering a peer that has already stopped listening.
        List<Response> out_ = await ServeAsync(
            "{\"op\":\"close\",\"id\":4}",
            "{\"op\":\"ping\",\"id\":5}");

        Assert.Single(out_);
        Assert.Equal(4, out_[0].Id);
        Assert.True(out_[0].Ok);
    }

    [Fact]
    public async Task EndOfInputEndsTheLoopCleanly()
    {
        // A client that exits without saying close -- killed, crashed -- must not make
        // this hang or fail. The exit code is asserted inside the helper.
        List<Response> out_ = await ServeAsync("{\"op\":\"ping\",\"id\":6}");

        Assert.Single(out_);
    }

    [Fact]
    public async Task AWholeRunCanBeDrivenThroughTheLoop()
    {
        // The happy path end to end, in process: open, ask, tell, close. This is the
        // only test that exercises the loop's session hand-off, which is what lets one
        // request's `open` be visible to the next request's `ask`.
        string config = "{\"parameters\":[{\"name\":\"x\",\"min\":0,\"max\":1}],"
            + "\"descriptors\":[{\"name\":\"x\",\"min\":0,\"max\":1,\"bins\":4}],"
            + "\"maxProposals\":4,\"maxEvaluations\":4,\"batchSize\":2}";

        var input = new StringReader(
            "{\"op\":\"open\",\"id\":1,\"config\":" + config + "}\n"
            + "{\"op\":\"ask\",\"id\":2,\"max\":2}\n");
        var output = new StringWriter();
        Assert.Equal(0, await Program.ServeAsync(input, output));

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        Response opened = Assert.IsType<Response>(System.Text.Json.JsonSerializer.Deserialize<Response>(lines[0]));
        Response asked = Assert.IsType<Response>(System.Text.Json.JsonSerializer.Deserialize<Response>(lines[1]));

        Assert.True(opened.Ok);
        Assert.True(asked.Ok);
        Assert.NotEmpty(Assert.IsType<List<Candidate>>(asked.Candidates));
    }

    [Fact]
    public Task APendingAskDoesNotBlockTheTellThatLetsItProgress() =>
        WithPendingAsk(async (input, output, first) =>
        {
            input.Send(new Request
            {
                Op = "tell",
                Id = 4,
                Results = new List<TellResult>
                {
                    new()
                    {
                        EvaluationId = first.EvaluationId,
                        Quality = 1,
                        Descriptors = first.Parameters.ToDictionary(pair => pair.Key, pair => (double?)pair.Value, StringComparer.Ordinal),
                    },
                },
            });

            var responses = new Dictionary<long, Response>();
            for (int i = 0; i < 2; i += 1)
            {
                Response response = await output.NextAsync();
                responses.Add(response.Id, response);
            }
            Assert.True(responses[4].Ok);
            Assert.Equal(1, responses[4].Accepted);
            Assert.True(responses[3].Ok);
            Assert.False(responses[3].Complete);
            Candidate next = Assert.Single(Assert.IsType<List<Candidate>>(responses[3].Candidates));
            Assert.NotEqual(first.EvaluationId, next.EvaluationId);

            input.Send(new Request { Op = "close", Id = 5 });
            Response closed = await output.NextAsync();
            Assert.Equal(5, closed.Id);
            Assert.True(closed.Ok);
            Assert.Equal(1.0, Assert.IsType<Candidate>(closed.Best).Quality);
        });

    [Fact]
    public Task CloseSettlesAPendingAskBeforeDisposingTheSession() =>
        WithPendingAsk(async (input, output, _) =>
        {
            input.Send(new Request { Op = "close", Id = 4 });

            AssertCanceledAsk(await output.NextAsync(), 3);
            Response closed = await output.NextAsync();
            Assert.Equal(4, closed.Id);
            Assert.True(closed.Ok);
            Assert.Null(closed.Best);
        });

    [Fact]
    public Task EndOfInputSettlesAPendingAskWithoutReportingFalseCompletion() =>
        WithPendingAsk(async (input, output, _) =>
        {
            input.Complete();
            AssertCanceledAsk(await output.NextAsync(), 3);
        });

    [Fact]
    public Task PendingAsksAreBoundedWithoutBlockingStatusOrClose() =>
        WithPendingAsk(async (input, output, _) =>
        {
            // Ask 3 is already waiting. Fill the remaining slots, then send one extra.
            for (int i = 1; i <= Program.MaxPendingAsks; i += 1)
                input.Send(new Request { Op = "ask", Id = 3 + i, Max = 1 });

            Response refused = await output.NextAsync();
            Assert.Equal(3 + Program.MaxPendingAsks, refused.Id);
            Assert.False(refused.Ok);
            Assert.Equal($"at most {Program.MaxPendingAsks} asks may wait for results at once", refused.Error);
            Assert.Null(refused.Candidates);
            Assert.Null(refused.Complete);

            input.Send(new Request { Op = "status", Id = 100 });
            Response status = await output.NextAsync();
            Assert.Equal(100, status.Id);
            Assert.True(status.Ok);
            Assert.False(status.Complete);

            input.Send(new Request { Op = "close", Id = 101 });
            for (int i = 0; i < Program.MaxPendingAsks; i += 1)
                AssertCanceledAsk(await output.NextAsync(), 3 + i);
            Response closed = await output.NextAsync();
            Assert.Equal(101, closed.Id);
            Assert.True(closed.Ok);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFailureCancelsCooperativeReadAndPreservesTheFirstError(bool readFaultsOnCancellation)
    {
        var writeError = new IOException("the response pipe failed");
        using var input = new RequestInput(
            cancellationFailure: readFaultsOnCancellation ? new IOException("secondary read failure") : null);
        using var output = new ResponseOutput(writeError);
        var responseReady = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Send(new Request { Op = "ask", Id = 7, Max = 1 });
        Task<int> serving = Program.ServeRequestsAsync(input, output,
            (request, token) => GatedAsk(request, token, responseReady.Task, dispatchSettled));
        try
        {
            await input.WaitForPendingReadAsync();
            Assert.Equal(1, input.ActiveReads);
            Assert.False(serving.IsCompleted);
            responseReady.SetResult(new Response { Ok = true, Candidates = new List<Candidate>() });

            IOException actual = await Assert.ThrowsAsync<IOException>(
                () => serving.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Same(writeError, actual);
            Assert.Equal(7, Assert.IsType<Response>(output.LastAttempt).Id);
            await input.WaitForReadSettlementAsync();
            Assert.True(input.CancellationObserved);
            Assert.Equal(0, input.ActiveReads);
            Assert.False(await dispatchSettled.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            input.Complete();
            responseReady.TrySetResult(new Response { Ok = true });
            await ObserveExpectedTransportFailure(serving);
        }
    }

    [Fact]
    public async Task WriteFailureDoesNotWaitForANonCooperativeBorrowedReader()
    {
        var writeError = new IOException("the response pipe failed");
        using var input = new RequestInput(honorCancellation: false);
        using var output = new ResponseOutput(writeError);
        var responseReady = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Send(new Request { Op = "ask", Id = 7, Max = 1 });
        Task<int> serving = Program.ServeRequestsAsync(input, output,
            (request, token) => GatedAsk(request, token, responseReady.Task, dispatchSettled));
        try
        {
            await input.WaitForPendingReadAsync();
            responseReady.SetResult(new Response { Ok = true, Candidates = new List<Candidate>() });
            Assert.Same(writeError, await Assert.ThrowsAsync<IOException>(
                () => serving.WaitAsync(TimeSpan.FromSeconds(30))));
            Assert.Equal(1, input.ActiveReads);

            // A Windows console-pipe read can remain blocked after cancellation. Its
            // later error must not replace the write error or require stdout to recover.
            input.Fail(new IOException("the abandoned read failed later"));
            await input.WaitForReadSettlementAsync();
            Assert.Equal(0, input.ActiveReads);
            Assert.Same(writeError, await Assert.ThrowsAsync<IOException>(() => serving));
            Assert.False(await dispatchSettled.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            input.Complete();
            responseReady.TrySetResult(new Response { Ok = true });
            await ObserveExpectedTransportFailure(serving);
        }
    }

    [Fact]
    public async Task ReadFailureSettlesPendingAsksAndPreservesTheReadError()
    {
        var readError = new IOException("the request pipe failed");
        using var input = new RequestInput();
        using var output = new ResponseOutput();
        var responseReady = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Send(new Request { Op = "ask", Id = 7, Max = 1 });
        Task<int> serving = Program.ServeRequestsAsync(input, output,
            (request, token) => GatedAsk(request, token, responseReady.Task, dispatchSettled));
        try
        {
            await input.WaitForPendingReadAsync();
            Assert.False(responseReady.Task.IsCompleted);
            input.Fail(readError);
            Assert.Same(readError, await Assert.ThrowsAsync<IOException>(
                () => serving.WaitAsync(TimeSpan.FromSeconds(30))));
            Assert.True(await dispatchSettled.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, input.ActiveReads);
            Assert.Null(output.LastAttempt);
        }
        finally
        {
            input.Complete();
            responseReady.TrySetResult(new Response { Ok = true });
            await ObserveExpectedTransportFailure(serving);
        }
    }

    private static async Task<Response> GatedAsk(Request request, CancellationToken cancellationToken,
        Task<Response> responseReady, TaskCompletionSource<bool> settled)
    {
        Assert.Equal(Protocol.Op.Ask, Protocol.ParseOp(request.Op));
        bool canceled = false;
        try
        {
            return await responseReady.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
            return new Response { Ok = false, Error = "request was canceled" };
        }
        finally
        {
            settled.TrySetResult(canceled);
        }
    }

    private static async Task ObserveExpectedTransportFailure(Task<int> serving)
    {
        try { await serving.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (IOException) { /* The test asserts the original transport exception. */ }
    }

    private static void AssertCanceledAsk(Response response, long id)
    {
        Assert.Equal(id, response.Id);
        Assert.False(response.Ok);
        Assert.Equal("request was canceled", response.Error);
        Assert.Null(response.Candidates);
        Assert.Null(response.Complete);
    }

    private static async Task WithPendingAsk(Func<RequestInput, ResponseOutput, Candidate, Task> exercise)
    {
        using var input = new RequestInput();
        using var output = new ResponseOutput();
        Task<int> serving = Program.ServeAsync(input, output);
        try
        {
            input.Send(new Request
            {
                Op = "open",
                Id = 1,
                Config = new RunConfig
                {
                    Parameters = { new ParameterConfig { Name = "x", Min = 0, Max = 1 } },
                    Descriptors = { new DescriptorConfig { Name = "x", Min = 0, Max = 1, Bins = 4 } },
                    MaxProposals = 8,
                    MaxEvaluations = 8,
                    BatchSize = 2,
                },
            });
            Response opened = await output.NextAsync();
            Assert.Equal(1, opened.Id);
            Assert.True(opened.Ok);

            input.Send(new Request { Op = "ask", Id = 2, Max = 1 });
            Response asked = await output.NextAsync();
            Assert.Equal(2, asked.Id);
            Assert.True(asked.Ok);
            Candidate first = Assert.Single(Assert.IsType<List<Candidate>>(asked.Candidates));

            // With the first candidate still awaiting Tell, this second Ask cannot
            // complete yet. Subsequent commands must remain readable while it waits.
            input.Send(new Request { Op = "ask", Id = 3, Max = 1 });
            await exercise(input, output, first);
        }
        finally
        {
            input.Complete();
            Assert.Equal(0, await serving.WaitAsync(TimeSpan.FromSeconds(30)));
        }
    }

    private sealed class RequestInput : TextReader
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<bool> _readWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _readSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _honorCancellation;
        private readonly Exception? _cancellationFailure;
        private string _current = string.Empty;
        private int _offset;
        private int _activeReads;
        private Exception? _readFailure;

        internal RequestInput(bool honorCancellation = true, Exception? cancellationFailure = null)
        {
            _honorCancellation = honorCancellation;
            _cancellationFailure = cancellationFailure;
        }

        internal int ActiveReads => Volatile.Read(ref _activeReads);
        internal bool CancellationObserved { get; private set; }
        internal Task WaitForPendingReadAsync() => _readWaiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        internal Task WaitForReadSettlementAsync() => _readSettled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        internal void Send(Request request)
        {
            string frame = System.Text.Json.JsonSerializer.Serialize(request, HostJsonContext.Default.Request) + "\n";
            if (!_frames.Writer.TryWrite(frame)) throw new InvalidOperationException("The test input is closed.");
        }

        internal void Complete() => _frames.Writer.TryComplete();

        internal void Fail(Exception error)
        {
            _readFailure = error;
            Complete();
        }

        public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
            ReadCoreAsync(buffer.AsMemory(index, count), CancellationToken.None).AsTask();

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, cancellationToken);

        private async ValueTask<int> ReadCoreAsync(Memory<char> buffer, CancellationToken cancellationToken)
        {
            if (_offset == _current.Length)
            {
                ValueTask<bool> ready = _frames.Reader.WaitToReadAsync(
                    _honorCancellation ? cancellationToken : CancellationToken.None);
                bool waiting = !ready.IsCompleted;
                if (waiting)
                {
                    Interlocked.Increment(ref _activeReads);
                    _readWaiting.TrySetResult(true);
                }
                try
                {
                    if (!await ready)
                    {
                        if (_readFailure is not null) throw _readFailure;
                        return 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    if (_cancellationFailure is not null) throw _cancellationFailure;
                    throw;
                }
                finally
                {
                    if (waiting)
                    {
                        Interlocked.Decrement(ref _activeReads);
                        _readSettled.TrySetResult(true);
                    }
                }
                if (!_frames.Reader.TryRead(out string? frame))
                    throw new InvalidOperationException("The signaled test frame is missing.");
                _current = frame;
                _offset = 0;
            }
            int copied = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Complete();
            base.Dispose(disposing);
        }
    }

    private sealed class ResponseOutput : TextWriter
    {
        private readonly Channel<Response> _responses = Channel.CreateUnbounded<Response>();
        private readonly Exception? _writeFailure;

        internal ResponseOutput(Exception? writeFailure = null) => _writeFailure = writeFailure;
        internal Response? LastAttempt { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        internal Task<Response> NextAsync() =>
            _responses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public override Task WriteLineAsync(string? value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            Response response = Assert.IsType<Response>(
                System.Text.Json.JsonSerializer.Deserialize(value, HostJsonContext.Default.Response));
            LastAttempt = response;
            if (_writeFailure is not null) return Task.FromException(_writeFailure);
            if (!_responses.Writer.TryWrite(response)) throw new InvalidOperationException("The test output is closed.");
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _responses.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
#endif
