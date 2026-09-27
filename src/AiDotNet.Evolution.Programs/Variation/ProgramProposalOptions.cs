namespace AiDotNet.Evolution.Programs;

/// <summary>Program bounds and prompt settings for model-driven variation; contains no builder or execution configuration.</summary>
public sealed class ProgramProposalOptions : ProgramTaskOptions
{
    /// <summary>Task description used when the prompt supplies no explicit description.</summary>
    public string? TaskDescription { get; set; }
    /// <summary>Prompt templates, context and size limits.</summary>
    public ProgramEvolutionPromptOptions Prompt { get; set; } = new();

    /// <inheritdoc/>
    public override ProgramProposalOptions Clone() => new()
    {
        Language = Language,
        EvolveBlockStartMarker = EvolveBlockStartMarker,
        EvolveBlockEndMarker = EvolveBlockEndMarker,
        EnforceEvolveBlocks = EnforceEvolveBlocks,
        MaxProgramChars = MaxProgramChars,
        Diff = Diff?.Clone() ?? throw new ArgumentException("Diff options cannot be null."),
        ResourceAccounting = ResourceAccounting,
        TaskDescription = TaskDescription,
        Prompt = Prompt?.Clone() ?? throw new ArgumentException("Prompt options cannot be null.")
    };

    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();
        if (Prompt is null) throw new ArgumentException("Prompt options cannot be null.");
        Prompt.Validate();
    }

    internal ProgramProposalOptions WithoutEvolveBlockEnforcement()
    {
        var copy = Clone();
        copy.EnforceEvolveBlocks = false;
        return copy;
    }
}
