using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ReleaseWorkflowSecurityContractTests
{
    [Fact]
    public void RepositoryReleaseWorkflowsSatisfySecurityContract()
    {
        IReadOnlyList<string> errors = ReleaseWorkflowSecurityContract.Validate(WorkflowDirectory());

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void QuotedOidcPermissionInPackJobIsRejected()
    {
        using var fixture = ReleaseWorkflowFixture.Create();
        string releasePath = Path.Combine(fixture.WorkflowDirectory, "automated-release.yml");
        string workflow = File.ReadAllText(releasePath).Replace("\r\n", "\n");
        const string safePack = "  pack:\n    name: Pack and sign\n    permissions:\n      contents: read\n";
        const string unsafePack = "  pack:\n    name: Pack and sign\n    permissions:\n      contents: read\n      \"id-token\": write\n";
        Assert.Contains(safePack, workflow, StringComparison.Ordinal);
        File.WriteAllText(releasePath, workflow.Replace(safePack, unsafePack));

        IReadOnlyList<string> errors = ReleaseWorkflowSecurityContract.Validate(fixture.WorkflowDirectory);

        Assert.Contains(errors,
            error => error.IndexOf("pack job cannot request an OIDC token", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void CommentsThatLookLikeSecuritySettingsAreIgnored()
    {
        using var fixture = ReleaseWorkflowFixture.Create();
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "comments.yml"),
            """
            name: Harmless comments
            on: workflow_dispatch
            jobs:
              inspect:
                runs-on: ubuntu-latest
                steps:
                  # id-token: write
                  # uses: NuGet/login@untrusted
                  - run: echo "comments are not workflow structure"
            """);

        IReadOnlyList<string> errors = ReleaseWorkflowSecurityContract.Validate(fixture.WorkflowDirectory);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void IsolatedAttestationRejectsAdditionalActions()
    {
        using var fixture = ReleaseWorkflowFixture.Create();
        string attestationPath = Path.Combine(fixture.WorkflowDirectory, "attest-release.yml");
        string workflow = File.ReadAllText(attestationPath).Replace("\r\n", "\n");
        const string provenanceStep =
            "      - uses: actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 # v4.2.2\n";
        const string untrustedStep = "      - uses: example/package-publisher@v1\n";
        Assert.Contains(provenanceStep, workflow, StringComparison.Ordinal);
        File.WriteAllText(attestationPath, workflow.Replace(provenanceStep, untrustedStep + provenanceStep));

        IReadOnlyList<string> errors = ReleaseWorkflowSecurityContract.Validate(fixture.WorkflowDirectory);

        Assert.Contains(errors,
            error => error.IndexOf("only download packages and create provenance", StringComparison.Ordinal) >= 0);
    }

    private static string WorkflowDirectory()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".github", "workflows");
            if (File.Exists(Path.Combine(candidate, "automated-release.yml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository workflow directory.");
    }

    private sealed class ReleaseWorkflowFixture : IDisposable
    {
        private ReleaseWorkflowFixture(string root)
        {
            Root = root;
            WorkflowDirectory = Path.Combine(root, ".github", "workflows");
        }

        private string Root { get; }

        public string WorkflowDirectory { get; }

        public static ReleaseWorkflowFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidotnet-evolution-workflow-tests", Guid.NewGuid().ToString("N"));
            var fixture = new ReleaseWorkflowFixture(root);
            Directory.CreateDirectory(fixture.WorkflowDirectory);

            foreach (string source in Directory.EnumerateFiles(WorkflowDirectory(), "*.yml")
                         .Concat(Directory.EnumerateFiles(WorkflowDirectory(), "*.yaml")))
            {
                File.Copy(source, Path.Combine(fixture.WorkflowDirectory, Path.GetFileName(source)));
            }

            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

internal static class ReleaseWorkflowSecurityContract
{
    private const string ReleaseWorkflowName = "automated-release.yml";
    private const string AttestationWorkflowName = "attest-release.yml";
    private const string NuGetLoginAction = "NuGet/login@";
    private const string ProvenanceAction = "actions/attest-build-provenance@";

    public static IReadOnlyList<string> Validate(string workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            throw new ArgumentException("Workflow directory is required.", nameof(workflowDirectory));
        }

        var errors = new List<string>();
        Dictionary<string, YamlMappingNode> workflows = LoadWorkflows(workflowDirectory, errors);
        if (!workflows.TryGetValue(ReleaseWorkflowName, out YamlMappingNode? release))
        {
            errors.Add($"Missing {ReleaseWorkflowName}.");
        }
        else
        {
            ValidateReleaseWorkflow(release, errors);
        }

        if (!workflows.TryGetValue(AttestationWorkflowName, out YamlMappingNode? attestation))
        {
            errors.Add($"Missing {AttestationWorkflowName}.");
        }
        else
        {
            ValidateAttestationWorkflow(attestation, errors);
        }

        int loginCount = workflows.Values
            .SelectMany(Jobs)
            .SelectMany(Steps)
            .Count(step => Action(step).StartsWith(NuGetLoginAction, StringComparison.OrdinalIgnoreCase));
        Require(loginCount == 1, errors,
            "repository must contain exactly one NuGet trusted-publishing login step");

        return errors;
    }

    private static Dictionary<string, YamlMappingNode> LoadWorkflows(
        string workflowDirectory,
        ICollection<string> errors)
    {
        var workflows = new Dictionary<string, YamlMappingNode>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(workflowDirectory))
        {
            errors.Add($"Workflow directory does not exist: {workflowDirectory}");
            return workflows;
        }

        IEnumerable<string> paths = Directory.EnumerateFiles(workflowDirectory, "*.yml")
            .Concat(Directory.EnumerateFiles(workflowDirectory, "*.yaml"));
        foreach (string path in paths)
        {
            try
            {
                using var reader = File.OpenText(path);
                var stream = new YamlStream();
                stream.Load(reader);
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                {
                    errors.Add($"{Path.GetFileName(path)} must contain one YAML mapping document.");
                    continue;
                }

                workflows.Add(Path.GetFileName(path), root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException)
            {
                errors.Add($"{Path.GetFileName(path)} is not valid readable YAML: {exception.Message}");
            }
        }

        return workflows;
    }

    private static void ValidateReleaseWorkflow(YamlMappingNode workflow, ICollection<string> errors)
    {
        YamlMappingNode? jobs = Mapping(workflow, "jobs");
        if (jobs is null)
        {
            errors.Add("release workflow must define a jobs mapping");
            return;
        }

        YamlMappingNode? pack = Job(jobs, "pack", errors);
        YamlMappingNode? attest = Job(jobs, "attest", errors);
        YamlMappingNode? publish = Job(jobs, "publish", errors);

        if (pack is not null)
        {
            Require(!HasKey(Mapping(pack, "permissions"), "id-token"), errors,
                "pack job cannot request an OIDC token");
            Require(!Steps(pack).Any(step => Action(step).StartsWith(ProvenanceAction, StringComparison.OrdinalIgnoreCase)),
                errors, "pack job cannot perform OIDC-dependent attestation");

            List<YamlMappingNode> uploads = Steps(pack)
                .Where(step => Action(step).StartsWith("actions/upload-artifact@", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Require(uploads.Count == 1, errors, "pack job must upload exactly one package artifact");
            if (uploads.Count == 1)
            {
                Require(Scalar(Mapping(uploads[0], "with"), "retention-days") == "30", errors,
                    "package artifact retention must be 30 days");
            }
        }

        if (attest is not null)
        {
            Require(Scalar(attest, "uses") == "./.github/workflows/attest-release.yml", errors,
                "attest job must call the isolated attestation workflow");
            Require(!HasKey(attest, "runs-on") && !HasKey(attest, "steps"), errors,
                "attest call job cannot contain runner steps");
            Require(Permission(attest, "id-token") == "write", errors,
                "attest call job must delegate OIDC permission to the isolated workflow");
        }

        if (publish is not null)
        {
            Require(Permission(publish, "id-token") == "write", errors,
                "publish job must request its NuGet OIDC token");
            int publishLoginCount = Steps(publish)
                .Count(step => Action(step).StartsWith(NuGetLoginAction, StringComparison.OrdinalIgnoreCase));
            Require(publishLoginCount == 1, errors,
                "publish job must contain exactly one NuGet trusted-publishing login step");
            Require(Needs(publish).SetEquals(new[] { "validate", "pack", "attest" }), errors,
                "publish job must wait for validation, package creation, and provenance");
        }
    }

    private static void ValidateAttestationWorkflow(YamlMappingNode workflow, ICollection<string> errors)
    {
        Require(Mapping(Mapping(workflow, "on"), "workflow_call") is not null, errors,
            "attestation workflow must only be entered as a reusable workflow call");

        YamlMappingNode? jobs = Mapping(workflow, "jobs");
        YamlMappingNode? attest = jobs is null ? null : Job(jobs, "attest", errors);
        if (attest is null)
        {
            return;
        }

        Require(Permission(attest, "id-token") == "write", errors,
            "isolated attestation job must request provenance OIDC");
        List<YamlMappingNode> steps = Steps(attest).ToList();
        Require(steps.Count == 2 && steps.All(IsAllowedAttestationAction), errors,
            "isolated attestation job may only download packages and create provenance");
        Require(steps.Count(step => Action(step).StartsWith(ProvenanceAction, StringComparison.OrdinalIgnoreCase)) == 1,
            errors, "isolated attestation job must create provenance exactly once");
        Require(steps.All(step => !HasKey(step, "run")), errors,
            "isolated attestation job may contain action steps only");
        Require(steps.All(step => !Action(step).StartsWith(NuGetLoginAction, StringComparison.OrdinalIgnoreCase)),
            errors, "isolated attestation job cannot authenticate to NuGet");
    }

    private static bool IsAllowedAttestationAction(YamlMappingNode step)
    {
        string action = Action(step);
        return action.StartsWith("actions/download-artifact@", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith(ProvenanceAction, StringComparison.OrdinalIgnoreCase);
    }

    private static YamlMappingNode? Job(
        YamlMappingNode jobs,
        string name,
        ICollection<string> errors)
    {
        YamlMappingNode? job = Mapping(jobs, name);
        if (job is null)
        {
            errors.Add($"release workflow must define a {name} job");
        }

        return job;
    }

    private static IEnumerable<YamlMappingNode> Jobs(YamlMappingNode workflow)
    {
        YamlMappingNode? jobs = Mapping(workflow, "jobs");
        return jobs is null
            ? Enumerable.Empty<YamlMappingNode>()
            : jobs.Children.Values.OfType<YamlMappingNode>();
    }

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job)
    {
        return Sequence(job, "steps")?.Children.OfType<YamlMappingNode>()
            ?? Enumerable.Empty<YamlMappingNode>();
    }

    private static string Action(YamlMappingNode step) => Scalar(step, "uses") ?? string.Empty;

    private static string? Permission(YamlMappingNode job, string name) => Scalar(Mapping(job, "permissions"), name);

    private static HashSet<string> Needs(YamlMappingNode job)
    {
        YamlSequenceNode? sequence = Sequence(job, "needs");
        if (sequence is not null)
        {
            return new HashSet<string>(
                sequence.Children.OfType<YamlScalarNode>().Select(node => node.Value ?? string.Empty),
                StringComparer.Ordinal);
        }

        string? scalar = Scalar(job, "needs");
        return scalar is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(new[] { scalar }, StringComparer.Ordinal);
    }

    private static YamlMappingNode? Mapping(YamlMappingNode? mapping, string key) => Node(mapping, key) as YamlMappingNode;

    private static YamlSequenceNode? Sequence(YamlMappingNode mapping, string key) => Node(mapping, key) as YamlSequenceNode;

    private static string? Scalar(YamlMappingNode? mapping, string key) =>
        (Node(mapping, key) as YamlScalarNode)?.Value;

    private static bool HasKey(YamlMappingNode? mapping, string key) => Node(mapping, key) is not null;

    private static YamlNode? Node(YamlMappingNode? mapping, string key)
    {
        if (mapping is null)
        {
            return null;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static void Require(bool condition, ICollection<string> errors, string message)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }
}
