using System.Data;
using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Marten.Services;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

/// <summary>
/// GH-5570. Marten 10 (marten#5907) only opens a Serializable session that owns its connection asynchronously, and
/// Wolverine opens a handler's session synchronously. A session factory that asks for Serializable fails when the
/// handler's session is opened, with a message that says what to do instead.
/// </summary>
public class serializable_session_factory_5570 : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SerializableSessionHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "serializable_sessions";
                    })
                    .BuildSessionsWith<SerializableSessionFactory>()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_serializable_session_factory_fails_with_the_remedy()
    {
        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            theHost.MessageBus().InvokeAsync(new WriteInSerializableSession(Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(SerializableSessionFactory));
        ex.Message.ShouldContain("LightweightSerializableSessionAsync()");
        ex.Message.ShouldContain("IMartenOutbox.Enroll()");
        ex.InnerException.ShouldBeOfType<NotSupportedException>();
    }
}

public class SerializableSessionFactory(IDocumentStore store) : SessionFactoryBase(store)
{
    public override SessionOptions BuildOptions()
    {
        return new SessionOptions { IsolationLevel = IsolationLevel.Serializable };
    }
}

public record WriteInSerializableSession(Guid Id);

public static class SerializableSessionHandler
{
    public static void Handle(WriteInSerializableSession command, IDocumentSession session)
    {
        session.Store(new SerializableSessionDoc { Id = command.Id });
    }
}

public class SerializableSessionDoc
{
    public Guid Id { get; set; }
}
