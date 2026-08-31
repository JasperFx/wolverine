using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.AzureBlobStorage.Internals;
using Wolverine.Persistence;

namespace Wolverine.AzureBlobStorage.Tests;

public class blob_persistence_frame_provider_tests
{
    private record Registered(string Id, string Body);

    private record NotRegistered(Guid Id);

    private record GuidIdentified(Guid Id, string Body);

    private static IServiceContainer containerFor(Action<AzureBlobStorageConfiguration> configure)
    {
        var configuration = new AzureBlobStorageConfiguration();
        configure(configuration);

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();

        return services.BuildServiceProvider().GetRequiredService<IServiceContainer>();
    }

    private static IServiceContainer theContainer => containerFor(blobs => blobs.Store<Registered>(x =>
    {
        x.ContainerName = "some-container";
        x.BlobNameFor = ctx => $"{ctx.Id}.json";
    }));

    /// <summary>
    /// Read through the interface deliberately, so a lost override reads the interface default of false
    /// and fails here rather than quietly making this provider a catch-all that shadows Marten.
    /// </summary>
    [Fact]
    public void is_not_a_catch_all()
    {
        IPersistenceFrameProvider provider = new BlobPersistenceFrameProvider();
        provider.IsCatchAll.ShouldBeFalse();
    }

    [Fact]
    public void claims_a_registered_document_type()
    {
        new BlobPersistenceFrameProvider()
            .CanPersist(typeof(Registered), theContainer, out var service)
            .ShouldBeTrue();

        service.ShouldBe(typeof(IBlobDocumentSession));
    }

    [Fact]
    public void does_not_claim_an_unregistered_document_type()
    {
        new BlobPersistenceFrameProvider()
            .CanPersist(typeof(NotRegistered), theContainer, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void does_not_claim_anything_when_wired_up_without_configuration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();
        var container = services.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        new BlobPersistenceFrameProvider().CanPersist(typeof(Registered), container, out _).ShouldBeFalse();
    }

    /// <summary>
    /// A message handler binds an [Entity] identity by exact CLR type, so this has to follow the
    /// document rather than being a hardcoded string.
    /// </summary>
    [Fact]
    public void takes_the_identity_type_from_the_document()
    {
        var container = containerFor(blobs =>
        {
            blobs.Store<Registered>(x =>
            {
                x.ContainerName = "some-container";
                x.BlobNameFor = ctx => $"{ctx.Id}.json";
            });

            blobs.Store<GuidIdentified>(x =>
            {
                x.ContainerName = "some-container";
                x.BlobNameFor = ctx => $"{ctx.Id}.json";
            });
        });

        var provider = new BlobPersistenceFrameProvider();

        provider.DetermineSagaIdType(typeof(Registered), container).ShouldBe(typeof(string));
        provider.DetermineSagaIdType(typeof(GuidIdentified), container).ShouldBe(typeof(Guid));
    }

    [Fact]
    public void an_explicit_identity_type_wins()
    {
        var container = containerFor(blobs => blobs.Store<GuidIdentified>(x =>
        {
            x.ContainerName = "some-container";
            x.BlobNameFor = ctx => $"{ctx.Id}.json";
            x.IdentityType = typeof(string);
        }));

        new BlobPersistenceFrameProvider()
            .DetermineSagaIdType(typeof(GuidIdentified), container)
            .ShouldBe(typeof(string));
    }

    /// <summary>
    /// There is no transaction to apply, and claiming an ordinary chain would make
    /// [Transactional]/AutoApplyTransactions resolve to a provider with nothing to commit.
    /// </summary>
    [Fact]
    public void claims_no_ordinary_chains()
    {
        new BlobPersistenceFrameProvider().CanApply(null!, theContainer).ShouldBeFalse();
    }

    [Fact]
    public void has_no_soft_delete_frames()
    {
        new BlobPersistenceFrameProvider().DetermineFrameToNullOutMaybeSoftDeleted(null!).ShouldBeEmpty();
    }

    /// <summary>
    /// Blob Storage has no query engine. Leaving these at the interface default is what makes [All],
    /// [FirstOrDefault] and [Queryable] fail at bootstrapping naming this provider.
    /// </summary>
    [Fact]
    public void does_not_pretend_to_be_queryable()
    {
        IPersistenceFrameProvider provider = new BlobPersistenceFrameProvider();
        var container = theContainer;

        provider.TryBuildAllFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
        provider.TryBuildFirstOrDefaultFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
        provider.TryBuildQueryableFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
    }
}
