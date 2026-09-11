namespace AiDotNet.Evolution;

/// <summary>Explicitly opts a variation operator into concurrent, snapshot-local proposal calls.</summary>
/// <typeparam name="TGenome">The immutable task-specific genome.</typeparam>
/// <remarks>
/// Returning true promises that overlapping ProposeAsync calls do not mutate shared learning/random state or depend
/// on arrival/completion order. Use only each context's owned random stream and immutable inputs. Learning updates,
/// checkpoint calls and outcome feedback remain serialized after the pipeline wave drains. Ordinary operators are
/// serialized even when multiple proposal workers are configured. This is a capability contract, not a thread-safety
/// inference from the absence of a checkpoint interface. External responses must be recorded for exact replay.
/// </remarks>
public interface IDeterministicConcurrentVariationOperator<TGenome> : IVariationOperator<TGenome>
{
    /// <summary>Gets whether this configured instance supports deterministic overlapping proposal calls.</summary>
    bool SupportsDeterministicConcurrency { get; }
}
