#if NET8_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The shared durable-worker conformance scenarios (conformance/durable-v1/scenarios.json), run against the C#
/// protocol endpoint the host serves. The Python and TypeScript clients run the same file; all must agree.
/// </summary>
public sealed class DurableConformanceTests
{
    private static readonly string Scenarios = FindScenarios();

    private static string FindScenarios()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "conformance", "durable-v1", "scenarios.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("conformance/durable-v1/scenarios.json not found above the test output.");
    }

    public static IEnumerable<object[]> Names() =>
        JsonNode.Parse(File.ReadAllText(Scenarios))!["scenarios"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Names))]
    public void The_host_endpoint_satisfies_the_shared_scenario(string name)
    {
        JsonNode suite = JsonNode.Parse(File.ReadAllText(Scenarios))!;
        JsonNode scenario = suite["scenarios"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;
        string directory = Path.Combine(Path.GetTempPath(), "durable-conformance", Guid.NewGuid().ToString("N"));
        JsonObject? lease = null;
        EvolutionWorkProtocol endpoint = new();
        try
        {
            int id = 0;
            foreach (JsonNode? step in scenario["steps"]!.AsArray())
            {
                string op = step!["op"]!.GetValue<string>();
                var request = new JsonObject { ["id"] = ++id, ["protocol"] = 1, ["op"] = op };
                foreach (var (key, value) in step["args"]!.AsObject())
                    request[key] = Substitute(value, suite, directory, lease);
                if (op == "open" && endpoint.IsClosed) endpoint = new EvolutionWorkProtocol();
                JsonNode reply = JsonNode.Parse(endpoint.ProcessJson(request.ToJsonString()))!;
                bool ok = reply["ok"]?.GetValue<bool>() ?? false;
                var expect = step["expect"]!.AsObject();
                if (expect.ContainsKey("$error"))
                {
                    Assert.False(ok, $"{name} step {id} ({op}) should have been refused: {reply.ToJsonString()}");
                    if (op == "open") endpoint = new EvolutionWorkProtocol();
                    continue;
                }
                Assert.True(ok, $"{name} step {id} ({op}) failed: {reply.ToJsonString()}");
                if (op == "claim" && reply["lease"] is JsonObject claimed) lease = claimed;
                foreach (var (path, expected) in expect)
                    Assert.True(JsonNode.DeepEquals(Lookup(reply, path), expected),
                        $"{name} step {id} ({op}): {path} expected {expected?.ToJsonString() ?? "null"} but reply was {reply.ToJsonString()}");
            }
        }
        finally
        {
            if (!endpoint.IsClosed) endpoint.ProcessJson("{\"id\":999,\"protocol\":1,\"op\":\"close\"}");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonNode? Substitute(JsonNode? value, JsonNode suite, string directory, JsonObject? lease)
    {
        if (value is JsonValue text && text.TryGetValue(out string? s))
        {
            if (s == "$dir") return directory;
            if (s == "$config") return Substitute(suite["config"]!.DeepClone(), suite, directory, lease);
            if (s == "$job") return suite["job"]!.DeepClone();
            if (s.StartsWith("$lease", StringComparison.Ordinal))
                return Lookup(lease ?? throw new InvalidOperationException("No lease has been claimed yet."), s["$lease".Length..].TrimStart('.'))?.DeepClone();
            return value.DeepClone();
        }
        if (value is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var (key, child) in obj) copy[key] = Substitute(child, suite, directory, lease);
            return copy;
        }
        return value?.DeepClone();
    }

    private static JsonNode? Lookup(JsonNode node, string path)
    {
        JsonNode? current = node;
        if (path.Length == 0) return current;
        foreach (string part in path.Split('.'))
            current = current is JsonObject obj && obj.TryGetPropertyValue(part, out JsonNode? next) ? next : null;
        return current;
    }
}
#endif
