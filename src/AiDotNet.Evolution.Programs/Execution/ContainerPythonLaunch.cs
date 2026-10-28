namespace AiDotNet.Evolution.Programs;

/// <summary>How <see cref="ContainerExecutionEngine"/> starts a Python candidate inside the container.</summary>
public enum ContainerPythonLaunch
{
    /// <summary>The in-container supervisor forks and runs the candidate as <c>__main__</c> in the child, so no
    /// interpreter starts per candidate. The child begins with the supervisor's already-imported standard-library
    /// modules (<c>json</c>, <c>os</c>, <c>select</c>, <c>struct</c>, <c>threading</c>); it shares no state with
    /// other candidates. The <see cref="ProgramLanguage.Python"/> entry of
    /// <see cref="ContainerExecutionOptions.Interpreters"/> is not used.</summary>
    ForkSupervisor = 0,

    /// <summary>Every candidate starts a fresh interpreter from the <see cref="ProgramLanguage.Python"/> entry of
    /// <see cref="ContainerExecutionOptions.Interpreters"/>, which costs an interpreter start per candidate.</summary>
    FreshInterpreter = 1
}
