using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>
/// V1-56 (#169): the one supported way to add to the environment a candidate process sees. The engines clear the
/// environment before every execution, which is why an evaluator cannot be handed a module search path by exporting
/// one in the shell that launched the host - and why that path has to be declared instead of inherited.
/// </summary>
public sealed class DeclaredSandboxEnvironmentTests
{
    private static ProgramExecuteRequest Request() =>
        new() { Language = ProgramLanguage.Python, SourceCode = "ignored" };

    private static ProgramSandboxOptions Pinned(string template)
    {
        ProgramSandboxOptions options = ProgramSandboxTestEnvironment.Options(template);
        options.RuntimeVersion = "declared-environment-tests-v1";
        return options;
    }

    [Fact]
    public async Task A_declared_variable_reaches_the_child_while_the_host_environment_still_does_not()
    {
        Assert.True(ProgramSandboxTestEnvironment.ShellExists, "No shell available on this machine.");

        const string SecretName = "AIDOTNET_DECLARED_ENV_SECRET";
        const string SecretValue = "s3cr3t-that-must-not-leak";
        const string DeclaredName = "AIDOTNET_DECLARED_ENV_PROBE";
        const string DeclaredValue = "declared-by-the-caller";
        // The host also holds a value under the declared name. Inheriting would produce the host's value, so the
        // assertion on the declared value is what separates "declared" from "inherited" rather than restating it.
        Environment.SetEnvironmentVariable(SecretName, SecretValue);
        Environment.SetEnvironmentVariable(DeclaredName, "host-value-that-must-be-ignored");
        try
        {
            ProgramSandboxOptions options = Pinned(ProgramSandboxTestEnvironment.PrintEnvironmentTemplate);
            options.DeclaredEnvironment[DeclaredName] = DeclaredValue;
            options.Validate();
            using var engine = new ProcessProgramExecutionEngine(options);

            ProgramExecuteResponse response = await engine.ExecuteAsync(Request());

            Assert.True(response.Success, response.Error ?? "no error reported");
            Assert.Contains(DeclaredName + "=" + DeclaredValue, response.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("host-value-that-must-be-ignored", response.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretName, response.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretValue, response.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, null);
            Environment.SetEnvironmentVariable(DeclaredName, null);
        }
    }

    [Fact]
    public async Task Nothing_is_added_when_nothing_is_declared()
    {
        Assert.True(ProgramSandboxTestEnvironment.ShellExists, "No shell available on this machine.");

        using var engine = new ProcessProgramExecutionEngine(
            Pinned(ProgramSandboxTestEnvironment.PrintEnvironmentTemplate));

        ProgramExecuteResponse response = await engine.ExecuteAsync(Request());

        Assert.True(response.Success, response.Error ?? "no error reported");
        Assert.DoesNotContain("PYTHONPATH", response.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("path")]
    [InlineData("PYTHONIOENCODING")]
    [InlineData("PYTHONDONTWRITEBYTECODE")]
    [InlineData("TEMP")]
    [InlineData("TMPDIR")]
    [InlineData("HOME")]
    [InlineData("DOTNET_NOLOGO")]
    public void A_name_the_engine_sets_itself_is_refused_by_name(string reserved)
    {
        // Each of these carries part of the boundary. Letting a declaration win would move the workspace, unpin the
        // search path, or change the child's encoding while still looking like ordinary configuration.
        var options = new ProgramSandboxOptions();
        options.DeclaredEnvironment[reserved] = "anything";

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains(reserved.ToUpperInvariant(), error.Message.ToUpperInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_reserved_name_is_one_the_engine_actually_sets()
    {
        // Inverted on purpose: a name added to the reserved list without the engine setting it would be a refusal with
        // no reason behind it, and a name the engine sets without listing it here would be silently overridable.
        Assert.True(ProgramSandboxTestEnvironment.ShellExists, "No shell available on this machine.");
        using var engine = new ProcessProgramExecutionEngine(
            Pinned(ProgramSandboxTestEnvironment.PrintEnvironmentTemplate));
        ProgramExecuteResponse response = await engine.ExecuteAsync(Request());
        string environment = response.StdOut ?? string.Empty;

        string[] platformOnly = OperatingSystem.IsWindows()
            ? new[] { "HOME", "TMPDIR", "LANG" }
            : new[] { "PATHEXT", "SystemRoot", "windir", "ComSpec", "TEMP", "TMP" };
        foreach (string reserved in ProgramSandboxOptions.ReservedEnvironmentNames)
        {
            if (platformOnly.Contains(reserved, StringComparer.OrdinalIgnoreCase)) continue;
            Assert.Contains(reserved + "=", environment, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HAS=EQUALS")]
    [InlineData("HAS\u0000NULL")]
    public void A_name_that_cannot_be_an_environment_variable_is_refused(string name)
    {
        var options = new ProgramSandboxOptions();
        options.DeclaredEnvironment[name] = "value";

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void A_null_value_is_refused_rather_than_read_as_an_unset_variable()
    {
        var options = new ProgramSandboxOptions();
        options.DeclaredEnvironment["AIDOTNET_PROBE"] = null!;

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Two_names_differing_only_in_case_are_refused_rather_than_silently_collapsed()
    {
        // One variable on Windows, two everywhere else. A configuration that means different things per platform is
        // refused rather than resolved by whichever entry the dictionary happened to yield last.
        var options = new ProgramSandboxOptions();
        options.DeclaredEnvironment["AIDOTNET_PROBE"] = "first";
        options.DeclaredEnvironment["aidotnet_probe"] = "second";

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("differ only in case", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_value_is_accepted_because_an_empty_search_path_is_a_real_setting()
    {
        var options = new ProgramSandboxOptions();
        options.DeclaredEnvironment["PYTHONPATH"] = string.Empty;

        options.Validate();
    }

    [Fact]
    public void Clone_carries_the_declared_environment_and_then_stops_sharing_it()
    {
        // A hand-written Clone that lists properties one by one drops whichever one is added next, and the engine
        // snapshots its options through Clone, so a dropped entry would disappear before the first execution.
        var options = new ProgramSandboxOptions { RuntimeVersion = "pinned-v1" };
        options.DeclaredEnvironment["PYTHONPATH"] = "/one";

        ProgramSandboxOptions clone = options.Clone();
        Assert.Equal("/one", clone.DeclaredEnvironment["PYTHONPATH"]);

        options.DeclaredEnvironment["PYTHONPATH"] = "/two";
        options.DeclaredEnvironment["ADDED_AFTER_CLONE"] = "x";
        Assert.Equal("/one", clone.DeclaredEnvironment["PYTHONPATH"]);
        Assert.False(clone.DeclaredEnvironment.ContainsKey("ADDED_AFTER_CLONE"));
    }

    [Fact]
    public void Clone_copies_every_writable_property_of_the_options()
    {
        // Pinned by reflection and inverted: a property added to ProgramSandboxOptions without a line in Clone fails
        // here rather than silently reverting to its default on the engine's snapshot.
        string[] copied = typeof(ProgramSandboxOptions).GetProperties()
            .Where(property => property.CanWrite || !property.PropertyType.IsValueType)
            .Select(property => property.Name)
            .Where(name => name != nameof(ProgramSandboxOptions.ReservedEnvironmentNames))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ProgramSandboxOptions.DeclaredEnvironment), nameof(ProgramSandboxOptions.Interpreters),
                nameof(ProgramSandboxOptions.Limits), nameof(ProgramSandboxOptions.RuntimeVersion),
                nameof(ProgramSandboxOptions.TimeProvider), nameof(ProgramSandboxOptions.WorkingDirectory)
            },
            copied.Where(name => name != nameof(ProgramSandboxOptions.Mode)
                && name != nameof(ProgramSandboxOptions.AllowUnsafeInProcessExecution)).ToArray());
    }

    [Fact]
    public void A_declared_search_path_changes_the_engine_identity()
    {
        // A result produced with a module search path is not interchangeable with one produced without it, so the
        // engine's version hash has to separate them or a cache would serve one for the other.
        string Hash(string? pythonPath)
        {
            ProgramSandboxOptions options = Pinned(ProgramSandboxTestEnvironment.EchoSourceTemplate);
            if (pythonPath is not null) options.DeclaredEnvironment["PYTHONPATH"] = pythonPath;
            options.WorkingDirectory = Path.Combine(Path.GetTempPath(), "aidotnet-declared-identity");
            using var engine = new ProcessProgramExecutionEngine(options);
            return engine.VersionHash;
        }

        string plain = Hash(null);
        Assert.Equal(plain, Hash(null));
        Assert.NotEqual(plain, Hash("/modules"));
        Assert.NotEqual(Hash("/modules"), Hash("/other-modules"));
        Assert.Equal(Hash("/modules"), Hash("/modules"));
    }
}
