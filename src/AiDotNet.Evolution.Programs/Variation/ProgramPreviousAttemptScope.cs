namespace AiDotNet.Evolution.Programs;

/// <summary>Chooses whose evaluated children the prompt's previous-attempts section recounts.</summary>
/// <remarks>
/// <para>
/// Each recounted child shows what it changed, what it measured, and whether that improved on its own parent. The
/// scope decides which children qualify. <see cref="Island"/> recounts the recent children of the parent's island,
/// as OpenEvolve recounts its island's programs; <see cref="Parent"/> only those of the parent being varied;
/// <see cref="Run"/> the recent children of the whole run. Replies on this same parent that could not be applied are
/// recounted under every scope, since they are the edits the model is most likely to repeat.
/// </para>
/// <para><b>For Beginners:</b> This picks which earlier results the AI is reminded of: its island's recent work,
/// only the work built on the program it is now editing, or everything recent.</para>
/// </remarks>
public enum ProgramPreviousAttemptScope
{
    /// <summary>Recent children evaluated on the parent's island.</summary>
    Island = 0,
    /// <summary>Recent children of the parent being varied.</summary>
    Parent = 1,
    /// <summary>Recent children from every island.</summary>
    Run = 2
}
