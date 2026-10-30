namespace AiDotNet.Evolution.Programs;

/// <summary>Whether <see cref="ContainerExecutionEngine"/> reuses a container across candidates.</summary>
public enum ContainerLifecycle
{
    /// <summary>A started container runs candidates one after another. Between candidates every process it started is
    /// killed and its workdir is emptied; the container is replaced when that fails, after a timeout or a lost reply,
    /// and after <see cref="ContainerExecutionOptions.RecycleAfter"/> candidates. Adds milliseconds per candidate.</summary>
    Warm = 0,

    /// <summary>A fresh container per candidate, removed afterwards. Nothing survives between candidates, at the cost
    /// of a container start (hundreds of milliseconds) per evaluation.</summary>
    Cold = 1
}
