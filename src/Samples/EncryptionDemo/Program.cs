using System.Security.Cryptography;
using EncryptionDemo;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Serialization.Encryption;

// Single-process demo: shows the configuration surface (per-type Encrypt + per-listener
// RequireEncryption) without standing up a separate sender and receiver. Local queues
// are in-memory pass-through, so the byte-level encrypt/decrypt step is not actually
// exercised here — for that, see the two-host acceptance tests in
// src/Testing/CoreTests/Acceptance/encryption_acceptance.cs. In production, replace the
// LocalQueue endpoints below with a real transport (TCP / Rabbit / Service Bus / Kafka)
// so the encrypted bytes actually leave the process.

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

        opts.PublishMessage<PaymentDetails>().ToLocalQueue("payments");
        opts.PublishMessage<OrderShipped>().ToLocalQueue("orders");

        // Receive-side enforcement: the "payments" listener accepts ONLY
        // encrypted envelopes. A plain-JSON envelope addressed to this queue
        // is routed to the dead-letter queue with EncryptionPolicyViolationException
        // before any serializer runs, so a misconfigured sender (or a forged
        // envelope) cannot deliver plaintext to a payment handler.
        opts.LocalQueue("payments").RequireEncryption();

        // The "orders" queue is left unmarked so non-sensitive types still
        // flow during a rolling deploy.
        opts.LocalQueue("orders");
    })
    .StartAsync();

var bus = host.MessageBus();

await bus.PublishAsync(new PaymentDetails("4111-1111-1111-1111", 99.99m));   // encrypted
await bus.PublishAsync(new OrderShipped(Guid.NewGuid()));                     // plain JSON

await Task.Delay(2000);
