using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;

namespace CoreTests.Runtime.Interop;

public record MtDocumentMessage(string Name, [property: Blob("application/pdf")] byte[]? Document);

public record MtNotesMessage(string Name, [property: Blob("text/plain")] string? Notes);

public record MtPlainMessage(string Name, byte[]? NotABlob);

/// <summary>
/// GH-3510: consuming MassTransit's MessageData claim-check references. The wire format is transcribed
/// from MassTransit's SystemTextMessageDataReference / SystemTextJsonMessageDataConverter — a JSON object
/// nested in the message body, NOT a header the way Wolverine's own claim checks work. MassTransit is
/// deliberately not referenced as a package, so the envelopes here are hand-built.
/// </summary>
public class MassTransitMessageDataTests
{
    private readonly IMassTransitInteropEndpoint theEndpoint = Substitute.For<IMassTransitInteropEndpoint>();
    private readonly RecordingInMemoryStore theStore = new();

    public MassTransitMessageDataTests()
    {
        theEndpoint.MassTransitReplyUri().Returns(new Uri("rabbitmq://localhost/responses"));
    }

    private MassTransitJsonSerializer serializer(Func<Uri, string>? addressToId = null)
    {
        var sut = new MassTransitJsonSerializer(theEndpoint);
        sut.ReadMessageDataFrom(theStore, addressToId);
        return sut;
    }

    /// <summary>Hand-build the MassTransit envelope wrapper around a message body.</summary>
    private static Envelope incoming(string bodyJson)
    {
        var envelope = new
        {
            messageId = Guid.NewGuid().ToString(),
            messageType = new[] { "urn:message:CoreTests.Runtime.Interop:MtDocumentMessage" },
            message = JsonDocument.Parse(bodyJson).RootElement
        };

        return new Envelope { Data = JsonSerializer.SerializeToUtf8Bytes(envelope) };
    }

    private T read<T>(MassTransitJsonSerializer sut, string bodyJson)
        => (T)sut.ReadFromData(typeof(T), incoming(bodyJson));

    [Fact]
    public void hydrates_a_byte_array_property_from_a_urn_file_reference()
    {
        var payload = "the actual pdf bytes"u8.ToArray();
        theStore.Seed("2026-08-22/abc123", payload);

        var message = read<MtDocumentMessage>(serializer(),
            """{"name":"invoice","document":{"data-ref":"urn:file:2026-08-22:abc123"}}""");

        message.Document.ShouldBe(payload);
        message.Name.ShouldBe("invoice");
    }

    [Fact]
    public void hydrates_a_string_property_from_an_azure_blob_reference()
    {
        theStore.Seed("2026-08-22/notes", "some very long notes"u8.ToArray());

        var message = read<MtNotesMessage>(serializer(),
            """{"name":"n","notes":{"data-ref":"https://acct.blob.core.windows.net/message-data/2026-08-22/notes"}}""");

        message.Notes.ShouldBe("some very long notes");
    }

    [Fact]
    public void gunzips_a_payload_whose_blob_name_ends_in_gz()
    {
        var payload = "compressed contents"u8.ToArray();

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        theStore.Seed("abc123.gz", buffer.ToArray());

        var message = read<MtDocumentMessage>(serializer(),
            """{"name":"z","document":{"data-ref":"https://acct.blob.core.windows.net/message-data/abc123.gz"}}""");

        message.Document.ShouldBe(payload);
    }

    [Fact]
    public void an_inline_text_payload_never_touches_the_store()
    {
        // MassTransit carries payloads below its 4KB threshold inline, and in that case the repository
        // may hold nothing at all under the address.
        var message = read<MtNotesMessage>(serializer(),
            """{"name":"n","notes":{"data-ref":"urn:file:abc","text":"carried inline"}}""");

        message.Notes.ShouldBe("carried inline");
        theStore.LoadCount.ShouldBe(0);
    }

    [Fact]
    public void an_inline_base64_payload_never_touches_the_store()
    {
        var payload = "inline bytes"u8.ToArray();

        var body = """{"name":"n","document":{"data-ref":"urn:file:abc","data":"""
                   + $"\"{Convert.ToBase64String(payload)}\"}}}}";

        var message = read<MtDocumentMessage>(serializer(), body);

        message.Document.ShouldBe(payload);
        theStore.LoadCount.ShouldBe(0);
    }

    [Fact]
    public void a_reference_with_no_address_reads_as_empty()
    {
        // Matches MassTransit's own converter, which returns EmptyMessageData for this shape.
        read<MtDocumentMessage>(serializer(), """{"name":"n","document":{}}""")
            .Document.ShouldBeNull();

        theStore.LoadCount.ShouldBe(0);
    }

    [Fact]
    public void a_plain_value_is_still_readable_on_a_blob_property()
    {
        // A Wolverine peer sending the same contract writes the property normally. The converter has to
        // tolerate that, otherwise enabling interop would break messages from your own services.
        var payload = "plain"u8.ToArray();

        read<MtDocumentMessage>(serializer(),
            $$"""{"name":"n","document":"{{Convert.ToBase64String(payload)}}"}""")
            .Document.ShouldBe(payload);

        theStore.LoadCount.ShouldBe(0);
    }

    [Fact]
    public void a_property_without_blob_is_left_completely_alone()
    {
        var payload = "not a claim check"u8.ToArray();

        read<MtPlainMessage>(serializer(),
            $$"""{"name":"n","notABlob":"{{Convert.ToBase64String(payload)}}"}""")
            .NotABlob.ShouldBe(payload);
    }

    [Fact]
    public void a_custom_address_mapper_overrides_the_built_in_translation()
    {
        theStore.Seed("mapped-id", "custom"u8.ToArray());

        var message = read<MtNotesMessage>(serializer(_ => "mapped-id"),
            """{"name":"n","notes":{"data-ref":"acme://whatever/we/like"}}""");

        message.Notes.ShouldBe("custom");
    }

    [Fact]
    public void message_data_interop_composes_with_UseSystemTextJsonForSerialization_in_either_order()
    {
        theStore.Seed("abc", "composed"u8.ToArray());

        var sut = new MassTransitJsonSerializer(theEndpoint);
        sut.ReadMessageDataFrom(theStore);
        sut.UseSystemTextJsonForSerialization(o => o.WriteIndented = true);

        read<MtNotesMessage>(sut, """{"name":"n","notes":{"data-ref":"urn:file:abc"}}""")
            .Notes.ShouldBe("composed");
    }

    private sealed class RecordingInMemoryStore : IClaimCheckStore
    {
        private readonly Dictionary<string, byte[]> _payloads = new();

        public int LoadCount;

        public void Seed(string id, byte[] payload) => _payloads[id] = payload;

        public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (!_payloads.TryGetValue(token.Id, out var bytes))
            {
                throw new KeyNotFoundException($"No payload seeded under '{token.Id}'.");
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
        }

        public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
