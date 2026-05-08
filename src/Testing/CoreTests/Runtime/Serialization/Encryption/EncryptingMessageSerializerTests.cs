using Shouldly;
using Wolverine;
using Wolverine.Newtonsoft;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class EncryptingMessageSerializerTests
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static EncryptingMessageSerializer NewSut(IMessageSerializer? inner = null, IKeyProvider? provider = null)
    {
        inner ??= new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        provider ??= new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) });
        return new EncryptingMessageSerializer(inner, provider);
    }

    [Fact]
    public void content_type_is_dedicated_encrypted_value()
    {
        NewSut().ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
    }

    [Fact]
    public void implements_async_serializer()
    {
        NewSut().ShouldBeAssignableTo<IAsyncMessageSerializer>();
    }

    [Fact]
    public void sync_write_bridges_to_async_and_round_trips()
    {
        var sut = NewSut();
        var envelope = new Envelope { Message = new HelloMessage("sync-write") };

        // Sync surface should produce the same on-the-wire envelope shape as async.
        var bytes = sut.Write(envelope);

        bytes.Length.ShouldBeGreaterThan(12 + 16);
        envelope.Headers.ContainsKey(EncryptionHeaders.KeyIdHeader).ShouldBeTrue();
        envelope.Headers[EncryptionHeaders.KeyIdHeader].ShouldBe("k1");
    }

    [Fact]
    public void sync_read_from_data_envelope_bridges_to_async_and_decrypts()
    {
        var sut = NewSut();
        var sendEnvelope = new Envelope { Message = new HelloMessage("sync-read") };
        var bytes = sut.Write(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var msg = sut.ReadFromData(typeof(HelloMessage), recvEnvelope);

        msg.ShouldBeOfType<HelloMessage>().Greeting.ShouldBe("sync-read");
    }

    [Fact]
    public void WriteMessage_no_envelope_path_throws_invalid_operation()
    {
        // The no-envelope overload cannot stamp a key-id header, so encryption
        // is impossible. Returning the inner serializer's plaintext under a
        // ContentType that advertises encryption would be a silent confidentiality
        // bug, so this overload fails loudly instead.
        var inner = new TrackingInnerSerializer();
        var sut = new EncryptingMessageSerializer(inner,
            new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) }));

        var ex = Should.Throw<InvalidOperationException>(() => sut.WriteMessage(new HelloMessage("plain")));

        inner.WriteMessageCallCount.ShouldBe(0);
        ex.Message.ShouldContain("Envelope");
    }

    [Fact]
    public void ReadFromData_byte_array_no_envelope_path_throws_invalid_operation()
    {
        // Symmetric to WriteMessage(object): no envelope context means no
        // key-id header, so decryption cannot proceed. Fail loudly so a stray
        // caller can't silently bypass decryption.
        var inner = new TrackingInnerSerializer();
        var sut = new EncryptingMessageSerializer(inner,
            new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) }));

        var ex = Should.Throw<InvalidOperationException>(() => sut.ReadFromData(new byte[] { 1, 2, 3 }));

        inner.ReadFromDataBytesCallCount.ShouldBe(0);
        ex.ShouldNotBeAssignableTo<MessageEncryptionException>();
        ex.Message.ShouldContain("Envelope");
    }

    private sealed class TrackingInnerSerializer : IMessageSerializer
    {
        public int WriteMessageCallCount;
        public int ReadFromDataBytesCallCount;

        public string ContentType => EnvelopeConstants.JsonContentType;

        public byte[] Write(Envelope model)
        {
            return new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()).Write(model);
        }

        public byte[] WriteMessage(object message)
        {
            WriteMessageCallCount++;
            return new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()).WriteMessage(message);
        }

        public object ReadFromData(Type messageType, Envelope envelope)
            => new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()).ReadFromData(messageType, envelope);

        public object ReadFromData(byte[] data)
        {
            ReadFromDataBytesCallCount++;
            return new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()).ReadFromData(data);
        }
    }

    [Fact]
    public async Task write_async_sets_content_type_and_key_id_headers()
    {
        var sut = NewSut();
        var envelope = new Envelope { Message = new HelloMessage("world") };

        var bytes = await sut.WriteAsync(envelope);

        envelope.Headers.ContainsKey(EncryptionHeaders.KeyIdHeader).ShouldBeTrue();
        envelope.Headers[EncryptionHeaders.KeyIdHeader].ShouldBe("k1");

        envelope.Headers.ContainsKey(EncryptionHeaders.InnerContentTypeHeader).ShouldBeTrue();
        envelope.Headers[EncryptionHeaders.InnerContentTypeHeader].ShouldBe(EnvelopeConstants.JsonContentType);

        bytes.Length.ShouldBeGreaterThan(12 + 16); // at least nonce + tag
    }

    [Fact]
    public async Task write_async_produces_unique_nonces_across_messages()
    {
        var sut = NewSut();
        var nonces = new HashSet<string>();

        for (var i = 0; i < 1000; i++)
        {
            var envelope = new Envelope { Message = new HelloMessage("x") };
            var bytes = await sut.WriteAsync(envelope);
            nonces.Add(Convert.ToHexString(bytes.AsSpan(0, 12)));
        }

        nonces.Count.ShouldBe(1000);
    }

    [Fact]
    public async Task write_async_ciphertext_is_not_plaintext_json()
    {
        var sut = NewSut();
        var envelope = new Envelope { Message = new HelloMessage("super-secret-string") };

        var bytes = await sut.WriteAsync(envelope);
        var dump = System.Text.Encoding.UTF8.GetString(bytes);

        dump.ShouldNotContain("super-secret-string");
    }

    [Fact]
    public async Task round_trip_through_system_text_json()
    {
        var sut = NewSut();

        var sendEnvelope = new Envelope { Message = new HelloMessage("hello") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var msg = await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope);

        msg.ShouldBeOfType<HelloMessage>().Greeting.ShouldBe("hello");
    }

    [Fact]
    public async Task round_trip_through_newtonsoft()
    {
        var newtonsoft = new NewtonsoftSerializer(NewtonsoftSerializer.DefaultSettings());
        var sut = NewSut(inner: newtonsoft);

        var sendEnvelope = new Envelope { Message = new HelloMessage("hi") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var msg = await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope);
        msg.ShouldBeOfType<HelloMessage>().Greeting.ShouldBe("hi");
    }

    [Fact]
    public async Task missing_key_id_header_throws_key_not_found()
    {
        var sut = NewSut();
        var sendEnvelope = new Envelope { Message = new HelloMessage("x") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope { Data = bytes, ContentType = EncryptionHeaders.EncryptedContentType };

        var ex = await Should.ThrowAsync<EncryptionKeyNotFoundException>(async () =>
            await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));

        ex.KeyId.ShouldBe("<missing>");
    }

    [Fact]
    public async Task unknown_key_id_throws_key_not_found_with_inner()
    {
        var sut = NewSut();
        var sendEnvelope = new Envelope { Message = new HelloMessage("x") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = "ghost-key",
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var ex = await Should.ThrowAsync<EncryptionKeyNotFoundException>(async () =>
            await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));

        ex.KeyId.ShouldBe("ghost-key");
        ex.InnerException.ShouldBeOfType<KeyNotFoundException>();
    }

    [Fact]
    public async Task tampered_ciphertext_byte_throws_decryption_exception()
    {
        var sut = NewSut();
        var sendEnvelope = new Envelope { Message = new HelloMessage("x") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        bytes[bytes.Length / 2] ^= 0xFF;

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(async () =>
            await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    [Fact]
    public async Task tampered_tag_byte_throws_decryption_exception()
    {
        var sut = NewSut();
        var sendEnvelope = new Envelope { Message = new HelloMessage("x") };
        var bytes = await sut.WriteAsync(sendEnvelope);

        bytes[bytes.Length - 1] ^= 0xFF;

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(async () =>
            await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    [Fact]
    public async Task body_shorter_than_28_bytes_throws_decryption_exception()
    {
        var sut = NewSut();
        var recvEnvelope = new Envelope
        {
            Data        = new byte[20],
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader] = "k1"
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(async () =>
            await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    [Fact]
    public void BuildAad_layout_matches_specified_byte_format()
    {
        // "wlv-enc-v1" || u16_be(len(MT)) || MT || u16_be(len(KeyId)) || KeyId || u16_be(len(ICT)) || ICT
        var aad = EncryptingMessageSerializer.BuildAad(
            messageType: "PaymentDetails",
            keyId: "k1",
            innerContentType: "application/json");

        var expected = new List<byte>();
        expected.AddRange(System.Text.Encoding.ASCII.GetBytes("wlv-enc-v1"));
        var mt = System.Text.Encoding.UTF8.GetBytes("PaymentDetails");
        expected.AddRange(new[] { (byte)(mt.Length >> 8), (byte)(mt.Length & 0xFF) });
        expected.AddRange(mt);
        var kid = System.Text.Encoding.UTF8.GetBytes("k1");
        expected.AddRange(new[] { (byte)(kid.Length >> 8), (byte)(kid.Length & 0xFF) });
        expected.AddRange(kid);
        var ict = System.Text.Encoding.UTF8.GetBytes("application/json");
        expected.AddRange(new[] { (byte)(ict.Length >> 8), (byte)(ict.Length & 0xFF) });
        expected.AddRange(ict);

        aad.ShouldBe(expected.ToArray());
    }

    [Fact]
    public void BuildAad_treats_null_message_type_as_empty()
    {
        var aad = EncryptingMessageSerializer.BuildAad(
            messageType: null, keyId: "k1", innerContentType: "application/json");
        var aadEmpty = EncryptingMessageSerializer.BuildAad(
            messageType: "", keyId: "k1", innerContentType: "application/json");
        aad.ShouldBe(aadEmpty);
    }

    [Fact]
    public async Task WriteAsync_uses_aad_with_messageType_keyId_innerContentType()
    {
        var key32 = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var provider = new InMemoryKeyProvider(
            defaultKeyId: "k1",
            keys: new Dictionary<string, byte[]> { ["k1"] = key32 });

        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        var sut   = new EncryptingMessageSerializer(inner, provider);

        var envelope = new Envelope(new EncryptedPayloadStub("hello"))
        {
            MessageType = typeof(EncryptedPayloadStub).ToMessageTypeName(),
            Headers     = new Dictionary<string, string?>()
        };

        var output = await sut.WriteAsync(envelope);

        var nonce      = output.AsSpan(0, 12).ToArray();
        var tag        = output.AsSpan(output.Length - 16, 16).ToArray();
        var ciphertext = output.AsSpan(12, output.Length - 12 - 16).ToArray();
        var plaintext  = new byte[ciphertext.Length];

        var aad = EncryptingMessageSerializer.BuildAad(
            typeof(EncryptedPayloadStub).ToMessageTypeName(), "k1", inner.ContentType);

        using var aes = new System.Security.Cryptography.AesGcm(key32, tagSizeInBytes: 16);
        Should.NotThrow(() => aes.Decrypt(nonce, ciphertext, tag, plaintext, aad));

        // Sanity: same input WITHOUT AAD should fail (proves AAD is bound).
        Should.Throw<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => aes.Decrypt(nonce, ciphertext, tag, new byte[ciphertext.Length]));
    }

    [Fact]
    public async Task ReadFromDataAsync_round_trips_when_aad_intact()
    {
        var (sut, envelopeOnWire) = await PrepareEncryptedEnvelopeAsync(
            messageType: typeof(EncryptedPayloadStub).ToMessageTypeName(), keyId: "k1");

        var result = await sut.ReadFromDataAsync(typeof(EncryptedPayloadStub), envelopeOnWire);
        result.ShouldBeOfType<EncryptedPayloadStub>().Secret.ShouldBe("hello");
    }

    [Fact]
    public async Task ReadFromDataAsync_throws_MessageDecryption_when_messageType_tampered()
    {
        var (sut, envelopeOnWire) = await PrepareEncryptedEnvelopeAsync(
            messageType: typeof(EncryptedPayloadStub).ToMessageTypeName(), keyId: "k1");

        envelopeOnWire.MessageType = "RefundIssued";

        await Should.ThrowAsync<MessageDecryptionException>(
            () => sut.ReadFromDataAsync(typeof(EncryptedPayloadStub), envelopeOnWire).AsTask());
    }

    [Fact]
    public async Task ReadFromDataAsync_throws_MessageDecryption_when_keyId_header_tampered()
    {
        // Two keys with identical bytes - proves AAD (not key lookup) catches the tamper.
        var bytes = Key32(0x42);
        var provider = new InMemoryKeyProvider(
            defaultKeyId: "k1",
            keys: new Dictionary<string, byte[]> { ["k1"] = bytes, ["k2"] = bytes });

        var (sut, envelopeOnWire) = await PrepareEncryptedEnvelopeAsync(
            messageType: typeof(EncryptedPayloadStub).ToMessageTypeName(), keyId: "k1", provider: provider);

        envelopeOnWire.Headers[EncryptionHeaders.KeyIdHeader] = "k2";

        await Should.ThrowAsync<MessageDecryptionException>(
            () => sut.ReadFromDataAsync(typeof(EncryptedPayloadStub), envelopeOnWire).AsTask());
    }

    [Fact]
    public async Task ReadFromDataAsync_throws_MessageDecryption_when_innerContentType_tampered()
    {
        var (sut, envelopeOnWire) = await PrepareEncryptedEnvelopeAsync(
            messageType: typeof(EncryptedPayloadStub).ToMessageTypeName(), keyId: "k1");

        envelopeOnWire.Headers[EncryptionHeaders.InnerContentTypeHeader] = "application/x-msgpack";

        await Should.ThrowAsync<MessageDecryptionException>(
            () => sut.ReadFromDataAsync(typeof(EncryptedPayloadStub), envelopeOnWire).AsTask());
    }

    private static async Task<(EncryptingMessageSerializer sut, Envelope envelopeOnWire)>
        PrepareEncryptedEnvelopeAsync(string messageType, string keyId, IKeyProvider? provider = null)
    {
        provider ??= new InMemoryKeyProvider(
            defaultKeyId: keyId,
            keys: new Dictionary<string, byte[]> { [keyId] = Key32(0x42) });

        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        var sut   = new EncryptingMessageSerializer(inner, provider);

        var sendEnvelope = new Envelope(new EncryptedPayloadStub("hello"))
        {
            MessageType = messageType,
            Headers     = new Dictionary<string, string?>()
        };

        var bytes = await sut.WriteAsync(sendEnvelope);

        var envelopeOnWire = new Envelope
        {
            Data        = bytes,
            ContentType = sut.ContentType,
            MessageType = sendEnvelope.MessageType,
            Headers     = new Dictionary<string, string?>(sendEnvelope.Headers)
        };

        return (sut, envelopeOnWire);
    }

    [Fact]
    public async Task round_trip_with_newtonsoft_sender_and_system_text_json_receiver()
    {
        // Cross-inner-serializer compatibility: AAD binds the SENDER's _inner.ContentType
        // ("application/json") into the auth tag. As long as the receiver's encrypting
        // serializer is also wrapping a JSON inner (whichever flavor), the AAD inputs
        // match (InnerContentTypeHeader is sent over the wire) and decryption succeeds.
        // The plaintext bytes happen to be valid JSON for both libraries, so the inner
        // dispatch on the receive side parses the same wire payload regardless of
        // sender/receiver inner choice.
        var key = Key32(0x42);
        var senderProvider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = key });
        var receiverProvider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = key });

        var senderInner = new NewtonsoftSerializer(NewtonsoftSerializer.DefaultSettings());
        var receiverInner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());

        var sender = new EncryptingMessageSerializer(senderInner, senderProvider);
        var receiver = new EncryptingMessageSerializer(receiverInner, receiverProvider);

        var sendEnvelope = new Envelope { Message = new HelloMessage("cross-flavor") };
        var bytes = await sender.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var msg = await receiver.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope);
        msg.ShouldBeOfType<HelloMessage>().Greeting.ShouldBe("cross-flavor");
    }

    [Fact]
    public async Task round_trip_with_system_text_json_sender_and_newtonsoft_receiver()
    {
        var key = Key32(0x42);
        var senderProvider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = key });
        var receiverProvider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = key });

        var senderInner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        var receiverInner = new NewtonsoftSerializer(NewtonsoftSerializer.DefaultSettings());

        var sender = new EncryptingMessageSerializer(senderInner, senderProvider);
        var receiver = new EncryptingMessageSerializer(receiverInner, receiverProvider);

        var sendEnvelope = new Envelope { Message = new HelloMessage("cross-flavor-reverse") };
        var bytes = await sender.WriteAsync(sendEnvelope);

        var recvEnvelope = new Envelope
        {
            Data        = bytes,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = sendEnvelope.Headers[EncryptionHeaders.KeyIdHeader],
                [EncryptionHeaders.InnerContentTypeHeader] = sendEnvelope.Headers[EncryptionHeaders.InnerContentTypeHeader]
            }
        };

        var msg = await receiver.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope);
        msg.ShouldBeOfType<HelloMessage>().Greeting.ShouldBe("cross-flavor-reverse");
    }

    [Fact]
    public async Task WriteAsync_wraps_wrong_key_length_from_custom_provider_in_EncryptionKeyNotFoundException()
    {
        // Custom IKeyProvider implementations that return a key that is not
        // exactly 32 bytes would otherwise surface as a raw CryptographicException
        // from the AesGcm constructor - at a call site outside the WriteAsync
        // try-catch, with no key-id information attached. The serializer must
        // wrap this into the same diagnostic shape as a missing/unknown key.
        var sut = new EncryptingMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            new ShortKeyProvider("k1"));

        var envelope = new Envelope { Message = new HelloMessage("x") };

        var ex = await Should.ThrowAsync<EncryptionKeyNotFoundException>(
            async () => await sut.WriteAsync(envelope));

        ex.KeyId.ShouldBe("k1");
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldContain("32 bytes");
        ex.InnerException!.Message.ShouldContain("16 bytes");
    }

    [Fact]
    public async Task ReadFromDataAsync_wraps_wrong_key_length_from_custom_provider_in_EncryptionKeyNotFoundException()
    {
        // Same hazard on the receive path: a provider that returns a wrong-sized
        // key for a key-id that resolved successfully must produce an
        // EncryptionKeyNotFoundException, not a raw CryptographicException
        // bubbling out of AesGcm's constructor.
        var sut = new EncryptingMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            new ShortKeyProvider("k1"));

        var recvEnvelope = new Envelope
        {
            Data        = new byte[40],
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader] = "k1"
            }
        };

        var ex = await Should.ThrowAsync<EncryptionKeyNotFoundException>(
            async () => await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));

        ex.KeyId.ShouldBe("k1");
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldContain("32 bytes");
    }

    private sealed class ShortKeyProvider : IKeyProvider
    {
        public ShortKeyProvider(string defaultKeyId) { DefaultKeyId = defaultKeyId; }
        public string DefaultKeyId { get; }
        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
            => ValueTask.FromResult(new byte[16]);
    }

    [Fact]
    public async Task WriteAsync_throws_when_provider_returns_null_default_key_id()
    {
        // A custom IKeyProvider that returns a null/empty DefaultKeyId would
        // otherwise crash with NullReferenceException inside BuildAad or
        // surface an opaque ArgumentNullException from the provider's lookup.
        // The serializer must reject this up front with a clear, key-id-aware
        // diagnostic.
        var sut = new EncryptingMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            new NullDefaultKeyIdProvider());

        var envelope = new Envelope { Message = new HelloMessage("x") };

        var ex = await Should.ThrowAsync<EncryptionKeyNotFoundException>(
            async () => await sut.WriteAsync(envelope));

        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldContain("DefaultKeyId");
    }

    private sealed class NullDefaultKeyIdProvider : IKeyProvider
    {
        public string DefaultKeyId => null!;
        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
            => ValueTask.FromResult(Enumerable.Repeat((byte)0x01, 32).ToArray());
    }

    [Fact]
    public async Task WriteAsync_propagates_OperationCanceledException_from_key_provider()
    {
        // The catch filter in WriteAsync intentionally excludes
        // OperationCanceledException so caller cancellation flows through
        // unchanged instead of being re-thrown as EncryptionKeyNotFound.
        // Lock that contract: a provider that throws OCE must surface OCE,
        // not a wrapped MessageEncryptionException.
        var sut = new EncryptingMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            new CancellingKeyProvider("k1"));

        var envelope = new Envelope { Message = new HelloMessage("x") };

        var ex = await Should.ThrowAsync<OperationCanceledException>(
            async () => await sut.WriteAsync(envelope));
        ex.ShouldNotBeAssignableTo<MessageEncryptionException>();
    }

    [Fact]
    public async Task ReadFromDataAsync_propagates_OperationCanceledException_from_key_provider()
    {
        var sut = new EncryptingMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            new CancellingKeyProvider("k1"));

        var recvEnvelope = new Envelope
        {
            Data        = new byte[40],
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader] = "k1"
            }
        };

        var ex = await Should.ThrowAsync<OperationCanceledException>(
            async () => await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
        ex.ShouldNotBeAssignableTo<MessageEncryptionException>();
    }

    private sealed class CancellingKeyProvider : IKeyProvider
    {
        public CancellingKeyProvider(string defaultKeyId) { DefaultKeyId = defaultKeyId; }
        public string DefaultKeyId { get; }
        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
            => throw new OperationCanceledException();
    }

    [Fact]
    public async Task ReadFromDataAsync_with_empty_envelope_data_throws_decryption_exception()
    {
        // Wolverine's Envelope itself rejects a null Data assignment with
        // WolverineSerializationException at the property setter, so the
        // serializer never sees null. The realistic boundary it does have to
        // defend against is an empty data buffer (a transport that produced
        // a zero-length frame): the body-length guard must produce a
        // MessageDecryptionException, not a downstream span-slicing crash.
        var sut = NewSut();

        var recvEnvelope = new Envelope
        {
            Data        = Array.Empty<byte>(),
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader] = "k1"
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(
            async () => await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    [Fact]
    public async Task ReadFromDataAsync_rejects_forged_plaintext_under_encrypted_content_type()
    {
        // Forgery scenario: a sender (or attacker) emits an envelope that
        // claims the encrypted content-type, supplies a plausible key-id and
        // inner-content-type header, but the body is actually plain JSON of
        // sufficient length to pass the 28-byte minimum. The auth tag check
        // must still reject it as MessageDecryptionException.
        var sut = NewSut();

        var forgedJson = System.Text.Encoding.UTF8.GetBytes(
            "{\"Greeting\":\"this-is-totally-not-encrypted-but-long-enough\"}");
        forgedJson.Length.ShouldBeGreaterThan(28);

        var recvEnvelope = new Envelope
        {
            Data        = forgedJson,
            ContentType = EncryptionHeaders.EncryptedContentType,
            MessageType = typeof(HelloMessage).ToMessageTypeName(),
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader]            = "k1",
                [EncryptionHeaders.InnerContentTypeHeader] = "application/json"
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(
            async () => await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    [Fact]
    public async Task ReadFromDataAsync_with_body_exactly_28_bytes_throws_decryption_exception()
    {
        // Boundary opposite to body_shorter_than_28_bytes: a 28-byte body is
        // the minimum the length guard accepts (12 nonce + 16 tag, zero
        // ciphertext). It then reaches AesGcm.Decrypt, where the tag check
        // fails for arbitrary input. Verifies the path past the length guard
        // still produces a MessageDecryptionException, not a raw crypto
        // exception.
        var sut = NewSut();

        var twentyEight = new byte[28];
        System.Security.Cryptography.RandomNumberGenerator.Fill(twentyEight);

        var recvEnvelope = new Envelope
        {
            Data        = twentyEight,
            ContentType = EncryptionHeaders.EncryptedContentType,
            Headers     =
            {
                [EncryptionHeaders.KeyIdHeader] = "k1"
            }
        };

        await Should.ThrowAsync<MessageDecryptionException>(
            async () => await sut.ReadFromDataAsync(typeof(HelloMessage), recvEnvelope));
    }

    private sealed record HelloMessage(string Greeting);
    private sealed record EncryptedPayloadStub(string Secret);
}
