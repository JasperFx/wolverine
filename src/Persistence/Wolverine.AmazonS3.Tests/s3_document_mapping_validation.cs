using Shouldly;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// A mapping is validated at its own Store call site, where the mistake is in front of the developer,
/// rather than at code generation time.
/// </summary>
public class s3_document_mapping_validation
{
    private record Invoice(string Id, string Body);

    private record NoIdentity(string Body);

    private record struct AStruct(string Id);

    [Fact]
    public void a_bucket_name_is_required()
    {
        var ex = Should.Throw<InvalidS3DocumentMappingException>(() =>
            new AmazonS3Configuration().Store<Invoice>(x => x.KeyFor = ctx => $"{ctx.Id}.json"));

        ex.Message.ShouldContain("No bucket name was set");
    }

    [Fact]
    public void a_key_function_is_required()
    {
        var ex = Should.Throw<InvalidS3DocumentMappingException>(() =>
            new AmazonS3Configuration().Store<Invoice>(x => x.BucketName = "some-bucket"));

        ex.Message.ShouldContain("No object key function was set");
    }

    [Fact]
    public void an_identity_member_is_required_unless_the_type_is_named()
    {
        var ex = Should.Throw<InvalidS3DocumentMappingException>(() =>
            new AmazonS3Configuration().Store<NoIdentity>(x =>
            {
                x.BucketName = "some-bucket";
                x.KeyFor = ctx => $"{ctx.Id}.json";
            }));

        ex.Message.ShouldContain("No identity member could be found");
    }

    [Fact]
    public void an_explicit_identity_type_covers_a_document_with_no_identity_member()
    {
        Should.NotThrow(() => new AmazonS3Configuration().Store<NoIdentity>(x =>
        {
            x.BucketName = "some-bucket";
            x.KeyFor = ctx => $"{ctx.Id}.json";
            x.IdentityType = typeof(string);
        }));
    }

    [Fact]
    public void only_reference_types_can_be_stored()
    {
        var ex = Should.Throw<InvalidS3DocumentMappingException>(() =>
            new AmazonS3Configuration().Store(typeof(AStruct), _ => { }));

        ex.Message.ShouldContain("Only reference types");
    }

    [Fact]
    public void an_unregistered_type_says_where_to_register_it()
    {
        var ex = Should.Throw<InvalidS3DocumentMappingException>(() =>
            new AmazonS3Configuration().MappingFor(typeof(Invoice)));

        ex.Message.ShouldContain("UseAmazonS3Persistence()");
    }
}
