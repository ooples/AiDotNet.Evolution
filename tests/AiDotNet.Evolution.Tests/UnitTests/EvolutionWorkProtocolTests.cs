using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionWorkProtocolTests
{
    [Fact]
    public void ReopenReconcilesExactLargeIdentitiesAndDecimalReceipts()
    {
        using var fixture = new Fixture(); JsonObject lease;
        using (var endpoint = fixture.Open())
        {
            Ok(endpoint, "enqueue", "job", Job());
            var claimed = Ok(endpoint, "claim", "worker", Worker("first"));
            Assert.True(claimed["available"]!.GetValue<bool>()); lease = claimed["lease"]!.AsObject();
            Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), lease["identity"]!["evaluationId"]!.GetValue<string>());
            var missing = Ok(endpoint, "claim", "worker", Worker("second"));
            Assert.False(missing["available"]!.GetValue<bool>()); Assert.Null(missing["lease"]); Assert.False(missing.ContainsKey("complete"));
        }
        using (var restored = fixture.Open())
        {
            var status = Ok(restored, "status"); Assert.True(status["wasRecovered"]!.GetValue<bool>());
            Assert.Equal("5", status["reserved"]!["cost"]!.GetValue<string>());
            var recovered = Ok(restored, "unsettled", "workerId", "first");
            Assert.True(recovered["reconciliationOnly"]!.GetValue<bool>());
            Assert.Equal(lease.ToJsonString(), recovered["lease"]!.ToJsonString());
            var commit = Commit(lease, "0.1234567890123456789012345678");
            Assert.Equal("accepted", Call(restored, commit)["disposition"]!.GetValue<string>());
            Assert.Equal("duplicate", Call(restored, commit)["disposition"]!.GetValue<string>());
        }
        using var final = fixture.Open();
        var result = Ok(final, "result", "evaluationId", long.MaxValue.ToString(CultureInfo.InvariantCulture), "attempt", 1)["result"]!;
        Assert.Equal("49", result["payload"]!.GetValue<string>());
        Assert.Equal("0.1234567890123456789012345678", result["actual"]!["cost"]!.GetValue<string>());
        var finalStatus = Ok(final, "status"); Assert.Equal("1", finalStatus["settled"]!.GetValue<string>());
        Assert.Equal("0", finalStatus["reserved"]!["cost"]!.GetValue<string>());
        Assert.False(finalStatus["supportsExactSearchContinuation"]!.GetValue<bool>());
        Assert.Contains("fork", finalStatus["searchContinuationGuarantee"]!.GetValue<string>());
    }

    [Fact]
    public void LeaseExpiryAndCancellationCannotRefundOrReplaceAnotherResult()
    {
        using var fixture = new Fixture(); using var endpoint = fixture.Open();
        Ok(endpoint, "enqueue", "job", Job());
        var first = Ok(endpoint, "claim", "worker", Worker("first"))["lease"]!.AsObject();
        fixture.Now = fixture.Now.AddSeconds(11);
        Assert.Equal("expired", Ok(endpoint, "heartbeat", "identity", first["identity"]!.DeepClone(), "workerId", "first")["status"]!.GetValue<string>());
        var second = Ok(endpoint, "claim", "worker", Worker("second"))["lease"]!.AsObject();
        Assert.NotEqual(first["identity"]!["leaseId"]!.GetValue<string>(), second["identity"]!["leaseId"]!.GetValue<string>());
        Assert.Equal("stale", Call(endpoint, Commit(first, "2"))["disposition"]!.GetValue<string>());
        Assert.Equal("duplicate-stale", Call(endpoint, Commit(first, "2"))["disposition"]!.GetValue<string>());
        Assert.True(Ok(endpoint, "cancel", "evaluationId", long.MaxValue.ToString(CultureInfo.InvariantCulture), "attempt", 1)["canceled"]!.GetValue<bool>());
        Assert.Equal("canceled", Ok(endpoint, "heartbeat", "identity", second["identity"]!.DeepClone(), "workerId", "second")["status"]!.GetValue<string>());
        Assert.Equal("5", Ok(endpoint, "status")["reserved"]!["cost"]!.GetValue<string>());
        Assert.Equal("stale", Call(endpoint, Commit(second, "3"))["disposition"]!.GetValue<string>());
        Assert.Null(Ok(endpoint, "result", "evaluationId", long.MaxValue.ToString(CultureInfo.InvariantCulture), "attempt", 1)["result"]);
        Assert.Equal("5", Ok(endpoint, "status")["spent"]!["cost"]!.GetValue<string>());
    }

    [Fact]
    public void BorrowedCoordinatorStaysAliveAndKeysetReconciliationDoesNotSkipAfterSettlement()
    {
        using var fixture = new Fixture();
        using var coordinator = new DurableEvolutionWorkCoordinator(fixture.DirectoryPath, "run", "compat", EvolutionResources.Of("cost", 100));
        using var endpoint = new EvolutionWorkProtocol(coordinator);
        for (int i = 0; i < 3; i++)
        {
            JsonObject job = Job(); job["evaluationId"] = i.ToString(CultureInfo.InvariantCulture); Ok(endpoint, "enqueue", "job", job);
            JsonObject worker = Worker("worker"); worker["maximumConcurrentWork"] = 3; Ok(endpoint, "claim", "worker", worker);
        }
        var first = Ok(endpoint, "unsettled", "workerId", "worker");
        string cursor = first["nextAfterLeaseId"]!.GetValue<string>();
        Call(endpoint, Commit(first["lease"]!.AsObject(), "1"));
        var next = Ok(endpoint, "unsettled", "workerId", "worker", "afterLeaseId", cursor);
        Assert.NotNull(next["lease"]); Assert.NotEqual(cursor, next["nextAfterLeaseId"]!.GetValue<string>());
        var delivery = Ok(endpoint, "delivery", "identity", first["lease"]!["identity"]!.DeepClone(), "workerId", "worker");
        Assert.True(delivery["result"]!["accepted"]!.GetValue<bool>());
        Ok(endpoint, "close"); Assert.True(endpoint.IsClosed); Assert.Equal(1, coordinator.Resources.Settled);
        Assert.False(JsonNode.Parse(endpoint.ProcessJson("{}"))!["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("-1")]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("1000000000000000001")]
    public void InvalidOrLossyAmountsCannotPublishAJob(string amount)
    {
        using var fixture = new Fixture(); using var endpoint = fixture.Open();
        JsonObject job = Job(); job["maximum"]!["cost"] = amount;
        var response = Call(endpoint, Request("enqueue", "job", job), expectOk: false);
        Assert.Equal(42, response["id"]!.GetValue<int>());
        Assert.False(Ok(endpoint, "claim", "worker", Worker("worker"))["available"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("{\"id\":42,\"protocol\":1,\"op\":\"status\",\"op\":\"close\"}")]
    [InlineData("{\"id\":42,\"protocol\":2,\"op\":\"close\"}")]
    [InlineData("{\"id\":42,\"protocol\":1,\"op\":\"close\",\"typo\":true}")]
    [InlineData("{\"id\":42,\"op\":\"close\"}")]
    [InlineData("{\"id\":42,\"protocol\":1,\"op\":\"cancel\",\"evaluationId\":9223372036854775807,\"attempt\":1}")]
    [InlineData("{\"id\":42,\"protocol\":1,\"op\":\"cancel\",\"evaluationId\":\"01\",\"attempt\":1}")]
    [InlineData("{\"id\":42,\"protocol\":1,\"op\":\"cancel\",\"evaluationId\":\"9223372036854775808\",\"attempt\":1}")]
    public void MalformedCommandsCannotSilentlyMutateOrDowngrade(string json)
    {
        using var fixture = new Fixture(); using var endpoint = fixture.Open();
        var response = JsonNode.Parse(endpoint.ProcessJson(json))!.AsObject();
        Assert.False(response["ok"]!.GetValue<bool>()); Assert.Equal(42, response["id"]!.GetValue<int>());
        Assert.False(endpoint.IsClosed); Assert.Equal("0", Ok(endpoint, "status")["admitted"]!.GetValue<string>());
    }

    [Fact]
    public void ShapeBoundsMatchingBudgetViolationsAndUnknownTicketsRemainExplicit()
    {
        using var fixture = new Fixture(); using var endpoint = fixture.Open();
        var tooLarge = JsonNode.Parse(endpoint.ProcessJson(new string('x', EvolutionWorkProtocol.MaximumFrameBytes + 1)))!;
        Assert.False(tooLarge["ok"]!.GetValue<bool>());
        Assert.False(JsonNode.Parse(endpoint.ProcessJson("not-json"))!["ok"]!.GetValue<bool>());
        JsonObject job = Job(); job["tags"] = new JsonArray("gpu"); job["minimumResources"] = new JsonObject { ["gpu_slots"] = "1" };
        Ok(endpoint, "enqueue", "job", job);
        Assert.False(Ok(endpoint, "claim", "worker", Worker("cpu"))["available"]!.GetValue<bool>());
        var worker = Worker("gpu"); worker["tags"] = new JsonArray("gpu"); worker["capacity"] = new JsonObject { ["gpu_slots"] = "1" };
        var lease = Ok(endpoint, "claim", "worker", worker)["lease"]!.AsObject();
        Assert.Equal("renewed", Ok(endpoint, "heartbeat", "identity", lease["identity"]!.DeepClone(), "workerId", "gpu")["status"]!.GetValue<string>());
        Assert.Equal("unknown-lease", Ok(endpoint, "heartbeat", "identity", lease["identity"]!.DeepClone(), "workerId", "foreign")["status"]!.GetValue<string>());
        JsonObject commit = Commit(lease, "6"); commit["workerId"] = "foreign";
        Assert.Equal("unknown-lease", Call(endpoint, commit)["disposition"]!.GetValue<string>());
        commit["workerId"] = "gpu";
        Assert.Equal("budget-violation", Call(endpoint, commit)["disposition"]!.GetValue<string>());
        Assert.True(Ok(endpoint, "status")["maximumViolated"]!.GetValue<bool>());
        Assert.Equal("6", Ok(endpoint, "status")["spent"]!["cost"]!.GetValue<string>());
    }

    private static JsonObject Job() => new()
    {
        ["evaluationId"] = long.MaxValue.ToString(CultureInfo.InvariantCulture),
        ["attempt"] = 1,
        ["canonicalGenomeId"] = "integer:7",
        ["payload"] = "7",
        ["estimated"] = new JsonObject { ["cost"] = "1" },
        ["maximum"] = new JsonObject { ["cost"] = "5" }
    };
    private static JsonObject Worker(string id) => new() { ["workerId"] = id, ["compatibilityHash"] = "compat" };
    private static JsonObject Commit(JsonObject lease, string actual) => Request("commit", "identity", lease["identity"]!.DeepClone(),
        "workerId", lease["workerId"]!.GetValue<string>(), "payload", "49", "provenance", "local-square-v1", "outcome", "completed", "actual", new JsonObject { ["cost"] = actual });
    private static JsonObject Request(string op, params object[] fields)
    {
        var request = new JsonObject { ["id"] = 42, ["protocol"] = 1, ["op"] = op };
        for (int i = 0; i < fields.Length; i += 2)
            request[(string)fields[i]] = fields[i + 1] switch { JsonNode node => node.DeepClone(), string text => JsonValue.Create(text), int value => JsonValue.Create(value), _ => throw new InvalidOperationException() };
        return request;
    }
    private static JsonObject Ok(EvolutionWorkProtocol endpoint, string op, params object[] fields) => Call(endpoint, Request(op, fields));
    private static JsonObject Call(EvolutionWorkProtocol endpoint, JsonObject request, bool expectOk = true)
    {
        var response = JsonNode.Parse(endpoint.ProcessJson(request.ToJsonString()))!.AsObject();
        Assert.Equal(expectOk, response["ok"]!.GetValue<bool>()); return response;
    }
    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "evolution-work-protocol-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now { get; set; } = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        internal EvolutionWorkProtocol Open()
        {
            var endpoint = new EvolutionWorkProtocol(() => Now);
            Ok(endpoint, "open", "config", new JsonObject
            {
                ["directory"] = DirectoryPath,
                ["runId"] = "run",
                ["compatibilityHash"] = "compat",
                ["limits"] = new JsonObject { ["cost"] = "100" },
                ["leaseDurationMs"] = 10000
            });
            return endpoint;
        }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
