using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// A mapping is validated at its own Store call site, where the mistake is in front of the developer,
/// rather than at code generation time.
/// </summary>
public class blob_document_mapping_validation
{
    private record Invoice(string Id, string Body);

    private record NoIdentity(string Body);

    private record struct AStruct(string Id);

    [Fact]
    public void a_container_name_is_required()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().Store<Invoice>(x => x.BlobNameFor = ctx => $"{ctx.Id}.json"));

        ex.Message.ShouldContain("No container name was set");
    }

    [Fact]
    public void a_blob_name_function_is_required()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().Store<Invoice>(x => x.ContainerName = "some-container"));

        ex.Message.ShouldContain("No blob name function was set");
    }

    /// <summary>
    /// The message has to name the call the developer actually made, or a saga registration is told to
    /// fix itself with <c>Store&lt;T&gt;()</c> — the one call that would then refuse it.
    /// </summary>
    [Fact]
    public void the_message_names_the_registration_that_was_used()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().Saga<ABlobSaga>(x => x.ContainerName = "some-container"));

        ex.Message.ShouldContain("Saga<ABlobSaga>");
    }

    [Fact]
    public void an_identity_member_is_required_unless_the_type_is_named()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().Store<NoIdentity>(x =>
            {
                x.ContainerName = "some-container";
                x.BlobNameFor = ctx => $"{ctx.Id}.json";
            }));

        ex.Message.ShouldContain("No identity member could be found");
    }

    [Fact]
    public void an_explicit_identity_type_covers_a_document_with_no_identity_member()
    {
        Should.NotThrow(() => new AzureBlobStorageConfiguration().Store<NoIdentity>(x =>
        {
            x.ContainerName = "some-container";
            x.BlobNameFor = ctx => $"{ctx.Id}.json";
            x.IdentityType = typeof(string);
        }));
    }

    [Fact]
    public void only_reference_types_can_be_stored()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().Store(typeof(AStruct), _ => { }));

        ex.Message.ShouldContain("Only reference types");
    }

    [Fact]
    public void an_unregistered_type_says_where_to_register_it()
    {
        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            new AzureBlobStorageConfiguration().MappingFor(typeof(Invoice)));

        ex.Message.ShouldContain("UseAzureBlobStoragePersistence()");
    }

    /// <summary>
    /// Wolverine hands out a default-tenant sentinel rather than null for a message with no tenant. A
    /// blob name function should never see it, or every un-tenanted key grows a "*DEFAULT*" segment.
    /// </summary>
    [Fact]
    public void the_default_tenant_sentinel_is_normalised_away()
    {
        string? seen = "not called";

        var configuration = new AzureBlobStorageConfiguration();
        configuration.Store<Invoice>(x =>
        {
            x.ContainerName = "some-container";
            x.BlobNameFor = ctx =>
            {
                seen = ctx.TenantId;
                return $"{ctx.Id}.json";
            };
        });

        configuration.MappingFor(typeof(Invoice))
            .BlobNameForIdentity("abc", JasperFx.StorageConstants.DefaultTenantId);

        seen.ShouldBeNull();
    }
}
