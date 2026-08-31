using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

public class blob_document_serializer_tests
{
    private record Invoice(string Id, string Body);

    private static readonly Invoice theInvoice = new("ABC-123", "one hundred euro");

    [Fact]
    public void plain_json_round_trips_and_declares_no_encoding()
    {
        var serializer = new BlobDocumentSerializer();

        serializer.ContentType.ShouldBe("application/json");
        serializer.ContentEncoding.ShouldBeNull();

        serializer.Deserialize<Invoice>(serializer.Serialize(theInvoice)).ShouldBe(theInvoice);
    }

    [Theory]
    [InlineData(BlobCompression.Brotli, "br")]
    [InlineData(BlobCompression.GZip, "gzip")]
    public void a_compressed_document_round_trips_and_declares_its_encoding(BlobCompression compression,
        string encoding)
    {
        var serializer = new BlobDocumentSerializer(compression: compression);

        serializer.ContentEncoding.ShouldBe(encoding);

        serializer.Deserialize<Invoice>(serializer.Serialize(theInvoice)).ShouldBe(theInvoice);
    }

    [Fact]
    public void compression_actually_compresses()
    {
        // A short document can come out larger, so use one with something to compress.
        var repetitive = new Invoice("ABC-123", new string('a', 4096));

        var plain = new BlobDocumentSerializer().Serialize(repetitive).Length;
        var compressed = new BlobDocumentSerializer(compression: BlobCompression.Brotli)
            .Serialize(repetitive).Length;

        compressed.ShouldBeLessThan(plain);
    }
}
