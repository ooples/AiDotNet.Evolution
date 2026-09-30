namespace AiDotNet.Evolution.Deployment;

/// <summary>What an <see cref="EvolutionDeploymentLifecycle"/> operation did.</summary>
public enum EvolutionDeploymentOutcome
{
    /// <summary>The candidate won the paired comparison and now serves.</summary>
    Promoted = 0,
    /// <summary>The candidate did not beat the incumbent by the policy's margin.</summary>
    InsufficientImprovement = 1,
    /// <summary>A validation measurement was unusable, so no comparison was made.</summary>
    InvalidValidation = 2,
    /// <summary>The candidate or the incumbent is quarantined.</summary>
    Quarantined = 3,
    /// <summary>The observed envelope or slot changed while the operation ran, so its result was discarded.</summary>
    Stale = 4,
    /// <summary>Another lifecycle operation was running.</summary>
    Busy = 5,
    /// <summary>The operation did not finish within its deadline and was left to settle without publishing.</summary>
    Abandoned = 6,
    /// <summary>The policy does not accept best-effort persistence, which this storage provides.</summary>
    PersistencePolicyDenied = 7,
    /// <summary>No retune was requested.</summary>
    NotRequested = 8,
    /// <summary>The lifetime retune budget or the cooldown refused the retune.</summary>
    BudgetDenied = 9,
    /// <summary>The observed window showed no regression.</summary>
    Healthy = 10,
    /// <summary>A regression was seen, but not yet for the number of consecutive windows that triggers a rollback.</summary>
    Monitoring = 11,
    /// <summary>The serving version regressed and the previous version was restored.</summary>
    RolledBack = 12,
    /// <summary>The serving version regressed with no earlier version to restore, so dispatch uses the fallback.</summary>
    QuarantinedFallback = 13
}