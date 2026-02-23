using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

public class SchedulingCapabilityTests
{
    // --- SupportsNativeScheduledCancellation flag propagation ---

    [Fact]
    public void null_sender_does_not_support_cancellation()
    {
        var sender = new NullSender(new Uri("null://test"));
        sender.SupportsNativeScheduledCancellation.ShouldBeFalse();
    }

    [Fact]
    public void tenanted_sender_delegates_cancellation_flag_to_default_sender()
    {
        var defaultSender = Substitute.For<ISender>();
        defaultSender.SupportsNativeScheduledCancellation.Returns(true);

        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, defaultSender);
        sender.SupportsNativeScheduledCancellation.ShouldBeTrue();
    }

    [Fact]
    public void tenanted_sender_with_null_default_returns_false_for_cancellation()
    {
        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        sender.SupportsNativeScheduledCancellation.ShouldBeFalse();
    }

    [Fact]
    public void tenanted_sender_with_non_cancellable_default_returns_false()
    {
        var defaultSender = Substitute.For<ISender>();
        defaultSender.SupportsNativeScheduledCancellation.Returns(false);

        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, defaultSender);
        sender.SupportsNativeScheduledCancellation.ShouldBeFalse();
    }

    // --- TenantedSender.SenderForTenantId ---

    [Fact]
    public void tenanted_sender_resolves_correct_inner_sender_by_tenant_id()
    {
        var senderOne = Substitute.For<ISender>();
        var senderTwo = Substitute.For<ISender>();

        var tenanted = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        tenanted.RegisterSender("one", senderOne);
        tenanted.RegisterSender("two", senderTwo);

        tenanted.SenderForTenantId("one").ShouldBe(senderOne);
        tenanted.SenderForTenantId("two").ShouldBe(senderTwo);
    }

    // --- TenantedSender.CancelScheduledMessageAsync ---

    [Fact]
    public async Task tenanted_sender_cancel_delegates_to_default_cancellable_sender()
    {
        var defaultSender = Substitute.For<ISender, ISenderWithScheduledCancellation>();
        var cancelSender = (ISenderWithScheduledCancellation)defaultSender;
        defaultSender.SupportsNativeScheduledCancellation.Returns(true);

        var tenanted = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, defaultSender);

        var token = (object)42L;
        await tenanted.CancelScheduledMessageAsync(token);

        await cancelSender.Received().CancelScheduledMessageAsync(token, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task tenanted_sender_cancel_throws_when_default_does_not_support_cancellation()
    {
        var defaultSender = Substitute.For<ISender>();
        var tenanted = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, defaultSender);

        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await tenanted.CancelScheduledMessageAsync(42L);
        });
    }
}
