using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AiDotNet.Evolution.Sandbox.Guardian;

/// <summary>
/// Runs one sandboxed command as a Linux child subreaper and leaves nothing of it behind.
/// </summary>
/// <remarks>
/// <para>
/// A candidate that double-forks (fork, setsid, fork, parent exits) detaches its grandchild from the candidate's
/// process tree. Without a subreaper that grandchild is reparented to init, where the engine's /proc walk no longer
/// counts it against the memory and CPU limits and its tree kill no longer reaches it. With
/// <c>PR_SET_CHILD_SUBREAPER</c> set here, every orphan below this process is reparented to it instead: it stays in
/// the tree the engine measures, and when the command ends this process kills and reaps whatever is left.
/// </para>
/// <para>
/// Usage: <c>dotnet exec AiDotNet.Evolution.Sandbox.Guardian.dll &lt;program&gt; [args...]</c>. The child inherits
/// standard input, output and error, and this process writes nothing to standard output, so the candidate's streams
/// are exactly its own. The exit code is the child's (128 + signal when a signal ended it).
/// </para>
/// </remarks>
internal static class Program
{
    private const int PrSetChildSubreaper = 36;
    private const int SigKill = 9;
    private const int WaitNoHang = 1;
    private const int UsageExitCode = 2;
    private const int SetupFailedExitCode = 125;
    private const int DescendantsSurvivedExitCode = 124;
    private const int SigStop = 19;
    private const int MaximumSweeps = 64;
    private static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(2);

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: AiDotNet.Evolution.Sandbox.Guardian <program> [args...]");
            return UsageExitCode;
        }

        if (!OperatingSystem.IsLinux() || prctl(PrSetChildSubreaper, 1, 0, 0, 0) != 0)
        {
            // Refuse rather than run uncontained: the engine only launches the guardian where it can work.
            Console.Error.WriteLine("sandbox guardian: PR_SET_CHILD_SUBREAPER is unavailable (errno "
                + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ").");
            return SetupFailedExitCode;
        }

        var start = new ProcessStartInfo(args[0]) { UseShellExecute = false };
        for (int index = 1; index < args.Length; index++) start.ArgumentList.Add(args[index]);

        int exitCode;
        try
        {
            using Process child = Process.Start(start)
                ?? throw new InvalidOperationException("The sandboxed command did not start.");
            child.WaitForExit();
            exitCode = child.ExitCode;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("sandbox guardian: " + exception.Message);
            return SetupFailedExitCode;
        }

        // A descendant that outlives the sweep would be reparented to init and escape: say so rather than hide it.
        if (!KillAndReapDescendants())
        {
            Console.Error.WriteLine("sandbox guardian: descendants survived termination.");
            return DescendantsSurvivedExitCode;
        }

        return exitCode;
    }

    // Everything still alive below this process is a detached descendant that outlived its command. Killing one can
    // reparent ITS children here too, so this sweeps until a pass finds nothing (bounded, in case of a fork bomb).
    // Each sweep stops every child first, so none can fork between being listed and being killed. Returns false when
    // descendants are still alive after the bound.
    private static bool KillAndReapDescendants()
    {
        for (int sweep = 0; sweep < MaximumSweeps; sweep++)
        {
            List<int> children = Children(Environment.ProcessId);
            if (children.Count == 0) break;
            foreach (int child in children) kill(child, SigStop);
            foreach (int child in children) kill(child, SigKill);
            Reap();
        }

        // kill only sends the signal: a child can still be exiting, or be a zombie waiting to be reaped, when the sweep
        // ends. Keep reaping for a bounded time and report survivors only if some are still there at the deadline.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            Reap();
            if (Children(Environment.ProcessId).Count == 0) return true;
            if (deadline.Elapsed >= TerminationGrace) return false;
            Thread.Sleep(10);
        }
    }

    private static void Reap()
    {
        // Orphans are not children the runtime started, so nothing else collects their exit status.
        while (waitpid(-1, out _, WaitNoHang) > 0)
        {
        }
    }

    private static List<int> Children(int processId)
    {
        var children = new List<int>();
        string tasks = "/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/task";
        string[] threads;
        try
        {
            threads = Directory.GetDirectories(tasks);
        }
        catch (IOException)
        {
            return children;
        }
        catch (UnauthorizedAccessException)
        {
            return children;
        }

        foreach (string thread in threads)
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(thread, "children"));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string token in text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int child)) children.Add(child);
            }
        }

        return children;
    }
}