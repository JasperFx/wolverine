using System.Security.Cryptography;
using FaultEventsDemo;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Serialization.Encryption;

// Single-process demo: shows the Fault<T> configuration surface
// (PublishFaultEvents + per-type override + Encrypt() ↔ Fault<T> pairing)
// without standing up a separate sender and receiver. Local queues are
// in-memory pass-through, so the byte-level encryption step for
// PaymentDetails is configured but not observable on the wire here. For a
// real two-process encryption round-trip see the acceptance tests under
// src/Testing/CoreTests/Acceptance/encryption_acceptance.cs. In production,
// replace the LocalQueue endpoint below with a real transport (TCP / Rabbit
// / Service Bus / Kafka) so the encrypted bytes actually leave the process.

var key = RandomNumberGenerator.GetBytes(32);

using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // 1. Global opt-in. Defaults: include exception message + stack trace.
        opts.PublishFaultEvents();

        // 2. Per-type opt-out for a chatty/non-actionable type.
        opts.Policies.ForMessagesOfType<HighVolumeChatter>().DoNotPublishFault();

        // 3. Per-type encryption pairing. Encrypt() automatically pairs
        //    Fault<PaymentDetails> with the same encrypting serializer +
        //    receive-side requirement.
        opts.UseSystemTextJsonForSerialization();
        opts.RegisterEncryptionSerializer(new InMemoryKeyProvider(
            "demo-key",
            new Dictionary<string, byte[]> { ["demo-key"] = key }));
        opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();

        // 4. Make the first InvalidOperationException terminal so the demo
        //    does not have to wait for retry exhaustion. The DLQ move
        //    triggers Fault<OrderPlaced> auto-publish.
        opts.Policies.OnException<InvalidOperationException>().MoveToErrorQueue();

        // No explicit routing: Wolverine's implicit local routing invokes
        // the in-process handler for every type with a registered handler,
        // including the auto-published Fault<OrderPlaced>.
    })
    .StartAsync();

var bus = host.MessageBus();

await bus.PublishAsync(new OrderPlaced(Guid.NewGuid(), "SKU-1"));         // → Fault<OrderPlaced>
await bus.PublishAsync(new PaymentDetails("4111-1111-1111-1111", 99.99m)); // succeeds, encrypted
await bus.PublishAsync(new HighVolumeChatter(42));                         // → no fault (opted out)

await Task.Delay(2000);
