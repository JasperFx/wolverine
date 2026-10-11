using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

public class composite_listener_disposal
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task closes_later_children_and_preserves_single_failure(bool parallel, bool synchronous)
    {
        var failure = new InvalidOperationException("first child disposal failed");
        var first = new OwnedListener(() => synchronous ? throw failure : ValueTask.FromException(failure));
        var second = new OwnedListener(() => ValueTask.CompletedTask);
        var listener = Composite(parallel, first, second);

        var observed = await Record.ExceptionAsync(() => listener.DisposeAsync().AsTask());

        Assert.Equal(1, second.DisposalCalls);
        Assert.True(second.ResourceClosed);
        Assert.Equal(1, first.DisposalCalls);
        Assert.Same(failure, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task retains_all_failures_in_child_order_after_attempting_all_children(bool parallel)
    {
        var firstFailure = new InvalidOperationException("first disposal");
        var secondFailure = new IOException("second disposal");
        var first = new OwnedListener(() => throw firstFailure);
        var second = new OwnedListener(() => ValueTask.FromException(secondFailure));
        var third = new OwnedListener(() => ValueTask.CompletedTask);
        var listener = Composite(parallel, first, second, third);

        var observed = await Record.ExceptionAsync(() => listener.DisposeAsync().AsTask());

        Assert.True(third.ResourceClosed);
        Assert.Equal(1, second.DisposalCalls);
        var aggregate = Assert.IsType<AggregateException>(observed);
        Assert.Equal(new Exception[] { firstFailure, secondFailure }, aggregate.InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task waits_for_inflight_disposal_before_continuing_after_its_failure(bool parallel)
    {
        var failure = new IOException("inflight disposal failed");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new OwnedListener(() => new ValueTask(release.Task));
        var second = new OwnedListener(() => ValueTask.CompletedTask);
        var listener = Composite(parallel, first, second);

        var disposal = listener.DisposeAsync().AsTask();
        Exception? observed = null;
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, first.DisposalCalls);
            Assert.Equal(0, second.DisposalCalls);
        }
        finally
        {
            release.TrySetException(failure);
            try { await disposal; }
            catch (Exception e) { observed = e; }
        }

        Assert.True(second.ResourceClosed);
        Assert.Same(failure, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task cancellation_preserves_token_and_closes_later_child(bool parallel)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var first = new OwnedListener(() => new ValueTask(Task.FromCanceled(cancellation.Token)));
        var last = new OwnedListener(() => ValueTask.CompletedTask);

        var observed = await Record.ExceptionAsync(() => Composite(parallel, first, last).DisposeAsync().AsTask());

        Assert.True(last.ResourceClosed);
        var canceled = Assert.IsType<TaskCanceledException>(observed);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task cancellation_and_fatal_failure_are_retained_in_order(bool parallel)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var fatal = new IOException("fatal disposal after cancellation");
        var first = new OwnedListener(() => new ValueTask(Task.FromCanceled(cancellation.Token)));
        var second = new OwnedListener(() => throw fatal);
        var last = new OwnedListener(() => ValueTask.CompletedTask);

        var observed = await Record.ExceptionAsync(() => Composite(parallel, first, second, last).DisposeAsync().AsTask());

        Assert.True(last.ResourceClosed);
        var aggregate = Assert.IsType<AggregateException>(observed);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        var canceled = Assert.IsType<TaskCanceledException>(aggregate.InnerExceptions[0]);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Same(fatal, aggregate.InnerExceptions[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task nested_failure_does_not_abandon_outer_child(bool parallel)
    {
        var firstFailure = new IOException("nested first");
        var secondFailure = new InvalidOperationException("nested second");
        var nested = Composite(!parallel,
            new OwnedListener(() => ValueTask.FromException(firstFailure)),
            new OwnedListener(() => ValueTask.FromException(secondFailure)));
        var last = new OwnedListener(() => ValueTask.CompletedTask);

        var observed = await Record.ExceptionAsync(() => Composite(parallel, nested, last).DisposeAsync().AsTask());

        Assert.True(last.ResourceClosed);
        var aggregate = Assert.IsType<AggregateException>(observed);
        Assert.Equal(new Exception[] { firstFailure, secondFailure }, aggregate.InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task successful_disposal_closes_each_child(bool parallel)
    {
        var children = Enumerable.Range(0, 3).Select(_ => new OwnedListener(() => ValueTask.CompletedTask)).ToArray();
        await Composite(parallel, children).DisposeAsync();
        Assert.All(children, child => { Assert.True(child.ResourceClosed); Assert.Equal(1, child.DisposalCalls); });
    }

    [Fact]
    public async Task parallel_disposal_ignores_null_and_closes_healthy_child()
    {
        var healthy = new OwnedListener(() => ValueTask.CompletedTask);
        await new ParallelListener(new Uri("stub://composite"), new IListener[] { null!, healthy }).DisposeAsync();
        Assert.True(healthy.ResourceClosed);
        Assert.Equal(1, healthy.DisposalCalls);
    }

    [Fact]
    public async Task parallel_disposal_ignores_null_and_preserves_single_failure()
    {
        var failure = new IOException("child disposal failed");
        var first = new OwnedListener(() => ValueTask.FromException(failure));
        var healthy = new OwnedListener(() => ValueTask.CompletedTask);
        var listener = new ParallelListener(new Uri("stub://composite"), new IListener[] { null!, first, null!, healthy });

        var observed = await Record.ExceptionAsync(() => listener.DisposeAsync().AsTask());

        Assert.Same(failure, observed);
        Assert.Equal(1, first.DisposalCalls);
        Assert.True(healthy.ResourceClosed);
        Assert.Equal(1, healthy.DisposalCalls);
    }
    private static IListener Composite(bool parallel, params IListener[] children)
    {
        var address = new Uri("stub://composite");
        if (parallel) return new ParallelListener(address, children);
        var compound = new CompoundListener(address);
        compound.Inner.AddRange(children);
        return compound;
    }

    private sealed class OwnedListener(Func<ValueTask> dispose) : IListener
    {
        public int DisposalCalls { get; private set; }
        public bool ResourceClosed { get; private set; }
        public Uri Address => new("stub://child");
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Wolverine.Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Wolverine.Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask StopAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposalCalls++;
            var result = dispose();
            ResourceClosed = result.IsCompletedSuccessfully;
            return result;
        }
    }
}
