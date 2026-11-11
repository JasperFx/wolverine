using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Marten;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace MartenTests;

/// <summary>
/// The control for <c>PolecatTests.enrolled_outbox_flushes_on_commit</c> and its Fisher twin: Marten adds a listener
/// to a session it did not open, so its enrolled sessions have always flushed on commit. The durability agent is
/// off, so the only thing that can deliver a message is the flush.
/// </summary>
public class enrolled_outbox_flushes_on_commit : IAsyncLifetime
{
    private const string SchemaName = "enrolled_outbox";

    private static readonly TimeSpan DeliveryTimeout = 15.Seconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var port = PortFinder.GetAvailablePort();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EnrolledOutboxHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.ListenAtPort(port).UseDurableInbox();
                opts.PublishMessage<EnrolledDurableExternal>().ToPort(port).UseDurableOutbox();
                opts.PublishMessage<EnrolledDurableLocal>().ToLocalQueue("enrolled-durable").UseDurableInbox();
                opts.PublishMessage<EnrolledBufferedLocal>().ToLocalQueue("enrolled-buffered").BufferedInMemory();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task publishThroughAnEnrolledSessionAsync(object message)
    {
        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        outbox.Enroll(session);

        session.Store(new EnrolledOutboxDoc { Id = Guid.NewGuid() });
        await outbox.PublishAsync(message);

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task a_durable_message_to_an_external_endpoint_goes_out_on_commit()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledDurableExternal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_durable_local_message_goes_out_on_commit()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledDurableLocal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_buffered_message_goes_out_on_commit_rather_than_being_lost()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledBufferedLocal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_session_opened_from_the_store_rather_than_the_container_flushes_too()
    {
        var id = Guid.NewGuid();

        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        outbox.Enroll(session);

        session.Store(new EnrolledOutboxDoc { Id = Guid.NewGuid() });
        await outbox.PublishAsync(new EnrolledBufferedLocal(id));

        await session.SaveChangesAsync(Token);

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }
}

public class EnrolledOutboxDoc
{
    public Guid Id { get; set; }
}

public record EnrolledDurableExternal(Guid Id);

public record EnrolledDurableLocal(Guid Id);

public record EnrolledBufferedLocal(Guid Id);

public static class EnrolledOutboxHandler
{
    public static void Handle(EnrolledDurableExternal message) => EnrolledDeliveries.Received(message.Id);

    public static void Handle(EnrolledDurableLocal message) => EnrolledDeliveries.Received(message.Id);

    public static void Handle(EnrolledBufferedLocal message) => EnrolledDeliveries.Received(message.Id);
}

public static class EnrolledDeliveries
{
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _deliveries = new();

    private static TaskCompletionSource deliveryOf(Guid id)
    {
        return _deliveries.GetOrAdd(id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public static void Received(Guid id) => deliveryOf(id).TrySetResult();

    public static Task WaitFor(Guid id, TimeSpan timeout) => deliveryOf(id).Task.WaitAsync(timeout);

    public static bool WasReceived(Guid id) => deliveryOf(id).Task.IsCompleted;
}
