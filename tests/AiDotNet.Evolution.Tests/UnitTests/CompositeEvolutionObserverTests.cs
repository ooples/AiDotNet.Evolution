using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>Fanning one run's events to several observers, and what happens when one of them fails.</summary>
public sealed class CompositeEvolutionObserverTests
{
    private sealed class Counting(Exception? failure = null) : IEvolutionObserver<int>
    {
        public int Seen;
        public ValueTask OnEventAsync(EvolutionEvent<int> evolutionEvent, CancellationToken cancellationToken = default)
        {
            Seen++;
            return failure is null ? default : throw failure;
        }
    }

    private static EvolutionEvent<int> Event(long sequence) => new(EvolutionEventKind.Proposed, sequence);

    [Fact]
    public async Task Every_observer_sees_every_event_in_order()
    {
        var first = new Counting();
        var second = new Counting();
        var composite = new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { first, second });

        await composite.OnEventAsync(Event(1));
        await composite.OnEventAsync(Event(2));

        Assert.Equal(2, composite.Count);
        Assert.Equal(2, first.Seen);
        Assert.Equal(2, second.Seen);
    }

    [Fact]
    public async Task One_observers_failure_does_not_cost_another_the_event()
    {
        // The reason this type exists with a policy rather than being a one-line loop in each caller: a
        // composite that let the first exception escape would turn a logging problem into a lost credit.
        var failing = new Counting(new InvalidOperationException("trace disk full"));
        var later = new Counting();
        var composite = new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { failing, later });

        await composite.OnEventAsync(Event(1));

        Assert.Equal(1, failing.Seen);
        Assert.Equal(1, later.Seen);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        // A cancelled run must stop rather than keep notifying, so this one exception is allowed out.
        using var cancellation = new CancellationTokenSource();
        // Cancel(), not CancelAsync(): the latter is .NET 8+ and this assembly also targets net471/net462.
        cancellation.Cancel();
        var observer = new Counting(new OperationCanceledException());
        var composite = new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { observer });

        await Assert.ThrowsAsync<OperationCanceledException>(() => composite.OnEventAsync(Event(1), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task An_uncancelled_cancellation_exception_is_isolated_like_any_other_failure()
    {
        // The escape hatch is guarded by the token, not by the exception type: an observer that throws
        // OperationCanceledException while nothing was cancelled has not been cancelled, it has failed. The
        // run was never asked to stop, so the remaining observers must still see the event. Pinned because a
        // refactor that dropped the "when" clause would start propagating this and the suite would not say so.
        var failing = new Counting(new OperationCanceledException());
        var later = new Counting();
        var composite = new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { failing, later });

        await composite.OnEventAsync(Event(1));

        Assert.Equal(1, failing.Seen);
        Assert.Equal(1, later.Seen);
    }

    [Fact]
    public async Task A_cancellation_mid_fan_out_stops_the_observers_after_it()
    {
        // The earlier test cancelled before the call, which only proves the exception escapes. This cancels
        // from INSIDE the first observer, so it also pins that later observers are skipped rather than
        // notified after the run was asked to stop.
        using var cancellation = new CancellationTokenSource();
        var later = new Counting();
        var cancelling = new Cancelling(cancellation);
        var composite = new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { cancelling, later });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => composite.OnEventAsync(Event(1), cancellation.Token).AsTask());

        Assert.Equal(1, cancelling.Seen);
        Assert.Equal(0, later.Seen);
    }

    private sealed class Cancelling(CancellationTokenSource source) : IEvolutionObserver<int>
    {
        public int Seen;
        public ValueTask OnEventAsync(EvolutionEvent<int> evolutionEvent, CancellationToken cancellationToken = default)
        {
            Seen++;
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    [Fact]
    public void An_empty_composite_is_allowed_and_a_null_or_repeated_observer_is_not()
    {
        var observer = new Counting();

        Assert.Equal(0, new CompositeEvolutionObserver<int>(Array.Empty<IEvolutionObserver<int>>()).Count);
        Assert.Throws<ArgumentNullException>(() => new CompositeEvolutionObserver<int>(null!));
        Assert.Throws<ArgumentException>(() => new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { observer, null! }));
        // Repeated observers would double whatever they count, and counting is the common reason to compose.
        Assert.Throws<ArgumentException>(() => new CompositeEvolutionObserver<int>(new IEvolutionObserver<int>[] { observer, observer }));
    }
}
