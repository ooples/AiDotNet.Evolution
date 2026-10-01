using System.Diagnostics;
using AiDotNet.Evolution.CSharp;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>The hard compile bound: prompt, exception-preserving, and limited to a fixed number of compiles.</summary>
public sealed class CompileBoundTests
{
    [Fact]
    public void A_compile_that_fails_quickly_rethrows_its_own_exception()
    {
        using var timeout = new CancellationTokenSource();
        using var slots = new SemaphoreSlim(2, 2);
        // Not an AggregateException: the handlers in Apply catch the original types.
        Assert.Throws<IOException>(() => CSharpPatchCompiler.WithinBound<int>(() => throw new IOException("image"),
            Stopwatch.StartNew(), TimeSpan.FromSeconds(10), timeout, slots));
        Assert.Throws<OperationCanceledException>(() => CSharpPatchCompiler.WithinBound<int>(
            () => throw new OperationCanceledException(), Stopwatch.StartNew(), TimeSpan.FromSeconds(10), timeout, slots));
        SpinWait.SpinUntil(() => slots.CurrentCount == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, slots.CurrentCount);
    }

    [Fact]
    public void A_compile_past_its_bound_returns_at_the_bound_and_keeps_its_slot_until_it_ends()
    {
        using var timeout = new CancellationTokenSource();
        using var slots = new SemaphoreSlim(1, 1);
        using var release = new ManualResetEventSlim();
        var clock = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => CSharpPatchCompiler.WithinBound(() => { release.Wait(); return 1; },
            Stopwatch.StartNew(), TimeSpan.FromMilliseconds(200), timeout, slots));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the bound did not return promptly: " + clock.Elapsed);
        Assert.True(timeout.IsCancellationRequested);
        // The abandoned compile still holds the only slot, so the next one is refused at its own bound.
        Assert.Equal(0, slots.CurrentCount);
        using var second = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => CSharpPatchCompiler.WithinBound(() => 2,
            Stopwatch.StartNew(), TimeSpan.FromMilliseconds(200), second, slots));
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => slots.CurrentCount == 1, TimeSpan.FromSeconds(5)), "the slot was not returned");
        using var third = new CancellationTokenSource();
        Assert.Equal(3, CSharpPatchCompiler.WithinBound(() => 3, Stopwatch.StartNew(), TimeSpan.FromSeconds(5), third, slots));
    }
}