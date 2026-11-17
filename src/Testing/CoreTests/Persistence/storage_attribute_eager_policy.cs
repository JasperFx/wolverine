using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Persistence;

// GH-5141: one Phase-A policy in core pre-populates IChain.AncillaryStoreType for every ancillary-store
// routing attribute, found through IAncillaryStoreAttribute, instead of one policy per integration
// ([MartenStore], [PolecatStore], [FisherStore]) looking for its own attribute type.
public class storage_attribute_eager_policy
{
    private static async Task<IHost> hostFor(params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAncillaryStoreFrameProvider, StubAncillaryStoreFrameProvider>();

                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static HandlerChain chainFor<T>(IHost host)
    {
        return host.Services.GetRequiredService<HandlerGraph>().ChainFor<T>()!;
    }

    [Fact]
    public async Task an_integration_store_attribute_on_the_handler_type_is_applied_eagerly()
    {
        using var host = await hostFor(typeof(EagerTypeLevelHandler));

        // The attribute is not a ModifyChainAttribute, so nothing but the eager policy can have set this
        chainFor<EagerTypeLevelMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }

    [Fact]
    public async Task an_integration_store_attribute_on_the_handler_method_is_applied_eagerly()
    {
        using var host = await hostFor(typeof(EagerMethodLevelHandler));

        chainFor<EagerMethodLevelMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }

    [Fact]
    public async Task the_method_attribute_wins_over_the_type_attribute()
    {
        using var host = await hostFor(typeof(EagerMethodOverridesTypeHandler));

        chainFor<EagerMethodOverridesTypeMessage>(host).AncillaryStoreType.ShouldBe(typeof(IOtherByAssemblyStore));
    }

    [Fact]
    public async Task the_generic_storage_attribute_is_still_applied_eagerly()
    {
        using var host = await hostFor(typeof(EagerStorageAttributeHandler));

        chainFor<EagerStorageAttributeMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }

    [Fact]
    public async Task a_handler_with_no_store_attribute_is_left_alone()
    {
        using var host = await hostFor(typeof(EagerUnattributedHandler));

        chainFor<EagerUnattributedMessage>(host).AncillaryStoreType.ShouldBeNull();
    }
}


/// <summary>
/// Stands in for [MartenStore] / [PolecatStore] / [FisherStore]: an ancillary-store routing attribute that
/// core knows only through <see cref="IAncillaryStoreAttribute"/>. Deliberately not a ModifyChainAttribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class FakeIntegrationStoreAttribute : Attribute, IAncillaryStoreAttribute
{
    public FakeIntegrationStoreAttribute(Type storeType)
    {
        StoreType = storeType;
    }

    public Type StoreType { get; }
}

public record EagerTypeLevelMessage;
public record EagerMethodLevelMessage;
public record EagerMethodOverridesTypeMessage;
public record EagerStorageAttributeMessage;
public record EagerUnattributedMessage;

[FakeIntegrationStore(typeof(IByAssemblyStore))]
public class EagerTypeLevelHandler
{
    public void Handle(EagerTypeLevelMessage message)
    {
    }
}

public class EagerMethodLevelHandler
{
    [FakeIntegrationStore(typeof(IByAssemblyStore))]
    public void Handle(EagerMethodLevelMessage message)
    {
    }
}

[FakeIntegrationStore(typeof(IByAssemblyStore))]
public class EagerMethodOverridesTypeHandler
{
    [FakeIntegrationStore(typeof(IOtherByAssemblyStore))]
    public void Handle(EagerMethodOverridesTypeMessage message)
    {
    }
}

public class EagerStorageAttributeHandler
{
    [Storage(typeof(IByAssemblyStore))]
    public void Handle(EagerStorageAttributeMessage message)
    {
    }
}

public class EagerUnattributedHandler
{
    public void Handle(EagerUnattributedMessage message)
    {
    }
}
