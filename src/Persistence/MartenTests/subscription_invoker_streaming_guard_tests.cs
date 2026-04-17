using Shouldly;
using Wolverine.Marten.Subscriptions;
using Xunit;

namespace MartenTests;

/// <summary>
///     Pins the contract that subscription-side IMessageInvoker implementations reject
///     streaming invocation. Subscriptions deliver events one-way; request/response
///     semantics (InvokeAsync with a reply) already throw NotSupportedException for the
///     same reason, and StreamAsync is even less applicable. Guarding here catches any
///     future refactor that accidentally relaxes the invariant.
/// </summary>
public class subscription_invoker_streaming_guard_tests
{
    [Fact]
    public async Task nullo_invoker_rejects_stream_async()
    {
        var invoker = new NulloMessageInvoker();

        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in invoker.StreamAsync<object>(new object(), null!))
            {
            }
        });
    }
}
