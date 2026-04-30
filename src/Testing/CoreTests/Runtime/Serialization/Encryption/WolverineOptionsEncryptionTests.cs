using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class WolverineOptionsEncryptionTests
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static IKeyProvider NewProvider() =>
        new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) });

    [Fact]
    public async Task use_encryption_swaps_default_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseEncryption(NewProvider()))
            .StartAsync();

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        options.DefaultSerializer.ShouldBeOfType<EncryptingMessageSerializer>();
        options.DefaultSerializer.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
    }

    [Fact]
    public async Task use_encryption_keeps_inner_json_serializer_resolvable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseEncryption(NewProvider()))
            .StartAsync();

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        var json = options.TryFindSerializer(EnvelopeConstants.JsonContentType);
        json.ShouldNotBeNull();
        json.ShouldBeAssignableTo<IMessageSerializer>();
        json.ShouldNotBeAssignableTo<EncryptingMessageSerializer>();
    }

    [Fact]
    public void use_encryption_rejects_null_provider()
    {
        var opts = new WolverineOptions();
        Should.Throw<ArgumentNullException>(() => opts.UseEncryption(null!));
    }

    [Fact]
    public async Task per_type_encrypt_routes_only_matching_type_to_encrypting_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());
                opts.Policies.ForMessagesOfType<EncryptedTypeA>().Encrypt();

                opts.PublishAllMessages().ToLocalQueue("target");
                opts.LocalQueue("target");
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var sessionA = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new EncryptedTypeA("x")));
        var sessionB = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new PlainTypeB("x")));

        var sentEncrypted = sessionA.Sent.SingleEnvelope<EncryptedTypeA>();
        sentEncrypted.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sentEncrypted.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();

        var sentPlain = sessionB.Sent.SingleEnvelope<PlainTypeB>();
        sentPlain.ContentType.ShouldBe(EnvelopeConstants.JsonContentType);
        sentPlain.Serializer.ShouldNotBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public async Task endpoint_encrypted_routes_outgoing_through_encrypting_content_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());

                opts.PublishAllMessages().ToLocalQueue("encrypted-q").Encrypted();
                opts.LocalQueue("encrypted-q");
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new EncryptedTypeA("x")));

        var sent = session.Sent.SingleEnvelope<EncryptedTypeA>();
        sent.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sent.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public void encrypt_without_registered_serializer_throws()
    {
        var opts = new WolverineOptions();
        opts.UseSystemTextJsonForSerialization();

        Should.Throw<InvalidOperationException>(() =>
            opts.Policies.ForMessagesOfType<EncryptedTypeA>().Encrypt());
    }

    [Fact]
    public async Task endpoint_encrypted_without_registered_serializer_throws_on_send()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                // NOTE: deliberately NOT calling RegisterEncryptionSerializer here.
                opts.PublishAllMessages().ToLocalQueue("encrypted-q").Encrypted();
                opts.LocalQueue("encrypted-q");
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        // The throw surfaces inside the publish path because the rule's lazy lookup
        // fires on the first envelope.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await bus.PublishAsync(new EncryptedTypeA("x")));

        ex.Message.ShouldContain("No encrypting serializer is registered");
    }
}

public sealed record EncryptedTypeA(string Value);
public sealed record PlainTypeB(string Value);
