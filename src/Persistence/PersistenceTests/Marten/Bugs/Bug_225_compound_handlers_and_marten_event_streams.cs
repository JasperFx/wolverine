using IntegrationTests;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten.Bugs;

public class Bug_225_compound_handlers_and_marten_event_streams : PostgresqlContext
{
    [Fact]
    public async Task should_apply_transaction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();
            })
            .UseWolverine(opts => { opts.Policies.AutoApplyTransactions(); })
            .StartAsync();

        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new StoreSomething2(id));

        using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var stream = await session.Events.FetchStreamAsync(id);

        stream.ShouldNotBeEmpty();
    }
}

public record StoreSomething2(Guid Id);

public record Something(Guid Id)
{
    public static Something Create(StoreSomething2 ev)
    {
        return new(ev.Id);
    }
}

// Works.
// public class StoreSomething2SimpleHandler
// {
//   public static async Task Handle(StoreSomething2 command, IDocumentSession session)
//   {
//     var stream = await session.Events.FetchForWriting<Something>(id: command.Id);
//     stream.AppendOne(command);
//   }
// }

// Works.
// public class StoreSomething2CompoundWithDependencyOnDocumentSessionHandler
// {
//   public static async
//     Task<IEventStream<Something>>
//     LoadAsync(StoreSomething2 command,
//               IDocumentSession session,
//               CancellationToken ct)
//     => await session.Events.FetchForWriting<Something>(id: command.Id, cancellation: ct);
//
//   public static void Handle(StoreSomething2 command, IEventStream<Something> stream, IDocumentSession session)
//   {
//     stream.AppendOne(command);
//   }
// }

// Broken
public class StoreSomething2CompoundHandler
{
    public static async
        Task<IEventStream<Something>>
        LoadAsync(StoreSomething2 command,
            IDocumentSession session,
            CancellationToken ct)
    {
        return await session.Events.FetchForWriting<Something>(command.Id, ct);
    }

    public static void Handle(StoreSomething2 command, IEventStream<Something> stream)
    {
        stream.AppendOne(command);
    }
}