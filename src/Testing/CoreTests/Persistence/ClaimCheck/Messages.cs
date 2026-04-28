using Wolverine.Persistence;

namespace CoreTests.Persistence.ClaimCheck;

#region sample_blob_attribute_message

public record BlobByteArrayMessage(string Name, [property: Blob("application/pdf")] byte[]? Payload);

#endregion

public record BlobStringMessage(string Title, [property: Blob("text/plain")] string? Body);

public record MultiBlobMessage(
    string Description,
    [property: Blob("image/png")] byte[]? Image,
    [property: Blob("text/plain")] string? Notes);

public record PlainMessage(string Name, byte[] InlineBytes);
