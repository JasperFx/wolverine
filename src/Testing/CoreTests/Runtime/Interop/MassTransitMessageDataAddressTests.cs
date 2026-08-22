using Shouldly;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;

namespace CoreTests.Runtime.Interop;

/// <summary>
/// GH-3510: address shapes transcribed from MassTransit's own repository implementations. MassTransit is
/// deliberately not a package reference here, so these are hand-built fixtures in the same spirit as
/// <c>MassTransitHeaders</c>.
/// </summary>
public class MassTransitMessageDataAddressTests
{
    private static string idFor(string address, out bool compressed)
        => MassTransitMessageDataAddress.ToPayloadId(new Uri(address), out compressed);

    [Fact]
    public void amazon_s3_and_file_system_urn_maps_colons_back_to_a_key()
    {
        // MassTransit's S3 + file-system repositories both return urn:file:{key} with path separators
        // replaced by colons.
        idFor("urn:file:2026-08-22-14:PW5rd0vBQZmpmxvA", out var compressed)
            .ShouldBe("2026-08-22-14/PW5rd0vBQZmpmxvA");

        compressed.ShouldBeFalse();
    }

    [Fact]
    public void a_flat_s3_key_has_no_separators_at_all()
    {
        idFor("urn:file:PW5rd0vBQZmpmxvA", out _).ShouldBe("PW5rd0vBQZmpmxvA");
    }

    [Fact]
    public void azure_blob_uri_drops_the_container_segment()
    {
        idFor("https://myaccount.blob.core.windows.net/message-data/2026-08-22/abc123", out var compressed)
            .ShouldBe("2026-08-22/abc123");

        compressed.ShouldBeFalse();
    }

    [Fact]
    public void a_gz_suffix_marks_the_payload_as_compressed()
    {
        idFor("https://myaccount.blob.core.windows.net/message-data/abc123.gz", out var compressed)
            .ShouldBe("abc123.gz");

        compressed.ShouldBeTrue();
    }

    [Fact]
    public void the_in_memory_repository_is_rejected_with_an_explanation()
    {
        var ex = Should.Throw<NotSupportedException>(() => idFor("urn:msgdata:PW5rd0vBQZmpmxvA", out _));

        // The failure has to name the actual problem -- a bare "not found" from the store would send
        // someone hunting through bucket permissions.
        ex.Message.ShouldContain("in-memory repository");
    }

    [Fact]
    public void an_unrecognised_urn_points_at_the_address_mapper_hook()
    {
        Should.Throw<NotSupportedException>(() => idFor("urn:something:else", out _))
            .Message.ShouldContain("ReadMessageDataFrom");
    }

    [Fact]
    public void a_blob_uri_with_no_blob_name_is_rejected()
    {
        Should.Throw<NotSupportedException>(() =>
            idFor("https://myaccount.blob.core.windows.net/message-data", out _));
    }
}
