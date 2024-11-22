using JasperFx.Core;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

public class TenantedSenderTests
{
    private readonly ISender theDefault = Substitute.For<ISender>();
    private readonly ISender one = Substitute.For<ISender>();
    private readonly ISender two = Substitute.For<ISender>();
    private readonly ISender three = Substitute.For<ISender>();

    [Fact]
    public async Task switch_on_known_tenants_happy_path()
    {
        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        
        sender.RegisterSender("one", one);
        sender.RegisterSender("two", two);
        sender.RegisterSender("three", three);

        var e1 = ObjectMother.Envelope();
        e1.TenantId = "one";
        
        var e2 = ObjectMother.Envelope();
        e2.TenantId = "two";
        
        var e3 = ObjectMother.Envelope();
        e3.TenantId = "three";

        await sender.SendAsync(e1);
        await sender.SendAsync(e2);
        await sender.SendAsync(e3);

        await one.Received().SendAsync(e1);
        await two.Received().SendAsync(e2);
        await three.Received().SendAsync(e3);
    }

    [Fact]
    public void default_sender_is_required_if_using_fallback_to_default()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, null);
        });
    }

    [Fact]
    public async Task throw_when_tenant_id_is_required()
    {
        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        
        sender.RegisterSender("one", one);
        sender.RegisterSender("two", two);
        sender.RegisterSender("three", three);
        
        var e1 = ObjectMother.Envelope();
        e1.TenantId = null;

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await sender.SendAsync(e1);
        });
        
        // Same on empty
        e1.TenantId = string.Empty;

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await sender.SendAsync(e1);
        });
    }

    [Fact]
    public async Task fall_back_to_default()
    {
        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.FallbackToDefault, theDefault);
        
        sender.RegisterSender("one", one);
        sender.RegisterSender("two", two);
        sender.RegisterSender("three", three);
        
        var e1 = ObjectMother.Envelope();
        e1.TenantId = null;

        var e2 = ObjectMother.Envelope();
        e2.TenantId = "two";
        
        var e3 = ObjectMother.Envelope();
        e3.TenantId = string.Empty;

        await sender.SendAsync(e1);
        await sender.SendAsync(e2);
        await sender.SendAsync(e3);

        await theDefault.Received().SendAsync(e1);
        await two.Received().SendAsync(e2);
        await theDefault.Received().SendAsync(e3);
    }

    [Fact]
    public async Task assert_that_tenant_exists()
    {
        var sender = new TenantedSender("tcp://localhost:1000".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        
        sender.RegisterSender("one", one);
        sender.RegisterSender("two", two);
        sender.RegisterSender("three", three);
        
        var e1 = ObjectMother.Envelope();
        e1.TenantId = "four";

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await sender.SendAsync(e1);
        });
    }
}