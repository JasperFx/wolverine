using System.Security.Cryptography;
using EncryptionDemo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Serialization.Encryption;

var key = RandomNumberGenerator.GetBytes(32);

using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Plain JSON globally
        opts.UseSystemTextJsonForSerialization();

        // Register the encrypting serializer alongside (without making it the default)
        opts.RegisterEncryptionSerializer(new InMemoryKeyProvider(
            "demo-key",
            new Dictionary<string, byte[]> { ["demo-key"] = key }));

        // Encrypt only the sensitive message type
        opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();

        opts.PublishAllMessages().ToLocalQueue("demo-queue");
        opts.LocalQueue("demo-queue");
    })
    .StartAsync();

var bus = host.Services.GetRequiredService<IMessageBus>();

await bus.PublishAsync(new PaymentDetails("4111-1111-1111-1111", 99.99m));   // encrypted
await bus.PublishAsync(new OrderShipped(Guid.NewGuid()));                     // plain JSON

await Task.Delay(2000);
