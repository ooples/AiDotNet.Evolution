#if NET8_0_OR_GREATER
using System.Diagnostics;
using AiDotNet.Evolution.Cli;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>What the CLI refuses to mount into a container, and what an OpenEvolve evaluator's identity covers.</summary>
public sealed class ContainerMountAndHelperIdentityTests
{
    // Docker bind-mounts the resolved target, so a link to the home directory is the home directory.
    [Fact]
    public void The_home_directory_and_a_link_to_it_are_refused_and_an_ordinary_directory_is_not()
    {
        string home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var directory = new TemporaryDirectory();
        string plain = Path.Combine(directory.Path, "evaluator");
        Directory.CreateDirectory(plain);
        Assert.Equal(plain, RunCommand.RequireMountable(plain, "the evaluator's directory"));
        Assert.Throws<InvalidDataException>(() => RunCommand.RequireMountable(home, "the evaluator's directory"));

        string link = Path.Combine(directory.Path, "innocent-looking");
        CreateDirectoryLink(link, home);
        try
        {
            InvalidDataException refused = Assert.Throws<InvalidDataException>(() => RunCommand.RequireMountable(link, "the evaluator's directory"));
            Assert.Contains("a link to " + home, refused.Message, StringComparison.Ordinal);
            // A link in a parent directory is resolved too.
            Assert.Equal(Path.Combine(home, "below"), RunCommand.ResolveLinks(Path.Combine(link, "below")), ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Directory.Delete(link); // the link itself, never what it points to
        }
    }

    [Fact]
    public void An_evaluator_that_is_itself_a_link_is_refused_for_a_container_and_the_file_is_not()
    {
        using var directory = new TemporaryDirectory();
        string real = Path.Combine(directory.Path, "real", "evaluator.py");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "def evaluate(path):\n    return {}\n");
        RunCommand.RequireEvaluatorNotLinked(real);

        string link = Path.Combine(directory.Path, "evaluator.py");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            return; // an unelevated Windows user cannot create a file link (and has no junction for files); Linux CI runs this
        }

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => RunCommand.RequireEvaluatorNotLinked(link));
        Assert.Contains(real, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_adding_or_removing_a_helper_changes_the_evaluator_identity_and_moving_the_checkout_does_not()
    {
        using var first = new TemporaryDirectory();
        using var moved = new TemporaryDirectory();
        foreach (string root in new[] { first.Path, moved.Path })
        {
            File.WriteAllText(Path.Combine(root, "evaluator.py"), "import helper\ndef evaluate(path):\n    return {'combined_score': helper.K}\n");
            File.WriteAllText(Path.Combine(root, "helper.py"), "K = 1\n");
        }

        string Shim(string root) => OpenEvolveEvaluatorShim.Build(Path.Combine(root, "evaluator.py"), Path.Combine(root, "evaluator.py"),
            new RunOpenEvolveEvaluator(), new[] { root }, Path.Combine(root, "out"));
        string before = Shim(first.Path);
        // The evaluator's own path is in the shim's configuration; the helper digest is by position and relative path.
        Assert.Equal(OpenEvolveEvaluatorShim.HelperSourcesSha256(new[] { first.Path }, null),
            OpenEvolveEvaluatorShim.HelperSourcesSha256(new[] { moved.Path }, null));

        // The run's own output, data files and bytecode are not sources the evaluator imports.
        Directory.CreateDirectory(Path.Combine(first.Path, "out"));
        File.WriteAllText(Path.Combine(first.Path, "out", "best.py"), "X = 1\n");
        File.WriteAllText(Path.Combine(first.Path, "data.csv"), "1,2\n");
        Directory.CreateDirectory(Path.Combine(first.Path, "__pycache__"));
        File.WriteAllText(Path.Combine(first.Path, "__pycache__", "helper.py"), "stale\n");
        Assert.Equal(before, Shim(first.Path));

        File.WriteAllText(Path.Combine(first.Path, "helper.py"), "K = 2\n");
        string edited = Shim(first.Path);
        Assert.NotEqual(before, edited);
        Directory.CreateDirectory(Path.Combine(first.Path, "package"));
        File.WriteAllText(Path.Combine(first.Path, "package", "extra.py"), "\n");
        string added = Shim(first.Path);
        Assert.NotEqual(edited, added);
        File.Delete(Path.Combine(first.Path, "package", "extra.py"));
        Assert.Equal(edited, Shim(first.Path));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction: a symbolic link needs a privilege an unelevated Windows user lacks, and Docker resolves both.
        using Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, mklink.StandardError.ReadToEnd());
    }
}
#endif