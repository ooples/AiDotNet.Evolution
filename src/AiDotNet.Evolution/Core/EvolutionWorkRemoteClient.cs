using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>A worker's connection to an <see cref="EvolutionWorkServer"/>.</summary>
/// <remarks>
/// The server's certificate is accepted only when its SHA-256 fingerprint matches the pinned one, whatever its
/// issuer or name; that pin, not a certificate authority, is what proves the worker reached its coordinator.
/// Requests are exchanged one at a time. After a transport failure the connection is unusable: reconnect and
/// reconcile with the unsettled-lease query rather than assuming an unacknowledged commit failed.
/// </remarks>
public sealed class EvolutionWorkRemoteClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _ssl;
    private readonly BoundedLineReader _reader;
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private bool _failed;
    private bool _disposed;

    private EvolutionWorkRemoteClient(TcpClient client, SslStream ssl)
    {
        _client = client;
        _ssl = ssl;
        _reader = new BoundedLineReader(ssl, EvolutionWorkProtocol.MaximumFrameBytes);
    }

    /// <summary>Connects, verifies the pinned certificate and presents the worker token.</summary>
    /// <param name="host">The coordinator's host name or address.</param>
    /// <param name="port">The coordinator's port.</param>
    /// <param name="certificateFingerprint">The server certificate's SHA-256 fingerprint, as hex (case and colons ignored).</param>
    /// <param name="workerToken">The shared worker token.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <exception cref="AuthenticationException">The certificate did not match the pin, or the server refused the token.</exception>
    public static async Task<EvolutionWorkRemoteClient> ConnectAsync(string host, int port, string certificateFingerprint,
        string workerToken, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(host);
        Guard.NotNullOrWhiteSpace(certificateFingerprint);
        Guard.NotNullOrWhiteSpace(workerToken);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        string pinned = certificateFingerprint.Replace(":", string.Empty).Trim().ToUpperInvariant();

        var client = new TcpClient { NoDelay = true };
        try
        {
            using (cancellationToken.Register(client.Dispose))
            {
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                    (_, certificate, _, _) => certificate is not null
                        && string.Equals(EvolutionWorkServer.ComputeFingerprint(certificate), pinned, StringComparison.Ordinal));
#if NET5_0_OR_GREATER
                await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.None, checkCertificateRevocation: false).ConfigureAwait(false);
#else
                await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, checkCertificateRevocation: false).ConfigureAwait(false);
#endif
                var connection = new EvolutionWorkRemoteClient(client, ssl);
                await connection.WriteAsync(AuthenticationFrame(workerToken)).ConfigureAwait(false);
                // The server acknowledges an accepted token and closes the connection on a refused one.
                string? acknowledgement = await connection._reader.ReadLineAsync().ConfigureAwait(false);
                if (acknowledgement is null)
                    throw new AuthenticationException("The coordinator refused the worker token.");
                if (!string.Equals(acknowledgement, EvolutionWorkProtocol.AuthenticatedFrame, StringComparison.Ordinal))
                    throw new AuthenticationException("The coordinator did not acknowledge the worker token.");
                return connection;
            }
        }
        catch (Exception exception) when (exception is not AuthenticationException && cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new OperationCanceledException("The connection attempt was canceled.", exception, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends one protocol request and returns the coordinator's reply.</summary>
    /// <param name="requestJson">One protocol JSON object without a trailing newline.</param>
    /// <param name="cancellationToken">Closes the connection if the reply does not arrive in time.</param>
    /// <exception cref="AuthenticationException">The server closed the connection, most often because the token was refused.</exception>
    public async Task<string> SendAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(requestJson);
        if (requestJson.IndexOf('\n') >= 0) throw new ArgumentException("A request must be a single line.", nameof(requestJson));
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EvolutionWorkRemoteClient));
            if (_failed) throw new InvalidOperationException("The connection failed earlier; reconnect and reconcile.");
            using (cancellationToken.Register(_client.Dispose))
            {
                try
                {
                    await WriteAsync(requestJson).ConfigureAwait(false);
                    return await _reader.ReadLineAsync().ConfigureAwait(false)
                        ?? throw new AuthenticationException("The coordinator closed the connection; the worker token may have been refused.");
                }
                catch
                {
                    _failed = true;
                    throw;
                }
            }
        }
        finally
        {
            _exchange.Release();
        }
    }

    private async Task WriteAsync(string frame)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame + "\n");
        await _ssl.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await _ssl.FlushAsync().ConfigureAwait(false);
    }

    private static string AuthenticationFrame(string token)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "authenticate");
            writer.WriteString("token", token);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Closes the connection. Leases this worker holds stay leased until they expire or are committed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ssl.Dispose();
        _client.Dispose();
        _exchange.Dispose();
    }
}
