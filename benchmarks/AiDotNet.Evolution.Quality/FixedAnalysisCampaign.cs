using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Quality;

internal static class FixedAnalysisCampaign
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: --analysis-campaign <locked-schedule.json> <new-result.json>");
        using var input = File.OpenRead(args[0]);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("Schedule exceeds 1 MiB.");
        using var document = await JsonDocument.ParseAsync(input);
        JsonElement root = document.RootElement;
        UniqueFields(root);
        if (root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "AnalysisPlan", "RegistrationSha256", "SearchSeeds" }) is false)
            throw new InvalidDataException("Unknown schedule field.");
        string registration = root.GetProperty("RegistrationSha256").GetString() ?? "";
        JsonElement plan = root.GetProperty("AnalysisPlan");
        string revision = plan.GetProperty("SourceRevision").GetString() ?? "";
        int count = plan.GetProperty("SeedCount").GetInt32();
        int budget = plan.GetProperty("Budget").GetInt32();
        uint[] seeds = root.GetProperty("SearchSeeds").EnumerateArray().Select(s => s.GetUInt32()).ToArray();
        string[] tasks = plan.GetProperty("Tasks").EnumerateArray().Select(t => t.GetProperty("Name").GetString() ?? "").ToArray();
        string[] methods = plan.GetProperty("Methods").EnumerateArray().Select(m => m.GetString() ?? "").ToArray();
        if (!Hex(registration, 64) || !Hex(revision, 40) || count is < 1 or > 1000 || count != seeds.Length || seeds.Distinct().Count() != count ||
            budget is < 8 or > 1_000_000 || tasks.Length == 0 || methods.Length == 0 ||
            tasks.Distinct().Count() != tasks.Length || methods.Distinct().Count() != methods.Length ||
            tasks.Any(t => !Enum.GetNames<QualityTask>().Contains(t)) || methods.Any(m => !Enum.GetNames<QualityMethod>().Contains(m)) ||
            (long)count * budget * tasks.Length * methods.Length > 2_000_000 ||
            plan.GetProperty("Protocol").GetString() != "numeric-development-v3-diagonal-cma" ||
            plan.GetProperty("Purpose").GetString() != "retrospective-development")
            throw new InvalidDataException("Invalid fixed numeric schedule.");
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var records = new List<RunRecord>();
        foreach (string task in tasks)
            foreach (string method in methods)
                foreach (uint seed in seeds)
                    records.Add(await QualityExperiment.RunAsync(Enum.Parse<QualityTask>(task), Enum.Parse<QualityMethod>(method), seed, budget));
        var report = new
        {
            SchemaVersion = 2,
            Protocol = "numeric-development-v3-diagonal-cma",
            Partition = "development",
            SourceRevision = revision,
            RegistrationSha256 = registration,
            Seeds = count,
            Budget = budget,
            TaskCount = tasks.Length,
            Methods = methods,
            Dimensions = QualityExperiment.Dimensions,
            InitialPopulation = QualityExperiment.InitialPopulation,
            Artifacts = new[] { typeof(FixedAnalysisCampaign).Assembly.Location, typeof(EvolutionEngineOptions).Assembly.Location }
                .ToDictionary(path => Path.GetFileName(path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()),
            Limitations = "Fresh fixed search seeds on development tasks; not a sealed holdout or a competitive superiority claim.",
            Runs = records
        };
        await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        return records.All(r => r.Status == "completed") ? 0 : 1;
    }

    private static bool Hex(string text, int length) => text.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void UniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new InvalidDataException("Duplicate schedule field.");
                UniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueFields(item);
    }
}
