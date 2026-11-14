using Shouldly;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class cancellation_shards
{
    // A message's timeout source links to its thread's intermediate token, so the receiver's own token must still
    // reach every in-flight message through whichever intermediate the message's thread drew
    [Fact]
    public async Task cancelling_the_parent_cancels_every_message_linked_through_a_shard()
    {
        using var parent = new CancellationTokenSource();

        var messages = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            CancellationTokenSource.CreateLinkedTokenSource(CancellationShards.For(parent.Token)),
            TestContext.Current.CancellationToken)));

        messages.ShouldAllBe(x => !x.IsCancellationRequested);

        await parent.CancelAsync();

        messages.ShouldAllBe(x => x.IsCancellationRequested);
    }

    [Fact]
    public async Task the_shards_are_forgotten_once_the_parent_is_cancelled()
    {
        using var parent = new CancellationTokenSource();

        CancellationShards.For(parent.Token);
        CancellationShards.IsTracking(parent.Token).ShouldBeTrue();

        await parent.CancelAsync();

        CancellationShards.IsTracking(parent.Token).ShouldBeFalse();
    }

    [Fact]
    public void a_token_that_cannot_be_cancelled_is_used_as_is()
    {
        CancellationShards.For(CancellationToken.None).ShouldBe(CancellationToken.None);
    }

    [Fact]
    public void an_already_cancelled_token_is_used_as_is_and_not_tracked()
    {
        var cancelled = new CancellationToken(true);

        CancellationShards.For(cancelled).ShouldBe(cancelled);
        CancellationShards.IsTracking(cancelled).ShouldBeFalse();
    }
}
