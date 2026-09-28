#if NET5_0_OR_GREATER
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-61 (#174): the coordinator served to remote workers over TLS with a pinned certificate and a token.</summary>
public sealed class EvolutionWorkServerTests : IDisposable
{
    private const string Token = "worker-token-0123456789";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "evolution-work-server-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _certificate = SelfSigned();
    private readonly DurableEvolutionWorkCoordinator _coordinator;
    private readonly EvolutionWorkProtocol _owner;
    private readonly List<EvolutionWorkServerEvent> _events = new();
    private readonly EvolutionWorkServer _server;

    public EvolutionWorkServerTests()
    {
        _coordinator = new DurableEvolutionWorkCoordinator(_directory, "run", "compat", EvolutionResources.Of("cost", 100));
        _owner = new EvolutionWorkProtocol(_coordinator);
        _server = new EvolutionWorkServer(_coordinator, _certificate, Token, new EvolutionWorkServerOptions
        {
            BindAddress = "127.0.0.1",
            OnEvent = e => { lock (_events) _events.Add(e); }
        });
        _server.Start();
    }

    [Fact]
    public async Task A_remote_worker_claims_and_commits_work_the_owner_enqueued()
    {
        Owner("enqueue", "job", Job(1));
        using EvolutionWorkRemoteClient worker = await Connect();

        JsonObject claimed = await Send(worker, "claim", "worker", Worker("remote-1"));
        Assert.True(claimed["available"]!.GetValue<bool>());
        JsonObject lease = claimed["lease"]!.AsObject();
        JsonObject commit = await Send(worker, "commit", "identity", lease["identity"]!.DeepClone(), "workerId", "remote-1",
            "payload", "49", "provenance", "remote-square-v1", "outcome", "completed", "actual", new JsonObject { ["cost"] = "1" });
        Assert.Equal("accepted", commit["disposition"]!.GetValue<string>());

        JsonObject result = Owner("result", "evaluationId", "1", "attempt", 1);
        Assert.Equal("49", result["result"]!["payload"]!.GetValue<string>());
    }

    [Fact]
    public async Task Owner_operations_are_refused_to_remote_workers()
    {
        using EvolutionWorkRemoteClient worker = await Connect();
        foreach (string op in new[] { "enqueue", "cancel", "result", "open", "close" })
        {
            JsonObject reply = JsonNode.Parse(await worker.SendAsync(Request(op).ToJsonString()))!.AsObject();
            Assert.False(reply["ok"]!.GetValue<bool>(), op);
            Assert.Contains("Remote workers may only", reply["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // Control: the same connection still serves a worker operation.
        Assert.True((await Send(worker, "status")).ContainsKey("settled"));
        lock (_events) Assert.Equal(5, _events.Count(e => e.Kind == EvolutionWorkServerEventKind.OperationRefused));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_the_right_one_is_not()
    {
        using EvolutionWorkRemoteClient impostor = await EvolutionWorkRemoteClient.ConnectAsync(
            "127.0.0.1", _server.LocalEndPoint.Port, _server.CertificateFingerprint, "not-the-worker-token-at-all");
        // The server closes the connection: a clean close reads as refusal, a reset as an I/O failure.
        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => impostor.SendAsync(Request("status").ToJsonString()));
        Assert.True(refused is AuthenticationException or IOException, refused.GetType().Name);

        using EvolutionWorkRemoteClient worker = await Connect();
        Assert.True((await Send(worker, "status")).ContainsKey("settled"));
        lock (_events) Assert.Contains(_events, e => e.Kind == EvolutionWorkServerEventKind.AuthenticationRefused);
    }

    [Fact]
    public async Task A_certificate_that_does_not_match_the_pin_is_refused()
    {
        using X509Certificate2 other = SelfSigned();
        await Assert.ThrowsAsync<AuthenticationException>(() => EvolutionWorkRemoteClient.ConnectAsync(
            "127.0.0.1", _server.LocalEndPoint.Port, EvolutionWorkServer.ComputeFingerprint(other), Token));

        using EvolutionWorkRemoteClient pinned = await Connect();
        Assert.True((await Send(pinned, "status")).ContainsKey("settled"));
    }

    [Fact]
    public async Task Two_workers_on_separate_connections_share_the_queue_without_double_claims()
    {
        for (int id = 1; id <= 6; id++) Owner("enqueue", "job", Job(id));
        using EvolutionWorkRemoteClient first = await Connect();
        using EvolutionWorkRemoteClient second = await Connect();

        var claimed = new List<string>();
        foreach ((EvolutionWorkRemoteClient worker, string id) in new[] { (first, "a"), (second, "b"), (first, "a"), (second, "b"), (first, "a"), (second, "b") })
        {
            JsonObject worker0 = Worker(id);
            worker0["maximumConcurrentWork"] = 3;
            JsonObject reply = await Send(worker, "claim", "worker", worker0);
            claimed.Add(reply["lease"]!["identity"]!["evaluationId"]!.GetValue<string>());
        }

        Assert.Equal(6, claimed.Distinct().Count());
    }

    private Task<EvolutionWorkRemoteClient> Connect() =>
        EvolutionWorkRemoteClient.ConnectAsync("127.0.0.1", _server.LocalEndPoint.Port, _server.CertificateFingerprint, Token);

    private static async Task<JsonObject> Send(EvolutionWorkRemoteClient client, string op, params object[] fields)
    {
        JsonObject reply = JsonNode.Parse(await client.SendAsync(Request(op, fields).ToJsonString()))!.AsObject();
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply;
    }

    private JsonObject Owner(string op, params object[] fields)
    {
        JsonObject reply = JsonNode.Parse(_owner.ProcessJson(Request(op, fields).ToJsonString()))!.AsObject();
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply;
    }

    private static JsonObject Job(int id) => new()
    {
        ["evaluationId"] = id.ToString(CultureInfo.InvariantCulture),
        ["attempt"] = 1,
        ["canonicalGenomeId"] = "integer:" + id.ToString(CultureInfo.InvariantCulture),
        ["payload"] = id.ToString(CultureInfo.InvariantCulture),
        ["estimated"] = new JsonObject { ["cost"] = "1" },
        ["maximum"] = new JsonObject { ["cost"] = "5" }
    };

    private static JsonObject Worker(string id) => new() { ["workerId"] = id, ["compatibilityHash"] = "compat" };

    private static JsonObject Request(string op, params object[] fields)
    {
        var request = new JsonObject { ["id"] = 7, ["protocol"] = 1, ["op"] = op };
        for (int i = 0; i < fields.Length; i += 2)
            request[(string)fields[i]] = fields[i + 1] switch
            {
                JsonNode node => node.DeepClone(),
                string text => JsonValue.Create(text),
                int value => JsonValue.Create(value),
                _ => throw new InvalidOperationException()
            };
        return request;
    }

    // SChannel will not serve an ephemeral key, so the certificate is round-tripped through PFX to persist it.
    private static X509Certificate2 SelfSigned()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=evolution-work-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
#else
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#endif
    }

    public void Dispose()
    {
        _server.Dispose();
        _owner.Dispose();
        _coordinator.Dispose();
        _certificate.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
#endif
