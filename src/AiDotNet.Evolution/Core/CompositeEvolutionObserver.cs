namespace AiDotNet.Evolution;

/// <summary>Fans one run's events out to several observers, in the order they were supplied.</summary>
/// <remarks>
/// <para>
/// <see cref="EvolutionEngine{TGenome}"/> takes one observer, so a run that wants two - a trace writer and
/// something that feeds a result back into the search, say - has nowhere to put the second. This is that
/// place, rather than each caller inventing its own wrapper and choosing its own failure policy.
/// </para>
/// <para>
/// <b>One observer's failure does not silence the rest.</b> The engine already isolates observer failures
/// from deterministic state, and a composite that let the first exception escape would turn a logging
/// problem into a lost credit or a missing trace. A recoverable failure is therefore swallowed per observer
/// and the remaining observers still see the event; cancellation is not swallowed, because a cancelled run
/// must stop rather than continue notifying.
/// </para>
/// <para><b>For Beginners:</b> use this when you want more than one thing watching a run - for example
/// writing a trace file and updating something that learns from outcomes. Each watcher sees every event, and
/// one of them failing will not stop the others from seeing it.</para>
/// </remarks>
/// <typeparam name="TGenome">The genome type the run produces.</typeparam>
public sealed class CompositeEvolutionObserver<TGenome> : IEvolutionObserver<TGenome>
{
    private readonly IEvolutionObserver<TGenome>[] _observers;

    /// <summary>Creates a composite over the supplied observers.</summary>
    /// <param name="observers">The observers to notify, in order. Nulls are refused rather than skipped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observers"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">An element is <c>null</c>, or the same observer appears twice.</exception>
    public CompositeEvolutionObserver(IEnumerable<IEvolutionObserver<TGenome>> observers)
    {
        Guard.NotNull(observers);
        _observers = observers.ToArray();
        for (int index = 0; index < _observers.Length; index++)
        {
            if (_observers[index] is null)
                throw new ArgumentException("An observer cannot be null.", nameof(observers));
            // A repeated observer would see each event twice, which doubles whatever it counts - and a
            // counting observer is the common case for wanting a composite at all.
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (ReferenceEquals(_observers[earlier], _observers[index]))
                    throw new ArgumentException("The same observer was supplied twice.", nameof(observers));
            }
        }
    }

    /// <summary>Gets how many observers this composite notifies.</summary>
    public int Count => _observers.Length;

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(EvolutionEvent<TGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        foreach (IEvolutionObserver<TGenome> observer in _observers)
        {
            try
            {
                await observer.OnEventAsync(evolutionEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (EvolutionExceptionPolicy.IsRecoverable(exception))
            {
                // Isolated per observer, matching the engine's own policy: one observer's failure must not
                // cost another observer the event.
            }
        }
    }
}
