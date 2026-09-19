// Relocated from ooples/AiDotNet d4535f7376888a7c2d35d7e6229494c4c60c0ac6:src/Evolution/Deployment/EvolutionDeploymentSelection.cs
// Original BSL license retained in AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Deployment;

/// <summary>An exact dispatch selection and revision token for attributing subsequent monitoring windows.</summary>
public sealed class EvolutionDeploymentSelection
{
    internal EvolutionDeploymentSelection(EvolutionDeployableArtifact artifact, string? revision, bool fallback)
    { Artifact = artifact; Revision = revision; IsFallback = fallback; }
    /// <summary>Gets immutable deployable bytes; instantiate a model only when its artifact identity changes.</summary>
    public EvolutionDeployableArtifact Artifact { get; }
    /// <summary>Gets the observed slot revision; stale observations cannot quarantine a later promotion of the same artifact.</summary>
    public string? Revision { get; }
    /// <summary>Gets whether dispatch must use the application's known-valid fallback.</summary>
    public bool IsFallback { get; }
}
