using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>What happened on a work server connection, for monitoring.</summary>
public enum EvolutionWorkServerEventKind
{
    /// <summary>The listener is bound and accepting connections.</summary>
    Listening = 0,
    /// <summary>A connection was accepted.</summary>
    Accepted = 1,
    /// <summary>A connection presented the worker token.</summary>
    Authenticated = 2,
    /// <summary>A connection failed the TLS handshake or presented a wrong or missing token, and was closed.</summary>
    AuthenticationRefused = 3,
    /// <summary>An authenticated connection asked for an operation remote workers may not perform.</summary>
    OperationRefused = 4,
    /// <summary>A connection was refused because the server is at its connection limit.</summary>
    ConnectionLimitReached = 5,
    /// <summary>A connection ended with an unexpected error; the event carries the exception.</summary>
    Faulted = 6,
    /// <summary>A connection closed.</summary>
    Closed = 7
}

/// <summary>One work server event.</summary>
public sealed class EvolutionWorkServerEvent
{
    /// <summary>Creates an event.</summary>
    public EvolutionWorkServerEvent(EvolutionWorkServerEventKind kind, string remoteEndPoint, Exception? exception = null)
    {
        Kind = kind;
        RemoteEndPoint = remoteEndPoint ?? string.Empty;
        Exception = exception;
    }

    /// <summary>Gets what happened.</summary>
    public EvolutionWorkServerEventKind Kind { get; }

    /// <summary>Gets the peer's address, or the listening address for <see cref="EvolutionWorkServerEventKind.Listening"/>.</summary>
    public string RemoteEndPoint { get; }

    /// <summary>Gets the error behind a <see cref="EvolutionWorkServerEventKind.Faulted"/> event. It is never sent to the peer.</summary>
    public Exception? Exception { get; }
}

/// <summary>Configures a <see cref="EvolutionWorkServer"/>.</summary>
public sealed class EvolutionWorkServerOptions
{
    /// <summary>Gets or sets the address to listen on. Defaults to all IPv4 interfaces.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Gets or sets the port, or zero for an ephemeral port read back from <see cref="EvolutionWorkServer.LocalEndPoint"/>.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets how many connections may be open at once. Defaults to 64.</summary>
    public int MaximumConnections { get; set; } = 64;

    /// <summary>Gets or sets how long a new connection has to finish TLS and present its token. Defaults to 10 seconds.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how long an authenticated connection may stay silent before it is closed. Defaults to 5 minutes.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets a callback for connection events, including faults with their exceptions.</summary>
    /// <remarks>Called on connection threads. It must be fast and must not throw; an exception from it is swallowed so
    /// monitoring can never take the server down.</remarks>
    public Action<EvolutionWorkServerEvent>? OnEvent { get; set; }

    internal void Validate()
    {
        if (!IPAddress.TryParse(BindAddress ?? string.Empty, out _))
            throw new ArgumentException("BindAddress must be an IP address.", nameof(BindAddress));
        if (Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (MaximumConnections is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(MaximumConnections));
        if (HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));
        if (IdleTimeout <= TimeSpan.Zero || IdleTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
    }
}

/// <summary>Serves a durable work coordinator to remote workers over TLS.</summary>
/// <remarks>
/// <para>
/// Frames are the same newline-delimited JSON objects the <c>--durable</c> host reads on standard input, so a
/// worker speaks one protocol whether the coordinator is a local process or a machine away. The server presents
/// <c>certificate</c>; a worker pins its SHA-256 fingerprint (<see cref="CertificateFingerprint"/>) rather than
/// trusting a certificate authority. The first frame on a connection must be
/// <c>{"op":"authenticate","token":"..."}</c>; anything else, or a wrong token, closes the connection.
/// </para>
/// <para>
/// Remote workers may only claim, heartbeat, commit, and read their own deliveries, unsettled leases and status.
/// Enqueueing, cancelling and reading results stay with the process that owns the coordinator. The token
/// authorises the worker pool, not an individual worker id: any holder can act as any worker id.
/// </para>
/// </remarks>
public sealed class EvolutionWorkServer : IDisposable
{
    private static readonly HashSet<string> WorkerOperations = new(StringComparer.Ordinal)
    {
        "claim", "heartbeat", "commit", "delivery", "unsettled", "status"
    };

    private const int MaximumAuthenticationFrameBytes = 4096;

    private readonly EvolutionWorkProtocol _protocol;
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _tokenHash;
    private readonly EvolutionWorkServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly HashSet<TcpClient> _clients = new();
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>Creates a server for a coordinator this process owns. Call <see cref="Start"/> to listen.</summary>
    /// <param name="coordinator">The coordinator. The server borrows it; disposing the server does not dispose it.</param>
    /// <param name="certificate">The server certificate, with its private key.</param>
    /// <param name="workerToken">The shared secret workers present; at least 16 characters.</param>
    /// <param name="options">Listening and timeout options, or <c>null</c> for the defaults.</param>
    public EvolutionWorkServer(DurableEvolutionWorkCoordinator coordinator, X509Certificate2 certificate, string workerToken,
        EvolutionWorkServerOptions? options = null)
    {
        Guard.NotNull(coordinator);
        Guard.NotNull(certificate);
        if (!certificate.HasPrivateKey) throw new ArgumentException("The server certificate needs its private key.", nameof(certificate));
        if (workerToken is null || workerToken.Trim().Length < 16)
            throw new ArgumentException("The worker token must be at least 16 characters.", nameof(workerToken));
        _options = options ?? new EvolutionWorkServerOptions();
        _options.Validate();
        _protocol = new EvolutionWorkProtocol(coordinator);
        _certificate = certificate;
        _tokenHash = Hash(workerToken);
        CertificateFingerprint = ComputeFingerprint(certificate);
        _listener = new TcpListener(IPAddress.Parse(_options.BindAddress), _options.Port);
    }

    /// <summary>Gets the SHA-256 fingerprint of the server certificate, as uppercase hex, for workers to pin.</summary>
    public string CertificateFingerprint { get; }

    /// <summary>Gets the bound address and port. Valid after <see cref="Start"/>.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Computes the fingerprint a worker pins: SHA-256 over the certificate's DER encoding, as uppercase hex.</summary>
    public static string ComputeFingerprint(X509Certificate certificate)
    {
        Guard.NotNull(certificate);
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(certificate.GetRawCertData());
        var text = new StringBuilder(digest.Length * 2);
        foreach (byte value in digest) text.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>Binds the listener and starts accepting workers.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EvolutionWorkServer));
            if (_acceptLoop is not null) throw new InvalidOperationException("The server is already started.");
            _listener.Start();
            Raise(EvolutionWorkServerEventKind.Listening, LocalEndPoint.ToString());
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            catch (SocketException exception)
            {
                // A reset during accept is the peer's problem, not the listener's; keep serving.
                Raise(EvolutionWorkServerEventKind.Faulted, LocalEndPoint.ToString(), exception);
                continue;
            }

            string peer = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            lock (_sync)
            {
                if (_disposed || _clients.Count >= _options.MaximumConnections)
                {
                    client.Dispose();
                    Raise(EvolutionWorkServerEventKind.ConnectionLimitReached, peer);
                    continue;
                }

                _clients.Add(client);
            }

            Raise(EvolutionWorkServerEventKind.Accepted, peer);
            _ = Task.Run(() => ServeAsync(client, peer));
        }
    }

    private async Task ServeAsync(TcpClient client, string peer)
    {
        using TcpClient connection = client;
        try
        {
            client.NoDelay = true;
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            // One reader per connection: it buffers past the end of a frame, so a second reader would lose bytes.
            // The token frame is tiny; an unauthenticated peer must not be able to make the server buffer 16 MB.
            var reader = new BoundedLineReader(ssl, MaximumAuthenticationFrameBytes);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                handshake.CancelAfter(_options.HandshakeTimeout);
                using CancellationTokenRegistration abort = handshake.Token.Register(client.Dispose);
                if (!await AuthenticateAsync(ssl, reader, peer).ConfigureAwait(false)) return;
            }

            Raise(EvolutionWorkServerEventKind.Authenticated, peer);
            reader.MaximumLineBytes = EvolutionWorkProtocol.MaximumFrameBytes;
            while (!_stopping.IsCancellationRequested)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                {
                    idle.CancelAfter(_options.IdleTimeout);
                    using CancellationTokenRegistration abort = idle.Token.Register(client.Dispose);
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }

                if (line is null) return;
                string reply = Dispatch(line, peer);
                byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                await ssl.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await ssl.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException
            or AuthenticationException or InvalidDataException)
        {
            // A dropped worker, an idle or handshake timeout, or an oversized frame: close this connection only.
            if (!_stopping.IsCancellationRequested) Raise(EvolutionWorkServerEventKind.Faulted, peer, exception);
        }
        finally
        {
            lock (_sync) _clients.Remove(connection);
            Raise(EvolutionWorkServerEventKind.Closed, peer);
        }
    }

    private async Task<bool> AuthenticateAsync(SslStream ssl, BoundedLineReader reader, string peer)
    {
        try
        {
#if NET5_0_OR_GREATER
            await ssl.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false,
                SslProtocols.None, checkCertificateRevocation: false).ConfigureAwait(false);
#else
            await ssl.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false,
                SslProtocols.Tls12, checkCertificateRevocation: false).ConfigureAwait(false);
#endif
        }
        catch (AuthenticationException)
        {
            Raise(EvolutionWorkServerEventKind.AuthenticationRefused, peer);
            return false;
        }

        string? first = await reader.ReadLineAsync().ConfigureAwait(false);
        if (first is not null && TokenMatches(first)) return true;
        Raise(EvolutionWorkServerEventKind.AuthenticationRefused, peer);
        return false;
    }

    private bool TokenMatches(string frame)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(frame);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("op", out JsonElement op) || op.ValueKind != JsonValueKind.String
                || op.GetString() != "authenticate"
                || !root.TryGetProperty("token", out JsonElement token) || token.ValueKind != JsonValueKind.String)
                return false;
            return FixedTimeEquals(Hash(token.GetString() ?? string.Empty), _tokenHash);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string Dispatch(string line, string peer)
    {
        long id = 0;
        string? op = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("id", out JsonElement rawId) && rawId.ValueKind == JsonValueKind.Number
                    && rawId.TryGetInt64(out long value) && value >= 1) id = value;
                if (root.TryGetProperty("op", out JsonElement rawOp) && rawOp.ValueKind == JsonValueKind.String) op = rawOp.GetString();
            }
        }
        catch (JsonException)
        {
            // Let the protocol produce its own bounded parse error.
        }

        if (op is not null && !WorkerOperations.Contains(op))
        {
            Raise(EvolutionWorkServerEventKind.OperationRefused, peer);
            return Refusal(id, "Remote workers may only claim, heartbeat, commit, and read deliveries, unsettled leases and status.");
        }

        return _protocol.ProcessJson(line);
    }

    private static string Refusal(long id, string error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteNumber("protocol", EvolutionWorkProtocol.Version);
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] Hash(string text)
    {
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    // Hashing first makes both operands the same length, so the comparison time does not depend on the token.
    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        int difference = left.Length ^ right.Length;
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private void Raise(EvolutionWorkServerEventKind kind, string endPoint, Exception? exception = null)
    {
        Action<EvolutionWorkServerEvent>? handler = _options.OnEvent;
        if (handler is null) return;
        try
        {
            handler(new EvolutionWorkServerEvent(kind, endPoint, exception));
        }
        catch (Exception callbackFailure) when (callbackFailure is not OutOfMemoryException)
        {
            // Documented: monitoring must never take the server down, and there is nowhere else to report this.
        }
    }

    /// <summary>Stops listening and closes every connection. The coordinator stays open.</summary>
    public void Dispose()
    {
        TcpClient[] open;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            open = _clients.ToArray();
        }

        _stopping.Cancel();
        _listener.Stop();
        foreach (TcpClient client in open) client.Dispose();
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop observed the stop; nothing further to report.
        }

        _stopping.Dispose();
    }
}
