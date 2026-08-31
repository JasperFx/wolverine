using Amazon.S3;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.AmazonS3.Internals;
using Wolverine.Persistence;

namespace Wolverine.AmazonS3.Tests;

public class s3_persistence_frame_provider_tests
{
    private record Registered(string Id, string Body);

    private record NotRegistered(Guid Id);

    private record GuidIdentified(Guid Id, string Body);

    private static IServiceContainer containerFor(Action<AmazonS3Configuration> configure)
    {
        var configuration = new AmazonS3Configuration();
        configure(configuration);

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();

        return services.BuildServiceProvider().GetRequiredService<IServiceContainer>();
    }

    private static IServiceContainer theContainer => containerFor(s3 => s3.Store<Registered>(x =>
    {
        x.BucketName = "some-bucket";
        x.KeyFor = ctx => $"{ctx.Id}.json";
    }));

    /// <summary>
    /// Read through the interface deliberately, so a lost override reads the interface default of false
    /// and fails here rather than quietly making this provider a catch-all that shadows Marten.
    /// </summary>
    [Fact]
    public void is_not_a_catch_all()
    {
        IPersistenceFrameProvider provider = new S3PersistenceFrameProvider();
        provider.IsCatchAll.ShouldBeFalse();
    }

    [Fact]
    public void claims_a_registered_document_type()
    {
        new S3PersistenceFrameProvider()
            .CanPersist(typeof(Registered), theContainer, out var service)
            .ShouldBeTrue();

        service.ShouldBe(typeof(IS3DocumentSession));
    }

    [Fact]
    public void does_not_claim_an_unregistered_document_type()
    {
        new S3PersistenceFrameProvider()
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

        new S3PersistenceFrameProvider().CanPersist(typeof(Registered), container, out _).ShouldBeFalse();
    }

    /// <summary>
    /// A message handler binds an [Entity] identity by exact CLR type, so this has to follow the
    /// document rather than being a hardcoded string.
    /// </summary>
    [Fact]
    public void takes_the_identity_type_from_the_document()
    {
        var container = containerFor(s3 =>
        {
            s3.Store<Registered>(x =>
            {
                x.BucketName = "some-bucket";
                x.KeyFor = ctx => $"{ctx.Id}.json";
            });

            s3.Store<GuidIdentified>(x =>
            {
                x.BucketName = "some-bucket";
                x.KeyFor = ctx => $"{ctx.Id}.json";
            });
        });

        var provider = new S3PersistenceFrameProvider();

        provider.DetermineSagaIdType(typeof(Registered), container).ShouldBe(typeof(string));
        provider.DetermineSagaIdType(typeof(GuidIdentified), container).ShouldBe(typeof(Guid));
    }

    [Fact]
    public void an_explicit_identity_type_wins()
    {
        var container = containerFor(s3 => s3.Store<GuidIdentified>(x =>
        {
            x.BucketName = "some-bucket";
            x.KeyFor = ctx => $"{ctx.Id}.json";
            x.IdentityType = typeof(string);
        }));

        new S3PersistenceFrameProvider()
            .DetermineSagaIdType(typeof(GuidIdentified), container)
            .ShouldBe(typeof(string));
    }

    /// <summary>
    /// There is no transaction to apply, and claiming a chain would make AutoApplyTransactions
    /// ambiguous in the normal case of S3 documents alongside a transactional store.
    /// </summary>
    [Fact]
    public void claims_no_chains()
    {
        new S3PersistenceFrameProvider().CanApply(null!, theContainer).ShouldBeFalse();
    }

    [Fact]
    public void has_no_soft_delete_frames()
    {
        new S3PersistenceFrameProvider().DetermineFrameToNullOutMaybeSoftDeleted(null!).ShouldBeEmpty();
    }

    /// <summary>
    /// S3 has no query engine. Leaving these at the interface default is what makes [All],
    /// [FirstOrDefault] and [Queryable] fail at bootstrapping naming this provider.
    /// </summary>
    [Fact]
    public void does_not_pretend_to_be_queryable()
    {
        IPersistenceFrameProvider provider = new S3PersistenceFrameProvider();
        var container = theContainer;

        provider.TryBuildAllFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
        provider.TryBuildFirstOrDefaultFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
        provider.TryBuildQueryableFrame(typeof(Registered), container, out _, out _).ShouldBeFalse();
    }
}
