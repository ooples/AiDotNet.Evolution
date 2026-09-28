// V1-61 (#174): one evolution run whose evaluations are claimed, heartbeated and committed by workers on other
// hosts through EvolutionWorkServer. The coordinator records the final state hash, how many results reached the
// engine, and which worker committed each evaluation, so a run can be compared with a single-host run of the same
// seed and a killed worker's lease can be traced to its re-issue.
//
//   coordinate --share DIR --port P --token T --evaluations N [--local-workers K] [--lease-ms 3000] [--out FILE]
//   work --share DIR --host H --port P --token T --worker-id ID [--eval-ms 0]
//
// The coordinator writes DIR/session.json (certificate fingerprint and compatibility hash) for workers to read.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDotNet.Evolution;

return await Harness.MainAsync(args);

internal static class Harness
{
    private const string CostKey = "cost_units";

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Length == 0) return Usage();
        var options = Options.Parse(args.Skip(1).ToArray());
        return args[0] switch
        {
            "coordinate" => await CoordinateAsync(options),
            "work" => await WorkAsync(options),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: coordinate --share DIR --port P --token T --evaluations N [--local-workers K] [--wait-workers K] [--lease-ms MS] [--eval-ms MS] [--out FILE]");
        Console.Error.WriteLine("       work --share DIR --host H --port P --token T --worker-id ID [--eval-ms MS]");
        return 2;
    }

    // The genome is an integer; quality and descriptor are fixed functions of it, so every worker computes the same result.
    internal static (double Quality, double X) Evaluate(int genome) => (genome * 7919 % 1000 / 1000.0, genome % 100);

    private static async Task<int> CoordinateAsync(Options options)
    {
        string share = options.Required("share");
        Directory.CreateDirectory(share);
        int evaluations = options.Int("evaluations", 60);
        int localWorkers = options.Int("local-workers", 0);
        int evalMs = options.Int("eval-ms", 0);
        string token = options.Required("token");
        string store = Path.Combine(share, "coordinator-" + Guid.NewGuid().ToString("N"));

        var engineOptions = new EvolutionEngineOptions
        {
            RunId = "distributed-v1-61",
            Seed = 20260928,
            MaxProposals = evaluations,
            MaxEvaluationAttempts = evaluations + 8,
            MaxGenerations = evaluations,
            ProposalBatchSize = 8,
            MaxDegreeOfParallelism = 8,
            MaxRetries = 0,
            CheckpointInterval = 0,
            MigrationInterval = 0,
            ExecutionMode = EvolutionExecutionMode.Deterministic,
            // Continuous dispatch keeps every worker busy instead of idling at the tail of each batch; the
            // deterministic mode still commits in evaluation order, so worker count and timing cannot change the run.
            Dispatch = EvolutionDispatchMode.Continuous,
            MaxInFlight = 16,
            // Longer than a lease plus a re-evaluation, so a killed worker is recovered by lease re-issue, not by an
            // engine retry that would change the trajectory.
            EvaluationTimeout = TimeSpan.FromMinutes(10)
        };
        EvolutionEngine<int> Factory(IEvolutionTask<int> task) => new(task, new StepVariation(),
            _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), engineOptions,
            genomeCodec: new IntegerCodec());

        using var session = new EvolutionSession<int>(Factory, new[] { 3, 17, 41, 58 },
            value => value.ToString(CultureInfo.InvariantCulture),
            new EvolutionExternalTaskIdentity("integer", "distributed-task-v1", "distributed-evaluator-v1"));
        using var coordinator = new DurableEvolutionWorkCoordinator(store, session.RunId, session.CompatibilityHash,
            EvolutionResources.Of(CostKey, 1_000_000),
            new EvolutionWorkCoordinatorOptions(maximumDeliveriesPerWork: 8,
                leaseDuration: TimeSpan.FromMilliseconds(options.Int("lease-ms", 3000))));
        var bridge = new EvolutionDurableSessionBridge<int>(session, coordinator, Decode, new EvolutionWorkRequirements(),
            EvolutionResources.Of(CostKey, 1), EvolutionResources.Of(CostKey, 1));

        using X509Certificate2 certificate = SelfSigned();
        var faults = new List<string>();
        int authenticated = 0;
        using var server = new EvolutionWorkServer(coordinator, certificate, token, new EvolutionWorkServerOptions
        {
            Port = options.Int("port", 7070),
            OnEvent = e =>
            {
                if (e.Kind == EvolutionWorkServerEventKind.Authenticated) Interlocked.Increment(ref authenticated);
                if (e.Kind is EvolutionWorkServerEventKind.Faulted or EvolutionWorkServerEventKind.AuthenticationRefused)
                    lock (faults) faults.Add(e.Kind + " " + e.RemoteEndPoint + " " + e.Exception?.GetType().Name);
            }
        });
        server.Start();
        await File.WriteAllTextAsync(Path.Combine(share, "session.json.tmp"), new JsonObject
        {
            ["fingerprint"] = server.CertificateFingerprint,
            ["compatibilityHash"] = session.CompatibilityHash,
            ["port"] = server.LocalEndPoint.Port
        }.ToJsonString());
        File.Move(Path.Combine(share, "session.json.tmp"), Path.Combine(share, "session.json"), overwrite: true);
        Console.WriteLine($"coordinator listening on {server.LocalEndPoint} fingerprint {server.CertificateFingerprint}");

        using var stop = new CancellationTokenSource();
        var locals = Enumerable.Range(0, localWorkers).Select(i => Task.Run(() => WorkLoopAsync("127.0.0.1",
            server.LocalEndPoint.Port, server.CertificateFingerprint, token, session.CompatibilityHash, "local-" + i, evalMs,
            stop.Token))).ToArray();

        // Throughput is measured from the moment the expected workers are connected, so process start-up on the
        // worker hosts is not charged to the evaluation rate.
        int waitWorkers = options.Int("wait-workers", localWorkers);
        var connecting = Stopwatch.StartNew();
        while (Volatile.Read(ref authenticated) < waitWorkers && connecting.Elapsed < TimeSpan.FromMinutes(5)) await Task.Delay(20);
        var clock = Stopwatch.StartNew();
        var asked = new List<(long EvaluationId, int Attempt)>();
        int delivered = 0;
        Task asking = Task.Run(async () =>
        {
            while (!session.IsComplete)
            {
                IReadOnlyList<EvolutionAskItem<int>> items;
                try
                {
                    items = await session.AskAsync(64, stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                foreach (EvolutionAskItem<int> item in items)
                {
                    if (item.WorkIdentity is null || !bridge.Enqueue(item)) continue;
                    lock (asked) asked.Add((item.WorkIdentity.EvaluationId, item.WorkIdentity.Attempt));
                }
            }
        });

        while (!session.IsComplete)
        {
            delivered += bridge.DeliverAvailableResults();
            await Task.WhenAny(session.Completion, Task.Delay(20));
        }

        EvolutionRunResult<int> result = await session.Completion;
        double seconds = clock.Elapsed.TotalSeconds;
        stop.Cancel();
        await Task.WhenAll(locals.Append(asking).Select(t => t.ContinueWith(_ => { })));

        var committed = new JsonArray();
        int missing = 0;
        lock (asked)
        {
            foreach ((long evaluationId, int attempt) in asked.OrderBy(a => a.EvaluationId))
            {
                EvolutionCommittedWork? work = coordinator.GetResult(evaluationId, attempt);
                if (work is null) { missing++; continue; }
                committed.Add(new JsonObject
                {
                    ["evaluationId"] = evaluationId,
                    ["attempt"] = attempt,
                    ["leaseId"] = work.Identity.LeaseId,
                    ["provenance"] = work.Provenance
                });
            }
        }

        var report = new JsonObject
        {
            ["stateHash"] = result.StateHash,
            ["evaluationsAsked"] = asked.Count,
            ["resultsDelivered"] = delivered,
            ["resultsMissing"] = missing,
            ["elapsedSeconds"] = seconds,
            ["evaluationsPerSecond"] = asked.Count / Math.Max(seconds, 1e-9),
            ["localWorkers"] = localWorkers,
            ["workersAwaited"] = waitWorkers,
            ["evalMs"] = evalMs,
            ["bestQuality"] = result.Best?.Evaluation.Quality,
            ["serverFaults"] = new JsonArray(faults.Select(f => (JsonNode)f).ToArray()),
            ["committed"] = committed
        };
        string text = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.Optional("out") is { } output) await File.WriteAllTextAsync(output, text);
        Console.WriteLine($"state {result.StateHash} asked {asked.Count} delivered {delivered} missing {missing} in {seconds:F2}s");
        await File.WriteAllTextAsync(Path.Combine(share, "done"), result.StateHash);
        return delivered == asked.Count && missing == 0 ? 0 : 1;
    }

    private static async Task<int> WorkAsync(Options options)
    {
        string share = options.Required("share");
        string sessionFile = Path.Combine(share, "session.json");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(sessionFile))
        {
            if (wait.Elapsed > TimeSpan.FromMinutes(5)) { Console.Error.WriteLine("no coordinator session appeared"); return 1; }
            await Task.Delay(200);
        }

        JsonNode session = JsonNode.Parse(await File.ReadAllTextAsync(sessionFile)) ?? throw new InvalidDataException("empty session file");
        int port = options.Optional("port") is { } explicitPort
            ? int.Parse(explicitPort, CultureInfo.InvariantCulture)
            : session["port"]!.GetValue<int>();
        await WorkLoopAsync(options.Required("host"), port, session["fingerprint"]!.GetValue<string>(),
            options.Required("token"), session["compatibilityHash"]!.GetValue<string>(), options.Required("worker-id"),
            options.Int("eval-ms", 0), CancellationToken.None);
        return 0;
    }

    // Claims one lease at a time, heartbeats while it evaluates, commits, and logs every claim and commit as a JSON
    // line so a killed worker's last claim can be matched against the lease that finally committed it.
    private static async Task WorkLoopAsync(string host, int port, string fingerprint, string token, string compatibility,
        string workerId, int evalMs, CancellationToken cancellationToken)
    {
        EvolutionWorkRemoteClient client;
        try
        {
            client = await Connect(host, port, fingerprint, token, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException)
        {
            return;
        }

        using (client)
        {
            long requestId = 0;
            async Task<JsonObject> Call(string op, JsonObject fields)
            {
                fields["id"] = ++requestId;
                fields["protocol"] = 1;
                fields["op"] = op;
                return JsonNode.Parse(await client.SendAsync(fields.ToJsonString(), cancellationToken))!.AsObject();
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                JsonObject claim;
                try
                {
                    claim = await Call("claim", new JsonObject
                    {
                        ["worker"] = new JsonObject { ["workerId"] = workerId, ["compatibilityHash"] = compatibility }
                    });
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException
                    or System.Security.Authentication.AuthenticationException or OperationCanceledException or InvalidOperationException)
                {
                    Log(workerId, "coordinator-gone", null);
                    return;
                }

                if (claim["ok"]?.GetValue<bool>() != true || claim["available"]?.GetValue<bool>() != true)
                {
                    await Task.Delay(25, cancellationToken).ContinueWith(_ => { });
                    continue;
                }

                JsonObject lease = claim["lease"]!.AsObject();
                JsonNode identity = lease["identity"]!;
                Log(workerId, "claim", identity);
                var payload = EvolutionDurableEvaluationPayload.FromJson(lease["payload"]!.GetValue<string>());
                int genome = int.Parse(payload.GenomePayload, CultureInfo.InvariantCulture);

                var evaluating = Stopwatch.StartNew();
                while (evaluating.ElapsedMilliseconds < evalMs && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(Math.Min(500, Math.Max(1, evalMs - (int)evaluating.ElapsedMilliseconds)), cancellationToken)
                        .ContinueWith(_ => { });
                    await Call("heartbeat", new JsonObject { ["identity"] = identity.DeepClone(), ["workerId"] = workerId });
                }

                (double quality, double x) = Evaluate(genome);
                JsonObject commit = await Call("commit", new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["workerId"] = workerId,
                    ["payload"] = quality.ToString("R", CultureInfo.InvariantCulture) + "|" + x.ToString("R", CultureInfo.InvariantCulture),
                    ["provenance"] = "worker:" + workerId,
                    ["outcome"] = "completed",
                    ["actual"] = new JsonObject { [CostKey] = "1" }
                });
                Log(workerId, "commit:" + commit["disposition"]?.GetValue<string>(), identity);
            }
        }
    }

    private static async Task<EvolutionWorkRemoteClient> Connect(string host, int port, string fingerprint, string token,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await EvolutionWorkRemoteClient.ConnectAsync(host, port, fingerprint, token, cancellationToken);
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 100)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private static void Log(string workerId, string eventName, JsonNode? identity)
    {
        var line = new JsonObject { ["worker"] = workerId, ["event"] = eventName, ["utc"] = DateTimeOffset.UtcNow.ToString("O") };
        if (identity is not null) line["identity"] = identity.DeepClone();
        Console.WriteLine(line.ToJsonString());
    }

    private static EvolutionTaskResult Decode(string payload)
    {
        string[] parts = payload.Split('|');
        return EvolutionTaskResult.Completed(double.Parse(parts[0], CultureInfo.InvariantCulture),
            new Dictionary<string, double> { ["x"] = double.Parse(parts[1], CultureInfo.InvariantCulture) });
    }

    private static X509Certificate2 SelfSigned()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=evolution-distributed", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private sealed class StepVariation : IVariationOperator<int>
    {
        public string Id => "step";
        public string VersionHash => "step-v1";

        public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) =>
            new(context.Parent.Candidate.CanonicalGenome.Genome + 1 + context.Random.NextInt(0, 23));
    }

    private sealed class IntegerCodec : IEvolutionGenomeCodec<int>
    {
        public string Id => "integer";
        public string VersionHash => "v1";
        public string Serialize(int genome) => genome.ToString(CultureInfo.InvariantCulture);
        public int Deserialize(string payload) => int.Parse(payload, CultureInfo.InvariantCulture);
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument " + args[i]);
                options._values[args[i].Substring(2)] = args[i + 1];
            }

            return options;
        }

        public string Required(string name) =>
            _values.TryGetValue(name, out string? value) ? value : throw new ArgumentException("--" + name + " is required.");

        public string? Optional(string name) => _values.TryGetValue(name, out string? value) ? value : null;

        public int Int(string name, int fallback) =>
            _values.TryGetValue(name, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
    }
}
